// Media detection: webRequest.onResponseStarted collects streams and media files per tab; the popup hands them to the app.
// Read-only observer — never blocking, never reading bodies, never touching incognito tabs.
(function () {
  'use strict';
  var W = self.WPC;
  var api = W.api;
  var noop = W.noop;
  var KEY = 'wpcMedia';
  var MAX_PER_TAB = 50;
  var MIN_MEDIA_BYTES = 65536;          // a video/audio response smaller than this is an ad bumper or a probe, not a file
  var TYPES = ['media', 'xmlhttprequest', 'other', 'object'];
  var ADD_TIMEOUT_MS = 20000;

  // Sites whose pages yt-dlp understands better than any single response we could catch.
  var YTDLP_SITES = ['youtube.com', 'youtu.be', 'youtube-nocookie.com', 'vimeo.com', 'dailymotion.com', 'rutube.ru',
    'vk.com', 'vkvideo.ru', 'ok.ru', 'twitch.tv', 'soundcloud.com', 'bilibili.com', 'facebook.com', 'instagram.com',
    'tiktok.com', 'twitter.com', 'x.com', 'coub.com', 'yandex.ru', 'dzen.ru'];

  function lower(s) {
    return String(s || '').toLowerCase();
  }

  function pathOf(url) {
    try { return new URL(url).pathname.toLowerCase(); } catch (e) { return ''; }
  }

  function hostOf(url) {
    try { return new URL(url).hostname.toLowerCase(); } catch (e) { return ''; }
  }

  // Same URL without the query: CDN links differ only by a signature, and a playlist is requested again and again.
  function dedupeKey(url) {
    try {
      var u = new URL(url);
      return u.origin + u.pathname;
    } catch (e) {
      return String(url || '');
    }
  }

  function isHttp(url) {
    return /^https?:\/\//i.test(String(url || ''));
  }

  function headerOf(headers, name) {
    for (var i = 0; headers && i < headers.length; i++) {
      if (lower(headers[i].name) === name) return String(headers[i].value || '');
    }
    return '';
  }

  // Chunks of a stream are not offers: the playlist above them is.
  function isSegment(path, type) {
    return /\.(ts|m4s|aac|m4f|cmfv|cmfa)$/.test(path) || type === 'video/mp2t' || type === 'video/iso.segment'
      || type === 'audio/iso.segment';
  }

  // "" = not media. Length is what the header promised, -1 when unknown.
  function detect(url, contentType, length) {
    if (!isHttp(url)) return '';
    var path = pathOf(url);
    var type = lower(contentType).split(';')[0].trim();
    if (isSegment(path, type)) return '';
    if (type === 'application/vnd.apple.mpegurl' || type === 'application/x-mpegurl' || type === 'audio/mpegurl'
      || type === 'audio/x-mpegurl' || /\.m3u8$/.test(path)) return 'hls';
    if (type === 'application/dash+xml' || /\.mpd$/.test(path)) return 'dash';
    if (type.indexOf('video/') === 0 || type.indexOf('audio/') === 0) {
      if (type === 'video/vnd.mpeg.dash.mpd') return 'dash';
      if (length >= 0 && length < MIN_MEDIA_BYTES) return '';
      return 'file';
    }
    return '';
  }

  function offerYtdlp(pageUrl) {
    var host = hostOf(pageUrl);
    if (!host) return false;
    for (var i = 0; i < YTDLP_SITES.length; i++) {
      var s = YTDLP_SITES[i];
      if (host === s || host.length > s.length && host.slice(-(s.length + 1)) === '.' + s) return true;
    }
    return false;
  }

  // ---- per-tab store (survives a service worker restart via storage.session) ----
  var chain = Promise.resolve();
  var incognitoTabs = {};

  function store() {
    return api.storage && api.storage.session ? api.storage.session : null;
  }

  function load() {
    var s = store();
    if (!s) return Promise.resolve({});
    return W.call(s, 'get', KEY).then(function (o) {
      return (o && o[KEY]) || {};
    }, function () { return {}; });
  }

  function mutate(fn) {
    chain = chain.then(function () {
      return load().then(function (all) {
        var out = fn(all);
        if (out === false) return null;
        var o = {};
        o[KEY] = all;
        var s = store();
        return s ? W.call(s, 'set', o) : null;
      });
    }).catch(function (e) { W.log('media store failed', e && e.message); });
    return chain;
  }

  function tabOf(all, tabId) {
    var key = String(tabId);
    if (!all[key]) all[key] = { page: '', items: [] };
    return all[key];
  }

  function badge(tabId, count) {
    if (!api.action || tabId < 0) return;
    W.call(api.action, 'setBadgeBackgroundColor', { color: '#2d7d46', tabId: tabId }).catch(noop);
    W.call(api.action, 'setBadgeText', { text: count > 0 ? String(count) : '', tabId: tabId }).catch(noop);
  }

  function isIncognito(details) {
    if (typeof details.incognito === 'boolean') return Promise.resolve(details.incognito);
    var key = String(details.tabId);
    if (incognitoTabs[key] !== undefined) return Promise.resolve(incognitoTabs[key]);
    if (!api.tabs || details.tabId < 0) return Promise.resolve(false);
    return W.call(api.tabs, 'get', details.tabId).then(function (tab) {
      incognitoTabs[key] = !!(tab && tab.incognito);
      return incognitoTabs[key];
    }, function () { return false; });
  }

  function remember(details, kind, type, length) {
    return mutate(function (all) {
      var tab = tabOf(all, details.tabId);
      var page = details.documentUrl || details.originUrl || details.initiator || '';
      if (!tab.page && isHttp(page)) tab.page = page;
      var key = dedupeKey(details.url);
      for (var i = 0; i < tab.items.length; i++) {
        if (tab.items[i].key === key) {
          tab.items[i].url = details.url;                  // freshest signature wins
          tab.items[i].at = Date.now();
          return true;
        }
      }
      tab.items.push({
        key: key,
        url: details.url,
        kind: kind,
        type: type,
        bytes: length,
        referrer: isHttp(page) ? page : '',
        at: Date.now()
      });
      while (tab.items.length > MAX_PER_TAB) tab.items.shift();
      return true;
    }).then(function () {
      return load();
    }).then(function (all) {
      var tab = all[String(details.tabId)];
      badge(details.tabId, tab ? tab.items.length : 0);
    });
  }

  function forget(tabId) {
    delete incognitoTabs[String(tabId)];
    return mutate(function (all) {
      if (!all[String(tabId)]) return false;
      delete all[String(tabId)];
      return true;
    }).then(function () { badge(tabId, 0); });
  }

  function startPage(tabId, url) {
    delete incognitoTabs[String(tabId)];
    return mutate(function (all) {
      all[String(tabId)] = { page: isHttp(url) ? url : '', items: [] };
      return true;
    }).then(function () { badge(tabId, 0); });
  }

  function onResponse(details) {
    if (!details || details.tabId === undefined || details.tabId < 0 || !isHttp(details.url)) return;
    var type = headerOf(details.responseHeaders, 'content-type');
    var len = parseInt(headerOf(details.responseHeaders, 'content-length'), 10);
    if (!isFinite(len) || len < 0) len = -1;
    var kind = detect(details.url, type, len);
    if (!kind) return;
    isIncognito(details).then(function (secret) {
      if (secret) return null;
      return remember(details, kind, lower(type).split(';')[0].trim(), len);
    }).catch(noop);
  }

  function onNavigate(details) {
    if (!details || details.type !== 'main_frame' || details.tabId === undefined || details.tabId < 0) return;
    startPage(details.tabId, details.url).catch(noop);
  }

  // ---- popup commands ----
  function listFor(tabId, pageUrl) {
    return load().then(function (all) {
      var tab = all[String(tabId)] || { page: '', items: [] };
      var page = isHttp(pageUrl) ? pageUrl : tab.page;
      var items = [];
      for (var i = tab.items.length - 1; i >= 0; i--) {
        var it = tab.items[i];
        items.push({ url: it.url, kind: it.kind, type: it.type, bytes: it.bytes });
      }
      return { ok: true, items: items, page: page, ytdlp: isHttp(page) && (items.length === 0 || offerYtdlp(page)) };
    });
  }

  function add(req) {
    var url = String(req.url || '');
    var kind = req.kind === 'page' || req.kind === 'hls' || req.kind === 'dash' ? req.kind : 'file';
    if (!isHttp(url)) return Promise.resolve({ ok: false, error: { code: 'bad_request', msg: 'http/https only' } });
    return load().then(function (all) {
      var tab = all[String(req.tabId)] || { page: '', items: [] };
      var page = isHttp(req.pageUrl) ? req.pageUrl : tab.page;
      var found = null;
      for (var i = 0; i < tab.items.length; i++) if (tab.items[i].url === url) found = tab.items[i];
      return W.bridge.request({
        type: 'addMedia',
        url: url,
        kind: kind,
        pageUrl: page,
        referrer: (found && found.referrer) || page,
        title: String(req.title || '').slice(0, 300),
        contentType: (found && found.type) || ''
      }, { timeoutMs: ADD_TIMEOUT_MS, kick: true });
    }).then(function (reply) {
      return { ok: true, reply: reply };
    });
  }

  // null = not ours; background.js keeps its own answer for unknown commands.
  function command(req) {
    if (!req) return null;
    if (req.cmd === 'media') return listFor(req.tabId, req.url);
    if (req.cmd === 'addMedia') return add(req);
    return null;
  }

  if (api.webRequest && api.webRequest.onResponseStarted) {
    api.webRequest.onResponseStarted.addListener(onResponse, { urls: ['<all_urls>'], types: TYPES }, ['responseHeaders']);
    api.webRequest.onBeforeRequest.addListener(onNavigate, { urls: ['<all_urls>'], types: ['main_frame'] });
    if (api.tabs && api.tabs.onRemoved) api.tabs.onRemoved.addListener(function (tabId) { forget(tabId).catch(noop); });
  }

  self.WpcMedia = {
    command: command,
    detect: detect,
    dedupeKey: dedupeKey,
    offerYtdlp: offerYtdlp,
    onResponse: onResponse,
    onNavigate: onNavigate,
    forget: forget,
    list: listFor,
    MAX_PER_TAB: MAX_PER_TAB,
    MIN_MEDIA_BYTES: MIN_MEDIA_BYTES
  };
})();
