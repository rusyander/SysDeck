// Context menu: link, image, all links on the page, selected links → add{urls}.
(function () {
  'use strict';
  var W = self.WPC;
  var R = WpcRules;
  var api = W.api;
  var menus = api.contextMenus || api.menus;
  if (!menus) return;

  var IDS = {
    link: 'wpc-link',
    image: 'wpc-image',
    page: 'wpc-page-links',
    selection: 'wpc-selected-links'
  };
  var COOKIE_CONCURRENCY = 8;

  function t(key) {
    return api.i18n.getMessage(key) || key;
  }

  function createMenus() {
    W.call(menus, 'removeAll').catch(W.noop).then(function () {
      var items = [
        { id: IDS.link, title: t('menuLink'), contexts: ['link'] },
        { id: IDS.image, title: t('menuImage'), contexts: ['image'] },
        { id: IDS.page, title: t('menuPage'), contexts: ['page'] },
        { id: IDS.selection, title: t('menuSelection'), contexts: ['selection'] }
      ];
      items.forEach(function (props) {
        W.call(menus, 'create', props).catch(function (e) { W.log('menu create failed', props.id, e && e.message); });
      });
    });
  }

  api.runtime.onInstalled.addListener(createMenus);
  api.runtime.onStartup.addListener(createMenus);

  // Injected into the clicked frame (activeTab granted by the menu click). Must be self-contained.
  function collectPageLinks() {
    var out = [];
    var nodes = document.querySelectorAll('a[href], area[href]');
    for (var i = 0; i < nodes.length && out.length < 5000; i++) out.push(String(nodes[i].href));
    return { links: out, text: '' };
  }

  function collectSelectedLinks() {
    var out = [];
    var sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return { links: out, text: '' };
    var nodes = document.querySelectorAll('a[href], area[href]');
    for (var i = 0; i < nodes.length && out.length < 5000; i++) {
      if (sel.containsNode(nodes[i], true)) out.push(String(nodes[i].href));
    }
    for (var r = 0; r < sel.rangeCount; r++) {
      var n = sel.getRangeAt(r).commonAncestorContainer;
      while (n && n.nodeType !== 1) n = n.parentNode;
      var a = n && n.closest ? n.closest('a[href]') : null;
      if (a) out.push(String(a.href));
    }
    return { links: out, text: String(sel).slice(0, 200000) };
  }

  function runInFrame(tab, frameId, func) {
    if (!tab || typeof tab.id !== 'number' || tab.id < 0) return Promise.reject(new Error('no tab'));
    var target = { tabId: tab.id };
    if (typeof frameId === 'number' && frameId > 0) target.frameIds = [frameId];
    return W.call(api.scripting, 'executeScript', { target: target, func: func }).then(function (results) {
      var r = results && results[0] ? results[0].result : null;
      return r || { links: [], text: '' };
    });
  }

  function collectUrls(info, tab) {
    switch (info.menuItemId) {
      case IDS.link: return Promise.resolve([info.linkUrl]);
      case IDS.image: return Promise.resolve([info.srcUrl]);
      case IDS.page:
        return runInFrame(tab, info.frameId, collectPageLinks).then(function (r) { return r.links; });
      case IDS.selection:
        return runInFrame(tab, info.frameId, collectSelectedLinks).then(function (r) {
          return r.links.concat(R.extractUrlsFromText(r.text || ''));
        }, function () {
          // Page not scriptable: fall back to URLs typed in the selected text.
          return R.extractUrlsFromText(info.selectionText || '');
        });
      default: return Promise.resolve(null);
    }
  }

  // Cookie header per URL (empty ones omitted), bounded parallelism.
  function cookiesByUrl(urls, storeId) {
    var out = {};
    var next = 0;
    function worker() {
      if (next >= urls.length) return Promise.resolve();
      var url = urls[next++];
      return W.readCookies(url, storeId).then(function (c) {
        if (c) out[url] = c;
      }, W.noop).then(worker);
    }
    var workers = [];
    for (var i = 0; i < COOKIE_CONCURRENCY; i++) workers.push(worker());
    return Promise.all(workers).then(function () { return out; });
  }

  menus.onClicked.addListener(function (info, tab) {
    var ids = [IDS.link, IDS.image, IDS.page, IDS.selection];
    if (ids.indexOf(info.menuItemId) < 0) return;
    collectUrls(info, tab).then(function (raw) {
      if (raw === null) return null;
      var urls = R.dedupUrls(raw || [], R.MAX_URLS);
      if (!urls.length) {
        W.badge('0', '#8a8a8a', t('badgeNone'));
        return null;
      }
      return W.storeIdForTab(tab).then(function (storeId) {
        return cookiesByUrl(urls, storeId);
      }).then(function (cookies) {
        var page = info.pageUrl || '';
        return W.bridge.request({
          type: 'add',
          urls: urls,
          pageUrl: page,
          referrer: info.frameUrl || page,
          cookiesByUrl: cookies,
          ua: W.ua,
          incognito: !!(tab && tab.incognito)
        }, { timeoutMs: 30000, kick: true });
      }).then(function (reply) {
        if (reply && reply.ok) {
          var added = typeof reply.added === 'number' ? reply.added : urls.length;
          W.badge('+' + added, '#2e7d32', api.i18n.getMessage('badgeAdded', [String(added)]));
        } else {
          W.log('add refused', reply && reply.error && reply.error.code);
          W.badge('!', '#c62828', t('badgeError'));
        }
      });
    }).catch(function (e) {
      W.log('menu action failed', e && (e.code || e.message));
      W.badge('!', '#c62828', t('badgeError'));
    });
  });
})();
