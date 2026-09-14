// Firefox interception: no onDeterminingFilename, so offer from downloads.onCreated while the transfer runs.
(function () {
  'use strict';
  var W = self.WPC;
  var R = WpcRules;
  var api = W.api;
  var noop = W.noop;
  var OFFER_TIMEOUT_MS = 60000;

  // Watches live in memory (event page); lost on background restart.
  var watches = {};

  function findItem(id) {
    return api.downloads.search({ id: id }).then(function (list) {
      return list && list.length ? list[0] : null;
    });
  }

  function reportWatch(id) {
    var entry = watches[id];
    if (!entry || entry.sending) return Promise.resolve();
    entry.sending = true;
    return findItem(id).then(function (item) {
      if (!item || item.state === 'interrupted') { delete watches[id]; return null; }
      if (item.state !== 'complete') { entry.sending = false; return null; }
      return W.bridge.request({
        type: 'browserFile',
        reqId: entry.reqId,
        path: item.filename,
        url: entry.url || item.url,
        finalUrl: entry.finalUrl || item.url,
        mime: item.mime || entry.mime,
        totalBytes: typeof item.totalBytes === 'number' && item.totalBytes >= 0 ? item.totalBytes :
          (typeof item.fileSize === 'number' && item.fileSize >= 0 ? item.fileSize : entry.totalBytes)
      }, { timeoutMs: 30000, kick: true }).then(function (reply) {
        delete watches[id];
        if (!reply.ok || !reply.removeBrowserCopy) return null;
        return api.downloads.removeFile(id).then(function () {
          return api.downloads.erase({ id: id });
        }).catch(function (e) { W.log('remove browser copy failed', e && e.message); });
      });
    }).catch(function (e) {
      if (watches[id]) watches[id].sending = false;
      W.log('browserFile deferred', id, e && (e.code || e.message));
    });
  }

  function watch(item, reqId) {
    watches[item.id] = {
      reqId: reqId,
      url: item.url || '',
      finalUrl: item.url || '',
      mime: item.mime || '',
      totalBytes: typeof item.totalBytes === 'number' ? item.totalBytes : -1,
      sending: false
    };
    return reportWatch(item.id);
  }

  api.downloads.onChanged.addListener(function (delta) {
    if (!delta || !delta.state || !watches[delta.id]) return;
    if (delta.state.current === 'interrupted') delete watches[delta.id];
    else if (delta.state.current === 'complete') reportWatch(delta.id);
  });

  W.bridge.on('state', function (s) {
    if (s === 'ready') Object.keys(watches).forEach(function (id) { reportWatch(Number(id)); });
  });

  function abandon(reqId) {
    return W.bridge.request({ type: 'abandon', reqId: reqId }, { timeoutMs: 10000 }).catch(noop);
  }

  function restore(item) {
    return W.downloadInBrowser(item.url, R.baseName(item.filename || '')).catch(function (e) {
      W.log('restore failed', e && e.message);
    });
  }

  // cancel + removeFile (if a partial exists) + erase, then commit. A finished file stays with the browser.
  function accept(item, reqId) {
    return findItem(item.id).then(function (cur) {
      if (!cur || cur.state === 'complete') return abandon(reqId);
      return api.downloads.cancel(item.id).then(function () {
        return findItem(item.id);
      }, function (e) {
        W.log('cancel failed', item.id, e && e.message);
        return findItem(item.id).then(function (after) {
          return after && after.state === 'interrupted' ? after : Promise.reject(e);
        });
      }).then(function (after) {
        var drop = after && after.exists ? api.downloads.removeFile(item.id).catch(noop) : Promise.resolve();
        return drop.then(function () {
          return api.downloads.erase({ id: item.id }).catch(function (e) {
            W.log('erase failed', item.id, e && e.message);
          });
        });
      }).then(function () {
        return W.bridge.request({ type: 'commit', reqId: reqId }, { timeoutMs: 15000 }).then(function (reply) {
          if (!reply.ok) { W.log('commit refused', reply.error && reply.error.code); return restore(item); }
          return null;
        }, function (e) {
          W.log('commit failed', e && (e.code || e.message));
          return restore(item);
        });
      }, function () {
        return abandon(reqId);
      });
    });
  }

  api.downloads.onCreated.addListener(function (item) {
    if (item.state && item.state !== 'in_progress') return;
    var rules = W.bridge.rules;
    var verdict = R.shouldOffer({
      url: item.url,
      finalUrl: item.url,
      referrer: item.referrer,
      filename: item.filename,
      incognito: item.incognito,
      byExtension: W.isOwnDownload(item)
    }, rules);
    if (!verdict.offer) return;

    var holdMs = (rules || R.normalizeRules(null)).holdMs;
    var late = false;
    var timer = setTimeout(function () { late = true; }, holdMs);
    var reqId = W.bridge.nextReqId();

    W.cookiesForDownload(item).catch(function () { return ''; }).then(function (cookies) {
      if (late) return null;
      var msg = R.buildOffer(item, { cookies: cookies, ua: W.ua, byExtension: false });
      msg.reqId = reqId;
      return W.bridge.request(msg, { timeoutMs: OFFER_TIMEOUT_MS, kick: true });
    }).then(function (reply) {
      clearTimeout(timer);
      if (!reply) return null;
      var action = reply.ok === true ? reply.action : '';
      if (action === 'accept') return late ? abandon(reqId) : accept(item, reqId);
      if (action === 'watch') return watch(item, reqId);
      return null; // decline: leave the browser download running
    }).catch(function (e) {
      clearTimeout(timer);
      W.log('offer failed', e && (e.code || e.message));
    });
  });
})();
