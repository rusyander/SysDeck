// Pure interception logic: no browser APIs. Loads via importScripts/<script> (global WpcRules) or require().
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.WpcRules = api;
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var MAX_URLS = 500;
  var HOLD_DEFAULT = 5000;
  var HOLD_MIN = 500;
  // Browser takes over after ~15 s (Edge 153 probe); stay well below.
  var HOLD_MAX = 12000;

  var DEFAULT_RULES = {
    enabled: true,
    incognito: false,
    skipHosts: [],
    skipExt: [],
    catchAll: true,
    holdMs: HOLD_DEFAULT
  };

  function strList(v, lower) {
    var out = [];
    if (!Array.isArray(v)) return out;
    for (var i = 0; i < v.length; i++) {
      if (typeof v[i] !== 'string') continue;
      var s = v[i].trim();
      if (lower) s = s.toLowerCase();
      if (s) out.push(s);
    }
    return out;
  }

  // Host rules → safe defaults; holdMs clamped under the browser cap.
  function normalizeRules(r) {
    r = r && typeof r === 'object' ? r : {};
    var hold = typeof r.holdMs === 'number' && isFinite(r.holdMs) ? r.holdMs : HOLD_DEFAULT;
    if (hold < HOLD_MIN) hold = HOLD_MIN;
    if (hold > HOLD_MAX) hold = HOLD_MAX;
    return {
      enabled: r.enabled !== false,
      incognito: r.incognito === true,
      skipHosts: strList(r.skipHosts, true),
      skipExt: strList(r.skipExt, true).map(function (e) { return e.replace(/^\.+/, ''); }),
      catchAll: r.catchAll !== false,
      holdMs: hold
    };
  }

  function parseUrl(u) {
    if (typeof u !== 'string' || !u) return null;
    try { return new URL(u); } catch (e) { return null; }
  }

  function isHttpUrl(u) {
    var p = parseUrl(u);
    return !!p && (p.protocol === 'http:' || p.protocol === 'https:');
  }

  function hostOf(u) {
    var p = parseUrl(u);
    if (!p || (p.protocol !== 'http:' && p.protocol !== 'https:')) return '';
    return p.hostname.toLowerCase().replace(/\.$/, '');
  }

  // Pattern: exact host, or "*.domain" = domain itself and any subdomain.
  function hostMatches(host, patterns) {
    if (!host || !patterns) return '';
    host = String(host).toLowerCase().replace(/\.$/, '');
    for (var i = 0; i < patterns.length; i++) {
      var p = String(patterns[i]).toLowerCase().trim().replace(/\.$/, '');
      if (!p) continue;
      if (p.indexOf('*.') === 0) {
        var base = p.slice(2);
        if (base && (host === base || host.slice(-(base.length + 1)) === '.' + base)) return patterns[i];
      } else if (host === p) {
        return patterns[i];
      }
    }
    return '';
  }

  // Last segment of a Windows/POSIX file path (URLs are parsed with URL, not here).
  function baseName(name) {
    if (typeof name !== 'string') return '';
    var i = Math.max(name.lastIndexOf('/'), name.lastIndexOf('\\'));
    return i >= 0 ? name.slice(i + 1) : name;
  }

  function extOfName(name) {
    var b = baseName(name);
    var i = b.lastIndexOf('.');
    if (i <= 0 || i === b.length - 1) return '';
    return b.slice(i + 1).toLowerCase();
  }

  // Extension from the browser's filename, else from the URL path.
  function extOf(filename, url) {
    var e = extOfName(filename);
    if (e) return e;
    var p = parseUrl(url);
    if (!p) return '';
    var path = p.pathname;
    try { path = decodeURIComponent(path); } catch (err) { /* keep raw */ }
    return extOfName(path);
  }

  // Decide whether a download is worth a round trip. rules may be null (not known yet): only hard filters apply.
  function shouldOffer(d, rules) {
    d = d || {};
    if (d.byExtension) return { offer: false, reason: 'byExtension' };
    var target = d.finalUrl || d.url;
    if (!isHttpUrl(d.url) || !isHttpUrl(target)) return { offer: false, reason: 'scheme' };
    if (!rules) return { offer: true, reason: '' };
    if (!rules.enabled) return { offer: false, reason: 'disabled' };
    if (d.incognito && !rules.incognito) return { offer: false, reason: 'incognito' };
    var hosts = [hostOf(d.url), hostOf(d.finalUrl), hostOf(d.referrer), hostOf(d.pageUrl)];
    for (var i = 0; i < hosts.length; i++) {
      if (hosts[i] && hostMatches(hosts[i], rules.skipHosts)) return { offer: false, reason: 'skipHost' };
    }
    var ext = extOf(d.filename, target);
    if (ext && rules.skipExt.indexOf(ext) >= 0) return { offer: false, reason: 'skipExt' };
    return { offer: true, reason: '' };
  }

  function numBytes(v) {
    return typeof v === 'number' && isFinite(v) && v >= 0 ? Math.floor(v) : -1;
  }

  // DownloadItem-like → offer message body (reqId added by the bridge).
  function buildOffer(item, extra) {
    item = item || {};
    extra = extra || {};
    var finalUrl = item.finalUrl || item.url || '';
    return {
      type: 'offer',
      url: item.url || '',
      finalUrl: finalUrl,
      referrer: item.referrer || '',
      pageUrl: extra.pageUrl || item.referrer || '',
      filename: baseName(item.filename || ''),
      mime: item.mime || '',
      totalBytes: numBytes(item.totalBytes),
      cookies: typeof extra.cookies === 'string' ? extra.cookies : '',
      ua: extra.ua || '',
      incognito: !!item.incognito,
      byExtension: !!extra.byExtension
    };
  }

  // Cookie objects (possibly from several getAll calls) → "n=v; n2=v2"; longer paths first, duplicates dropped.
  function joinCookies(lists) {
    var all = [];
    var seen = {};
    for (var i = 0; i < (lists || []).length; i++) {
      var list = lists[i] || [];
      for (var j = 0; j < list.length; j++) {
        var c = list[j];
        if (!c || typeof c.name !== 'string') continue;
        var key = c.name + '\u0001' + (c.domain || '') + '\u0001' + (c.path || '') + '\u0001' +
          JSON.stringify(c.partitionKey || null);
        if (seen[key]) continue;
        seen[key] = true;
        all.push({ c: c, order: all.length });
      }
    }
    all.sort(function (a, b) {
      var d = (b.c.path || '').length - (a.c.path || '').length;
      return d !== 0 ? d : a.order - b.order;
    });
    var pairs = [];
    var pairSeen = {};
    for (var k = 0; k < all.length; k++) {
      var pair = all[k].c.name ? all[k].c.name + '=' + (all[k].c.value || '') : (all[k].c.value || '');
      if (!pair || pairSeen[pair]) continue;
      pairSeen[pair] = true;
      pairs.push(pair);
    }
    return pairs.join('; ');
  }

  // http/https only, fragment dropped, first occurrence kept, at most max (≤ 500).
  function dedupUrls(urls, max) {
    var limit = typeof max === 'number' && max > 0 ? Math.min(max, MAX_URLS) : MAX_URLS;
    var out = [];
    var seen = {};
    for (var i = 0; i < (urls || []).length && out.length < limit; i++) {
      var p = parseUrl(typeof urls[i] === 'string' ? urls[i].trim() : '');
      if (!p || (p.protocol !== 'http:' && p.protocol !== 'https:')) continue;
      p.hash = '';
      var s = p.href;
      if (seen[s]) continue;
      seen[s] = true;
      out.push(s);
    }
    return out;
  }

  // Plain-text URLs inside a selection.
  function extractUrlsFromText(text) {
    if (typeof text !== 'string' || !text) return [];
    var m = text.match(/https?:\/\/[^\s<>"'`]+/gi) || [];
    return m.map(function (u) { return u.replace(/[),.;:!?\]]+$/, ''); });
  }

  var RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$/i;

  // Basename safe for downloads.download({filename}); '' = let the browser choose.
  function sanitizeFilename(name) {
    var b = baseName(typeof name === 'string' ? name : '');
    b = b.replace(/[\u0000-\u001f\u007f<>:"\/\\|?*]/g, '_').replace(/^[\s.]+|[\s.]+$/g, '');
    if (!b || /^_+$/.test(b)) return '';
    if (RESERVED.test(b)) b = '_' + b;
    if (b.length > 180) {
      var dot = b.lastIndexOf('.');
      var ext = dot > 0 && b.length - dot <= 16 ? b.slice(dot) : '';
      b = b.slice(0, 180 - ext.length) + ext;
    }
    return b;
  }

  // "chrome"|"edge"|"yandex"|"firefox"|"chromium" from userAgentData.brands + UA fallback.
  function detectBrowser(brands, ua) {
    var names = [];
    for (var i = 0; i < (brands || []).length; i++) {
      if (brands[i] && typeof brands[i].brand === 'string') names.push(brands[i].brand);
    }
    ua = typeof ua === 'string' ? ua : '';
    if (names.indexOf('Microsoft Edge') >= 0 || /\bEdg[AE]?\//.test(ua)) return 'edge';
    if (names.indexOf('YaBrowser') >= 0 || names.indexOf('Yandex') >= 0 || /\bYaBrowser\//.test(ua)) return 'yandex';
    if (/\bFirefox\//.test(ua) && !/\bSeamonkey\//i.test(ua)) return 'firefox';
    if (names.indexOf('Google Chrome') >= 0) return 'chrome';
    return 'chromium';
  }

  // Reconnect delay: 1, 2, 4 … capped at 30 s. attempt starts at 0.
  function backoffMs(attempt) {
    var n = typeof attempt === 'number' && attempt > 0 ? Math.floor(attempt) : 0;
    if (n > 5) return 30000;
    return Math.min(30000, 1000 * Math.pow(2, n));
  }

  // Bytes/s → [number text, unit index 0..3 = B, KB, MB, GB].
  function formatRate(bps) {
    var v = typeof bps === 'number' && isFinite(bps) && bps > 0 ? bps : 0;
    var unit = 0;
    while (v >= 1024 && unit < 3) { v /= 1024; unit += 1; }
    var text = unit === 0 || v >= 100 ? String(Math.round(v)) : v.toFixed(1);
    return [text, unit];
  }

  // Error text from the native port → popup state key.
  function connectionKind(lastError) {
    var s = String(lastError || '');
    if (/not found|No such native application|not registered/i.test(s)) return 'missing';
    if (/forbidden|not_allowed/i.test(s)) return 'forbidden';
    return 'lost';
  }

  return {
    formatRate: formatRate,
    connectionKind: connectionKind,
    MAX_URLS: MAX_URLS,
    HOLD_MAX: HOLD_MAX,
    DEFAULT_RULES: DEFAULT_RULES,
    normalizeRules: normalizeRules,
    isHttpUrl: isHttpUrl,
    hostOf: hostOf,
    hostMatches: hostMatches,
    baseName: baseName,
    extOf: extOf,
    shouldOffer: shouldOffer,
    buildOffer: buildOffer,
    joinCookies: joinCookies,
    dedupUrls: dedupUrls,
    extractUrlsFromText: extractUrlsFromText,
    sanitizeFilename: sanitizeFilename,
    detectBrowser: detectBrowser,
    backoffMs: backoffMs
  };
});
