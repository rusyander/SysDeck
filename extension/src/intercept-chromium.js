// Chromium interception: onDeterminingFilename hold ≤ rules.holdMs, then accept/decline/watch.
(function () {
  'use strict';
  var W = self.WPC;
  var R = WpcRules;
  var api = W.api;
  var noop = W.noop;
  var WATCH_KEY = 'wpcWatches';
  var OFFER_TIMEOUT_MS = 60000;

  // ---- watches (survive SW restart via storage.session) ----
  var watchChain = Promise.resolve();
  var sending = {};

  function watchStore() {
    return chrome.storage && chrome.storage.session ? chrome.storage.session : null;
  }

  function loadWatches() {
    var store = watchStore();
    if (!store) return Promise.resolve({});
    return W.call(store, 'get', WATCH_KEY).then(function (o) {
      return (o && o[WATCH_KEY]) || {};
    }, function () { return {}; });
  }

  function mutateWatches(fn) {
    watchChain = watchChain.then(function () {
      return loadWatches().then(function (w) {
        fn(w);
        var o = {};
        o[WATCH_KEY] = w;
        var store = watchStore();
        return store ? W.call(store, 'set', o) : null;
      });
    }).catch(function (e) { W.log('watch store failed', e && e.message); });
    return watchChain;
  }

  function addWatch(item, reqId) {
    return mutateWatches(function (w) {
      w[String(item.id)] = {
        reqId: reqId,
        url: item.url || '',
        finalUrl: item.finalUrl || item.url || '',
        mime: item.mime || '',
        totalBytes: typeof item.totalBytes === 'number' ? item.totalBytes : -1
      };
    });
  }

  // Store, then report at once if the item already finished (small files).
  function watch(item, reqId) {
    return addWatch(item, reqId).then(function () {
      return loadWatches();
    }).then(function (w) {
      var entry = w[String(item.id)];
      return entry ? reportWatch(item.id, entry) : null;
    });
  }

  function removeWatch(id) {
    return mutateWatches(function (w) { delete w[String(id)]; });
  }

  function findItem(id) {
    return W.call(api.downloads, 'search', { id: id }).then(function (list) {
      return list && list.length ? list[0] : null;
    });
  }

  // Watched download finished in the browser → browserFile; host may ask to drop the browser copy.
  function reportWatch(id, entry) {
    var key = String(id);
    if (sending[key]) return Promise.resolve();
    sending[key] = true;
    // Re-read after pending store writes: a parallel report may have closed this watch already.
    var closed = false;
    return watchChain.then(loadWatches).then(function (w) {
      if (!w[key]) { closed = true; return null; }
      entry = w[key];
      return findItem(id);
    }).then(function (item) {
      if (closed) return null;
      if (!item) return removeWatch(id);
      if (item.state === 'interrupted') return removeWatch(id);
      if (item.state !== 'complete') return null;
      var total = typeof item.totalBytes === 'number' && item.totalBytes >= 0 ? item.totalBytes :
        (typeof item.fileSize === 'number' && item.fileSize >= 0 ? item.fileSize : entry.totalBytes);
      return W.bridge.request({
        type: 'browserFile',
        reqId: entry.reqId,
        path: item.filename,
        url: entry.url || item.url,
        finalUrl: item.finalUrl || entry.finalUrl,
        mime: item.mime || entry.mime,
        totalBytes: typeof total === 'number' ? total : -1
      }, { timeoutMs: 30000, kick: true }).then(function (reply) {
        return removeWatch(id).then(function () {
          if (!reply.ok || !reply.removeBrowserCopy) return null;
          return W.call(api.downloads, 'removeFile', id).then(function () {
            return W.call(api.downloads, 'erase', { id: id });
          }).catch(function (e) { W.log('remove browser copy failed', e && e.message); });
        });
      });
      // on bridge failure the watch stays and is retried on the next "ready"
    }).catch(function (e) {
      W.log('browserFile deferred', id, e && (e.code || e.message));
    }).then(function () { delete sending[key]; });
  }

  api.downloads.onChanged.addListener(function (delta) {
    if (!delta || !delta.state) return;
    var st = delta.state.current;
    if (st !== 'complete' && st !== 'interrupted') return;
    loadWatches().then(function (w) {
      var entry = w[String(delta.id)];
      if (!entry) return null;
      if (st === 'interrupted') return removeWatch(delta.id);
      return reportWatch(delta.id, entry);
    }).catch(noop);
  });

  W.bridge.on('state', function (s) {
    if (s !== 'ready') return;
    loadWatches().then(function (w) {
      Object.keys(w).forEach(function (id) { reportWatch(Number(id), w[id]); });
    }).catch(noop);
  });

  // ---- accept path ----
  function abandon(reqId) {
    return W.bridge.request({ type: 'abandon', reqId: reqId }, { timeoutMs: 10000 }).catch(noop);
  }

  // Host lost between cancel and commit: give the user the file back through the browser.
  function restore(item) {
    return W.downloadInBrowser(item.finalUrl || item.url, R.baseName(item.filename || '')).catch(function (e) {
      W.log('restore failed', e && e.message);
    });
  }

  function accept(item, reqId, suggest) {
    return W.call(api.downloads, 'cancel', item.id).then(function () {
      return W.call(api.downloads, 'erase', { id: item.id }).catch(function (e) {
        W.log('erase failed', item.id, e && e.message);
      }).then(function () {
        return W.bridge.request({ type: 'commit', reqId: reqId }, { timeoutMs: 15000 }).then(function (reply) {
          if (!reply.ok) { W.log('commit refused', reply.error && reply.error.code); return restore(item); }
          return null;
        }, function (e) {
          W.log('commit failed', e && (e.code || e.message));
          return restore(item);
        });
      });
    }, function (e) {
      // Could not stop the browser transfer: leave it to the browser.
      W.log('cancel failed', item.id, e && e.message);
      try { suggest(); } catch (x) { /* already decided */ }
      return abandon(reqId);
    });
  }

  chrome.downloads.onDeterminingFilename.addListener(function (item, suggest) {
    var rules = W.bridge.rules;
    var verdict = R.shouldOffer({
      url: item.url,
      finalUrl: item.finalUrl,
      referrer: item.referrer,
      filename: item.filename,
      incognito: item.incognito,
      byExtension: W.isOwnDownload(item)
    }, rules);
    if (!verdict.offer) {
      suggest();
      return;
    }

    var holdMs = (rules || R.normalizeRules(null)).holdMs;
    var settled = false;
    var timer = null;
    var reqId = W.bridge.nextReqId();

    function release() {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try { suggest(); } catch (e) { /* browser already moved on */ }
    }

    timer = setTimeout(function () {
      if (settled) return;
      W.log('hold expired', item.id);
      release();
    }, holdMs);

    W.cookiesForDownload(item).catch(function () { return ''; }).then(function (cookies) {
      if (settled) return null;
      var msg = R.buildOffer(item, { cookies: cookies, ua: W.ua, byExtension: false });
      msg.reqId = reqId;
      return W.bridge.request(msg, { timeoutMs: OFFER_TIMEOUT_MS, kick: true });
    }).then(function (reply) {
      if (!reply) return null;
      var action = reply.ok === true ? reply.action : '';
      if (settled) {
        // Late answer: the browser owns the transfer now.
        if (action === 'accept') return abandon(reqId);
        if (action === 'watch') return watch(item, reqId);
        return null;
      }
      if (action === 'accept') {
        settled = true;
        clearTimeout(timer);
        return accept(item, reqId, suggest);
      }
      if (action === 'watch') {
        release();
        return watch(item, reqId);
      }
      if (reply.ok !== true) W.log('offer refused', reply.error && reply.error.code);
      release();
      return null;
    }).catch(function (e) {
      W.log('offer failed', e && (e.code || e.message));
      release();
    });

    return true;
  });
})();
