// Popup: connection state, master switch, per-site skip, live status (≤ 1 poll/s while open), open app.
(function () {
  'use strict';
  var isFirefox = typeof browser !== 'undefined' && !!browser.runtime &&
    typeof browser.runtime.getBrowserInfo === 'function';
  var api = isFirefox ? browser : chrome;
  var R = WpcRules;
  var ALL_URLS = { origins: ['<all_urls>'] };
  var POLL_MS = 1000;

  var snap = null;
  var host = '';
  var busy = false;
  var tab = null;                      // active tab: id, url, title — the media list is per tab
  var MEDIA_SHOWN = 12;

  function $(id) { return document.getElementById(id); }

  function t(key, subs) {
    return api.i18n.getMessage(key, subs) || '';
  }

  function send(msg) {
    return new Promise(function (resolve) {
      var done = function (resp) {
        resolve(resp || { ok: false, error: { code: 'no_response' } });
      };
      if (isFirefox) {
        api.runtime.sendMessage(msg).then(done, function (e) {
          done({ ok: false, error: { code: 'internal', msg: e && e.message } });
        });
        return;
      }
      chrome.runtime.sendMessage(msg, function (resp) {
        if (chrome.runtime.lastError) done({ ok: false, error: { code: 'internal', msg: chrome.runtime.lastError.message } });
        else done(resp);
      });
    });
  }

  function activeTab() {
    return new Promise(function (resolve) {
      var q = { active: true, currentWindow: true };
      var pick = function (tabs) { resolve(tabs && tabs[0] ? tabs[0] : null); };
      try {
        if (isFirefox) api.tabs.query(q).then(pick, function () { resolve(null); });
        else chrome.tabs.query(q, function (tabs) { if (chrome.runtime.lastError) resolve(null); else pick(tabs); });
      } catch (e) {
        resolve(null);
      }
    });
  }

  function localize() {
    try { document.documentElement.lang = api.i18n.getUILanguage(); } catch (e) { /* keep default */ }
    var nodes = document.querySelectorAll('[data-i18n]');
    for (var i = 0; i < nodes.length; i++) {
      var text = t(nodes[i].getAttribute('data-i18n'));
      if (text) nodes[i].textContent = text;
    }
  }

  function showError(text) {
    var el = $('err');
    el.textContent = text || '';
    el.hidden = !text;
  }

  function errorText(resp) {
    if (resp && resp.ok && resp.reply && resp.reply.ok === false) return t('errRejected');
    var code = resp && resp.error ? resp.error.code : '';
    if (code === 'timeout') return t('errNoAnswer');
    if (code === 'disconnected') return t('errNoConnection');
    return t('errFailed');
  }

  function connKind(s) {
    if (!s) return 'connecting';
    if (s.state === 'ready') return 'ready';
    if (s.state === 'connecting' || s.state === 'idle') return 'connecting';
    return R.connectionKind(s.lastError);
  }

  var CONN_TEXT = { ready: 'connReady', connecting: 'connConnecting', missing: 'connMissing', forbidden: 'connForbidden', lost: 'connLost' };
  var HINT_TEXT = { missing: 'hintMissing', forbidden: 'hintForbidden', lost: 'hintLost' };

  function render() {
    var kind = connKind(snap);
    var ready = kind === 'ready';
    var rules = snap && snap.rules;

    $('conn').setAttribute('data-state', kind);
    $('connText').textContent = t(CONN_TEXT[kind]);
    var hint = HINT_TEXT[kind];
    $('hint').textContent = hint ? t(hint) : '';
    $('hint').hidden = !hint;

    var enabled = $('enabled');
    enabled.checked = !!(rules && rules.enabled);
    enabled.disabled = !ready || busy;

    var siteRow = $('siteRow');
    siteRow.hidden = !host;
    if (host) {
      var match = rules ? R.hostMatches(host, rules.skipHosts) : '';
      var byWildcard = !!match && match.toLowerCase() !== host;
      var skip = $('skipSite');
      skip.checked = !!match;
      skip.disabled = !ready || busy || byWildcard;
      $('siteHost').textContent = byWildcard ? t('skipSiteByRule', [match]) : host;
    }

    $('stats').hidden = !ready;
    $('agent').hidden = !ready;
    $('openApp').disabled = !ready || busy;
  }

  function renderStatus(reply) {
    if (!reply || reply.ok !== true) return;
    $('statActive').textContent = String(reply.active || 0);
    $('statQueued').textContent = String(reply.queued || 0);
    var rate = R.formatRate(reply.speedBps);
    var units = ['unitBps', 'unitKBps', 'unitMBps', 'unitGBps'];
    $('statSpeed').textContent = rate[0] + ' ' + t(units[rate[1]]);
    $('agent').textContent = t(reply.agentRunning ? 'agentRunning' : 'agentStopped');
  }

  // ---- media found on the page ----
  function kindText(kind) {
    if (kind === 'hls') return t('mediaKindHls');
    if (kind === 'dash') return t('mediaKindDash');
    return t('mediaKindFile');
  }

  function sizeText(bytes) {
    if (!(bytes > 0)) return '';
    if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + ' ' + t('unitMB');
    return Math.round(bytes / 1024) + ' ' + t('unitKB');
  }

  function shortUrl(url) {
    try {
      var u = new URL(url);
      var name = u.pathname.split('/').pop() || u.hostname;
      return u.hostname + '/' + (name.length > 40 ? name.slice(0, 40) + '…' : name);
    } catch (e) {
      return String(url).slice(0, 60);
    }
  }

  function addMedia(url, kind, btn) {
    btn.disabled = true;
    showError('');
    send({
      cmd: 'addMedia', url: url, kind: kind,
      tabId: tab ? tab.id : -1, pageUrl: (tab && tab.url) || '', title: (tab && tab.title) || ''
    }).then(function (resp) {
      if (resp && resp.ok && (!resp.reply || resp.reply.ok !== false)) {
        btn.textContent = t('mediaAdded');
        return;
      }
      btn.disabled = false;
      showError(errorText(resp));
    });
  }

  function mediaRow(item) {
    var li = document.createElement('li');
    li.className = 'media-item';
    var text = document.createElement('div');
    text.className = 'media-text';
    var head = document.createElement('span');
    var size = sizeText(item.bytes);
    head.className = 'media-kind';
    head.textContent = kindText(item.kind) + (size ? ' · ' + size : '');
    var sub = document.createElement('small');
    sub.className = 'sub';
    sub.textContent = shortUrl(item.url);
    sub.title = item.url;
    text.appendChild(head);
    text.appendChild(sub);
    var btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn small';
    btn.textContent = t('mediaDownload');
    btn.addEventListener('click', function () { addMedia(item.url, item.kind, btn); });
    li.appendChild(text);
    li.appendChild(btn);
    return li;
  }

  function renderMedia(data) {
    var items = (data && data.items) || [];
    var list = $('mediaList');
    list.textContent = '';
    for (var i = 0; i < items.length && i < MEDIA_SHOWN; i++) list.appendChild(mediaRow(items[i]));
    $('mediaEmpty').hidden = items.length > 0;
    $('mediaYtdlp').hidden = !(data && data.ytdlp);
    $('mediaBox').hidden = !(items.length || (data && data.ytdlp));
  }

  function refreshMedia() {
    if (!tab || typeof tab.id !== 'number') { $('mediaBox').hidden = true; return; }
    send({ cmd: 'media', tabId: tab.id, url: tab.url || '' }).then(function (resp) {
      if (resp && resp.ok) renderMedia(resp);
      else $('mediaBox').hidden = true;
    });
  }

  function tick() {
    var ready = snap && snap.state === 'ready';
    var p = ready ? send({ cmd: 'status' }) : send({ cmd: 'state' });
    p.then(function (resp) {
      if (resp && resp.state) snap = resp.state;
      if (ready && resp && resp.ok) renderStatus(resp.reply);
      render();
    }).then(function () {
      setTimeout(tick, POLL_MS);
    });
  }

  function command(msg, onDone) {
    busy = true;
    showError('');
    render();
    send(msg).then(function (resp) {
      busy = false;
      if (resp && resp.state) snap = resp.state;
      if (!resp || !resp.ok || (resp.reply && resp.reply.ok === false)) showError(errorText(resp));
      else if (onDone) onDone(resp);
      render();
    });
  }

  function checkPermission() {
    if (!isFirefox || !api.permissions) return;
    api.permissions.contains(ALL_URLS).then(function (has) {
      $('permBox').hidden = !!has;
    }, function () { /* keep hidden */ });
  }

  $('enabled').addEventListener('change', function (e) {
    command({ cmd: 'setEnabled', on: e.target.checked });
  });

  $('skipSite').addEventListener('change', function (e) {
    if (host) command({ cmd: 'skipHost', host: host, on: e.target.checked });
  });

  $('openApp').addEventListener('click', function () {
    command({ cmd: 'openApp' }, function () { window.close(); });
  });

  $('permBtn').addEventListener('click', function () {
    // Must run inside the click (user gesture).
    api.permissions.request(ALL_URLS).then(checkPermission, checkPermission);
  });

  $('mediaYtdlp').addEventListener('click', function (e) {
    if (tab && tab.url) addMedia(tab.url, 'page', e.target);
  });

  localize();
  checkPermission();
  Promise.all([send({ cmd: 'state', kick: true }), activeTab()]).then(function (res) {
    if (res[0] && res[0].state) snap = res[0].state;
    tab = res[1];
    host = tab ? R.hostOf(tab.url || '') : '';
    render();
    refreshMedia();
    setTimeout(tick, snap && snap.state === 'ready' ? 0 : POLL_MS);
  });
})();
