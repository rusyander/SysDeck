// Background entry: Chromium service worker (imports the rest) or Firefox background script (manifest lists files).
'use strict';

if (typeof importScripts === 'function' && typeof WpcRules === 'undefined') {
  importScripts('rules.js', 'bridge.js');
}

var WPC = (function () {
  var isFirefox = typeof browser !== 'undefined' && !!browser.runtime &&
    typeof browser.runtime.getBrowserInfo === 'function';
  var api = isFirefox ? browser : chrome;
  var R = WpcRules;

  function log() {
    var args = Array.prototype.slice.call(arguments);
    args.unshift('[wpc]');
    console.log.apply(console, args);
  }

  function noop() {}

  // Uniform promise call: callbacks + lastError on Chromium, native promises on Firefox.
  function call(obj, name) {
    var args = Array.prototype.slice.call(arguments, 2);
    return new Promise(function (resolve, reject) {
      var fn = obj && obj[name];
      if (typeof fn !== 'function') { reject(new Error('API missing: ' + name)); return; }
      try {
        if (isFirefox) {
          Promise.resolve(fn.apply(obj, args)).then(resolve, reject);
          return;
        }
        fn.apply(obj, args.concat([function (res) {
          var err = chrome.runtime.lastError;
          if (err) reject(new Error(err.message || String(err)));
          else resolve(res);
        }]));
      } catch (e) {
        reject(e);
      }
    });
  }

  var ua = typeof navigator !== 'undefined' ? navigator.userAgent : '';
  var brands = typeof navigator !== 'undefined' && navigator.userAgentData ? navigator.userAgentData.brands : null;
  var browserName = isFirefox ? 'firefox' : R.detectBrowser(brands, ua);
  var extVersion = api.runtime.getManifest().version;

  // Downloads this extension started itself (giveBack, restore): never offered again.
  var ownIds = {};
  var ownUrls = {};
  var OWN_URL_TTL = 120000;

  function markOwnUrl(url) {
    if (url) ownUrls[url] = Date.now() + OWN_URL_TTL;
  }

  function markOwnId(id) {
    if (typeof id === 'number') ownIds[id] = true;
  }

  function consumeOwnUrl(url) {
    if (!url || !ownUrls[url]) return false;
    var alive = ownUrls[url] > Date.now();
    delete ownUrls[url];
    return alive;
  }

  function isOwnDownload(item) {
    if (!item) return false;
    if (item.byExtensionId && item.byExtensionId === api.runtime.id) return true;
    if (ownIds[item.id]) return true;
    return consumeOwnUrl(item.url) || consumeOwnUrl(item.finalUrl);
  }

  function incognitoAllowed() {
    return call(api.extension, 'isAllowedIncognitoAccess').then(function (v) { return !!v; }, function () { return false; });
  }

  // Cookie header for one URL from the given store; partitioned cookies merged when supported.
  function readCookies(url, storeId) {
    if (!R.isHttpUrl(url) || !api.cookies) return Promise.resolve('');
    var base = { url: url };
    if (storeId) base.storeId = storeId;
    function withKeys(extra) {
      var q = {};
      for (var k in base) q[k] = base[k];
      for (var e in extra) q[e] = extra[e];
      return q;
    }
    var plain = isFirefox
      ? call(api.cookies, 'getAll', withKeys({ firstPartyDomain: null })).catch(function () {
        return call(api.cookies, 'getAll', base);
      })
      : call(api.cookies, 'getAll', base);
    var partitioned = call(api.cookies, 'getAll', withKeys(isFirefox ? { firstPartyDomain: null, partitionKey: {} } : { partitionKey: {} }))
      .catch(function () { return []; });
    return Promise.all([plain.catch(function () { return []; }), partitioned]).then(function (lists) {
      return R.joinCookies(lists);
    });
  }

  function storeIdForDownload(item) {
    if (isFirefox) return item.cookieStoreId || (item.incognito ? 'firefox-private' : 'firefox-default');
    return item.incognito ? '1' : '0';
  }

  function cookiesForDownload(item) {
    var url = item.finalUrl || item.url;
    return readCookies(url, storeIdForDownload(item)).catch(function () {
      return readCookies(url, undefined);
    });
  }

  function storeIdForTab(tab) {
    if (!tab) return Promise.resolve(undefined);
    if (tab.cookieStoreId) return Promise.resolve(tab.cookieStoreId);
    return call(api.cookies, 'getAllCookieStores').then(function (stores) {
      for (var i = 0; i < (stores || []).length; i++) {
        if ((stores[i].tabIds || []).indexOf(tab.id) >= 0) return stores[i].id;
      }
      return tab.incognito ? '1' : '0';
    }, function () { return undefined; });
  }

  // Browser re-download (giveBack push, failed commit): marked as ours first.
  function downloadInBrowser(url, filename) {
    var opts = { url: url, saveAs: false };
    var safe = R.sanitizeFilename(filename || '');
    if (safe) opts.filename = safe;
    markOwnUrl(url);
    return call(api.downloads, 'download', opts).then(function (id) {
      markOwnId(id);
      return id;
    });
  }

  function onPush(msg) {
    if (msg.type === 'needCookies') {
      return readCookies(msg.url, undefined).then(function (cookies) {
        return { cookies: cookies };
      }, function () { return { cookies: '' }; });
    }
    if (msg.type === 'giveBack') {
      if (!R.isHttpUrl(msg.url)) return { ok: false, error: { code: 'bad_request', msg: 'http/https only' } };
      return downloadInBrowser(msg.url, msg.filename).then(function () {
        return { ok: true };
      }, function (e) {
        return { ok: false, error: { code: 'internal', msg: e && e.message ? e.message : String(e) } };
      });
    }
    log('unknown push', msg.type);
    return null;
  }

  var bridge = new WpcBridge({
    connect: function (name) { return api.runtime.connectNative(name); },
    lastError: function (port) {
      if (isFirefox) return port && port.error ? String(port.error.message || port.error) : '';
      var e = chrome.runtime.lastError;
      return e ? String(e.message || e) : '';
    },
    hello: function () {
      return incognitoAllowed().then(function (allowed) {
        return { browser: browserName, extVersion: extVersion, incognitoAllowed: allowed };
      });
    },
    onPush: onPush,
    log: log
  });

  // Toolbar badge for menu feedback.
  var badgeTimer = null;
  function badge(text, color, title) {
    var action = api.action;
    if (!action) return;
    if (badgeTimer) clearTimeout(badgeTimer);
    call(action, 'setBadgeBackgroundColor', { color: color }).catch(noop);
    call(action, 'setBadgeText', { text: text }).catch(noop);
    if (title) call(action, 'setTitle', { title: title }).catch(noop);
    badgeTimer = setTimeout(function () {
      badgeTimer = null;
      call(action, 'setBadgeText', { text: '' }).catch(noop);
      call(action, 'setTitle', { title: api.i18n.getMessage('actionTitle') }).catch(noop);
    }, 5000);
  }

  function errorOf(e) {
    return { code: (e && e.code) || 'internal', msg: e && e.message ? e.message : String(e) };
  }

  // Popup → background commands.
  function onPopupCommand(req) {
    var snap = function () {
      var s = bridge.snapshot();
      s.browser = browserName;
      s.isFirefox = isFirefox;
      return s;
    };
    switch (req && req.cmd) {
      case 'state':
        if (req.kick) bridge.kick();
        return Promise.resolve({ ok: true, state: snap() });
      case 'status':
        return bridge.request({ type: 'status' }, { timeoutMs: 3000 }).then(function (reply) {
          return { ok: true, reply: reply, state: snap() };
        });
      case 'setEnabled':
        return bridge.request({ type: 'setEnabled', on: !!req.on }, { timeoutMs: 5000, kick: true }).then(function (reply) {
          if (reply.ok && reply.rules) bridge.setRules(reply.rules);
          return { ok: true, reply: reply, state: snap() };
        });
      case 'skipHost':
        return bridge.request({ type: 'skipHost', host: String(req.host || ''), on: !!req.on }, { timeoutMs: 5000, kick: true })
          .then(function (reply) {
            if (reply.ok && reply.rules) bridge.setRules(reply.rules);
            return { ok: true, reply: reply, state: snap() };
          });
      case 'openApp':
        return bridge.request({ type: 'openApp', page: 'downloads' }, { timeoutMs: 15000, kick: true }).then(function (reply) {
          return { ok: true, reply: reply, state: snap() };
        });
      default:
        // Media commands live in media.js; it answers null for anything it does not own.
        var media = self.WpcMedia && self.WpcMedia.command ? self.WpcMedia.command(req) : null;
        if (media) return media;
        return Promise.resolve({ ok: false, error: { code: 'bad_request', msg: 'unknown command' } });
    }
  }

  api.runtime.onMessage.addListener(function (req, sender, sendResponse) {
    if (!sender || sender.id !== api.runtime.id) return false;
    onPopupCommand(req).then(sendResponse, function (e) {
      sendResponse({ ok: false, error: errorOf(e), state: bridge.snapshot() });
    });
    return true;
  });

  return {
    api: api,
    isFirefox: isFirefox,
    call: call,
    log: log,
    noop: noop,
    ua: ua,
    browserName: browserName,
    bridge: bridge,
    isOwnDownload: isOwnDownload,
    markOwnUrl: markOwnUrl,
    markOwnId: markOwnId,
    readCookies: readCookies,
    cookiesForDownload: cookiesForDownload,
    storeIdForTab: storeIdForTab,
    downloadInBrowser: downloadInBrowser,
    badge: badge
  };
})();

self.WPC = WPC;

if (typeof importScripts === 'function') {
  importScripts('intercept-chromium.js', 'menu.js', 'media.js');
}

WPC.log('background up', WPC.browserName, 'ext', WPC.api.runtime.getManifest().version);
WPC.bridge.start();
