// Real background.js + intercept-*.js + menu.js booted in a vm over fake browser APIs and a fake native host.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { boot, answerHello, flush } = require('./harness.js');

const COOKIES = [
  { name: 'sid', value: 'abc', domain: '.site.org', path: '/' },
  { name: 'chip', value: 'p1', domain: 'site.org', path: '/', partitionKey: { topLevelSite: 'https://site.org' } }
];

// Fires onDeterminingFilename like Chromium; returns the suggest spy.
function determine(env, props) {
  const item = env.fake.addItem(Object.assign({
    url: 'https://site.org/get?id=1', finalUrl: 'https://dl.site.org/file.torrent', referrer: 'https://site.org/topic/1',
    filename: 'file.torrent', mime: 'application/x-bittorrent', totalBytes: 2048
  }, props || {}));
  const suggest = { count: 0 };
  const fn = () => { suggest.count += 1; };
  const ret = env.fake.api.downloads.onDeterminingFilename.fire(Object.assign({}, item), fn)[0];
  return { item, suggest, ret };
}

async function chromium(rules) {
  const env = boot({ mode: 'callback', cookies: COOKIES });
  const port = await answerHello(env, rules);
  return { env, port };
}

test('chromium boot: hello fields per contract, context menus created on install', async () => {
  const env = boot({ mode: 'callback' });
  await flush();
  const hello = env.fake.port().last('hello');
  assert.equal(env.fake.port().name, 'org.wpc.downloads');
  assert.deepEqual(Object.keys(hello).sort(), ['browser', 'extVersion', 'incognitoAllowed', 'reqId', 'type', 'v']);
  assert.equal(hello.v, 1);
  assert.equal(hello.browser, 'edge');
  assert.equal(hello.extVersion, '1.0.0');
  assert.equal(hello.incognitoAllowed, false);
  assert.equal(typeof hello.reqId, 'string');
  env.fake.api.runtime.onInstalled.fire({ reason: 'install' });
  await flush();
  const ids = env.fake.callsOf('menus.create').map((a) => a[0].id + ':' + a[0].contexts.join(','));
  assert.deepEqual(ids, ['wpc-link:link', 'wpc-image:image', 'wpc-page-links:page', 'wpc-selected-links:selection']);
});

test('chromium accept: offer (cookies incl. partitioned, ua) → cancel → erase → commit{same reqId}; no suggest', async () => {
  const { env, port } = await chromium();
  const d = determine(env);
  assert.equal(d.ret, true, 'listener holds the download');
  await flush();
  const offer = port.last('offer');
  assert.equal(offer.url, 'https://site.org/get?id=1');
  assert.equal(offer.finalUrl, 'https://dl.site.org/file.torrent');
  assert.equal(offer.pageUrl, 'https://site.org/topic/1');
  assert.equal(offer.cookies, 'sid=abc; chip=p1');
  assert.match(offer.ua, /Edg\/153/);
  assert.equal(offer.totalBytes, 2048);
  assert.equal(offer.byExtension, false);
  const storeQueries = env.fake.callsOf('cookies.getAll').map((a) => a[0]);
  assert.ok(storeQueries.some((q) => q.storeId === '0' && q.partitionKey), 'partitionKey:{} merge queried');
  port.reply({ reqId: offer.reqId, ok: true, action: 'accept' });
  await flush();
  assert.deepEqual(env.fake.callsOf('downloads.cancel'), [[d.item.id]]);
  assert.deepEqual(env.fake.callsOf('downloads.erase'), [[{ id: d.item.id }]]);
  assert.deepEqual(port.last('commit'), { type: 'commit', reqId: offer.reqId });
  assert.equal(d.suggest.count, 0);
  assert.equal(env.fake.items.has(d.item.id), false);
  port.reply({ reqId: offer.reqId, ok: true, id: 'dl-1' });
  await env.clock.advance(20000);
  assert.equal(env.fake.callsOf('downloads.download').length, 0, 'no browser restore after a good commit');
});

test('chromium decline → suggest() at once; nothing cancelled', async () => {
  const { env, port } = await chromium();
  const d = determine(env);
  await flush();
  port.reply({ reqId: port.last('offer').reqId, ok: true, action: 'decline', reason: 'rules' });
  await flush();
  assert.equal(d.suggest.count, 1);
  assert.equal(env.fake.callsOf('downloads.cancel').length, 0);
});

test('chromium hold expiry → suggest(); late accept → abandon{reqId}, download left alone', async () => {
  const { env, port } = await chromium({ holdMs: 3000 });
  const d = determine(env);
  await flush();
  const offer = port.last('offer');
  await env.clock.advance(2999);
  assert.equal(d.suggest.count, 0);
  await env.clock.advance(1);
  assert.equal(d.suggest.count, 1);
  port.reply({ reqId: offer.reqId, ok: true, action: 'accept' });
  await flush();
  assert.deepEqual(port.last('abandon'), { type: 'abandon', reqId: offer.reqId });
  assert.equal(port.ofType('commit').length, 0);
  assert.equal(env.fake.callsOf('downloads.cancel').length, 0);
  assert.equal(d.suggest.count, 1);
});

test('chromium: host answer never waits past holdMs even when the host is slow to hello', async () => {
  const env = boot({ mode: 'callback', cookies: COOKIES });
  await flush();
  const d = determine(env);
  await flush();
  assert.equal(env.fake.port().ofType('offer').length, 0, 'offer queued behind hello');
  await env.clock.advance(5000);
  assert.equal(d.suggest.count, 1);
});

test('chromium watch → suggest; storage.session entry; complete → browserFile; removeBrowserCopy → removeFile + erase', async () => {
  const { env, port } = await chromium();
  const d = determine(env);
  await flush();
  const offer = port.last('offer');
  port.reply({ reqId: offer.reqId, ok: true, action: 'watch' });
  await flush();
  assert.equal(d.suggest.count, 1);
  assert.ok(env.fake.session.wpcWatches[String(d.item.id)], 'watch persisted in storage.session');
  assert.equal(port.ofType('browserFile').length, 0, 'not complete yet');

  const it = env.fake.items.get(d.item.id);
  it.state = 'complete';
  it.filename = 'C:\\Users\\u\\Downloads\\file.torrent';
  env.fake.api.downloads.onChanged.fire({ id: d.item.id, state: { previous: 'in_progress', current: 'complete' } });
  await flush();
  const bf = port.last('browserFile');
  assert.deepEqual(bf, { type: 'browserFile', reqId: offer.reqId, path: 'C:\\Users\\u\\Downloads\\file.torrent',
    url: 'https://site.org/get?id=1', finalUrl: 'https://dl.site.org/file.torrent', mime: 'application/x-bittorrent', totalBytes: 2048 });
  port.reply({ reqId: bf.reqId, ok: true, removeBrowserCopy: true });
  await flush();
  assert.deepEqual(env.fake.callsOf('downloads.removeFile'), [[d.item.id]]);
  assert.deepEqual(env.fake.callsOf('downloads.erase'), [[{ id: d.item.id }]]);
  assert.deepEqual(env.fake.session.wpcWatches, {});
});

test('chromium watch survives a service-worker restart (storage.session) and reports on the next hello', async () => {
  const { env, port } = await chromium();
  const d = determine(env);
  await flush();
  port.reply({ reqId: port.last('offer').reqId, ok: true, action: 'watch' });
  await flush();
  // Host goes away, the download completes meanwhile.
  port.drop('Native host has exited.');
  const it = env.fake.items.get(d.item.id);
  it.state = 'complete';
  it.filename = 'C:\\d\\file.torrent';
  env.fake.api.downloads.onChanged.fire({ id: d.item.id, state: { current: 'complete' } });
  await flush();
  await env.clock.advance(1000);
  const p2 = await answerHello(env);
  const bf = p2.last('browserFile');
  assert.ok(bf, 'browserFile sent after reconnect');
  assert.equal(bf.path, 'C:\\d\\file.torrent');
  p2.reply({ reqId: bf.reqId, ok: true, removeBrowserCopy: false });
  await flush();
  assert.equal(env.fake.callsOf('downloads.removeFile').length, 0);
  assert.deepEqual(env.fake.session.wpcWatches, {});
});

test('chromium: port drop with a pending offer → suggest() immediately, reconnect after 1 s', async () => {
  const { env, port } = await chromium();
  const d = determine(env);
  await flush();
  assert.ok(port.last('offer'));
  port.drop('Native host has exited.');
  await flush();
  assert.equal(d.suggest.count, 1);
  const n = env.fake.ports.length;
  await env.clock.advance(1000);
  assert.equal(env.fake.ports.length, n + 1);
});

test('chromium: commit lost → the file is handed back to the browser download (marked, not re-offered)', async () => {
  const { env, port } = await chromium();
  const d = determine(env);
  await flush();
  const offer = port.last('offer');
  port.reply({ reqId: offer.reqId, ok: true, action: 'accept' });
  await flush();
  assert.ok(port.last('commit'));
  port.drop('Native host has exited.');
  await flush();
  const dl = env.fake.callsOf('downloads.download');
  assert.equal(dl.length, 1);
  assert.deepEqual(dl[0][0], { url: 'https://dl.site.org/file.torrent', saveAs: false, filename: 'file.torrent' });
  const again = determine(env, { url: 'https://dl.site.org/file.torrent', finalUrl: 'https://dl.site.org/file.torrent' });
  assert.equal(again.suggest.count, 1, 'own download passes straight through');
  assert.notEqual(d.item.id, again.item.id);
});

test('chromium: cancel fails (already finished) → suggest + abandon', async () => {
  const { env, port } = await chromium();
  const d = determine(env);
  await flush();
  env.fake.items.get(d.item.id).state = 'complete';
  const offer = port.last('offer');
  port.reply({ reqId: offer.reqId, ok: true, action: 'accept' });
  await flush();
  assert.equal(d.suggest.count, 1);
  assert.deepEqual(port.last('abandon'), { type: 'abandon', reqId: offer.reqId });
  assert.equal(port.ofType('commit').length, 0);
});

test('chromium filters: no offer for blob/data, own downloads, skipHosts, skipExt, disabled, incognito', async () => {
  const { env, port } = await chromium({ skipHosts: ['*.skip.org'], skipExt: ['exe'] });
  const cases = [
    { url: 'blob:https://site.org/1', finalUrl: 'blob:https://site.org/1' },
    { url: 'data:application/octet-stream;base64,AA==', finalUrl: 'data:application/octet-stream;base64,AA==' },
    { byExtensionId: 'kfbocmoigekddjcfodbhbiahndahdmal' },
    { referrer: 'https://www.skip.org/page' },
    { filename: 'setup.exe' },
    { incognito: true }
  ];
  for (const c of cases) {
    const d = determine(env, c);
    assert.equal(d.suggest.count, 1, JSON.stringify(c));
  }
  await flush();
  assert.equal(port.ofType('offer').length, 0);
  port.reply({ type: 'rules', pushId: 'r1', rules: { enabled: false } });
  await flush();
  assert.equal(determine(env).suggest.count, 1);
  assert.equal(port.ofType('offer').length, 0);
});

test('host not installed: download goes to the browser at once, bridge keeps backing off', async () => {
  const env = boot({ mode: 'callback', hostMissing: true });
  await flush();
  assert.equal(env.sandbox.WPC.bridge.state, 'waiting');
  assert.match(env.sandbox.WPC.bridge.lastError, /native messaging host not found/);
  assert.ok(env.logs.some((l) => /native port closed: Specified native messaging host not found/.test(l)));
  const d = determine(env);
  await flush();
  assert.equal(d.suggest.count, 1);
});

test('pushes: giveBack → downloads.download(basename, saveAs:false) → giveBackResult; needCookies → cookies', async () => {
  const { env, port } = await chromium();
  port.reply({ type: 'giveBack', pushId: 'g1', url: 'https://site.org/big.iso', referrer: 'https://site.org/', filename: '..\\..\\evil\\big.iso' });
  await flush();
  assert.deepEqual(env.fake.callsOf('downloads.download')[0][0], { url: 'https://site.org/big.iso', saveAs: false, filename: 'big.iso' });
  assert.deepEqual(port.last('giveBackResult'), { ok: true, type: 'giveBackResult', pushId: 'g1' });
  // The resulting browser download is never offered back.
  const own = determine(env, { url: 'https://site.org/big.iso', finalUrl: 'https://site.org/big.iso' });
  assert.equal(own.suggest.count, 1);
  port.reply({ type: 'giveBack', pushId: 'g2', url: 'javascript:alert(1)', filename: 'x' });
  port.reply({ type: 'needCookies', pushId: 'c1', url: 'https://site.org/big.iso' });
  await flush();
  assert.equal(port.last('giveBackResult').ok, false);
  assert.deepEqual(port.last('needCookiesResult'), { cookies: 'sid=abc; chip=p1', type: 'needCookiesResult', pushId: 'c1' });
  assert.equal(port.ofType('offer').length, 0);
});

test('menu: link → add{urls, cookiesByUrl, pageUrl, referrer, ua, incognito}; page links deduped via scripting', async () => {
  const env = boot({ mode: 'callback', cookies: COOKIES, scriptResult: {
    links: ['https://site.org/a.zip#x', 'https://site.org/a.zip', 'javascript:void(0)', 'https://other.net/b.bin'], text: '' } });
  const port = await answerHello(env);
  const menus = env.fake.api.contextMenus;
  menus.onClicked.fire({ menuItemId: 'wpc-link', linkUrl: 'https://site.org/a.zip', pageUrl: 'https://site.org/p', frameId: 0 }, { id: 1, incognito: false });
  await flush();
  const add = port.last('add');
  assert.deepEqual(Object.keys(add).sort(), ['cookiesByUrl', 'incognito', 'pageUrl', 'referrer', 'reqId', 'type', 'ua', 'urls']);
  assert.deepEqual(add.urls, ['https://site.org/a.zip']);
  assert.deepEqual(add.cookiesByUrl, { 'https://site.org/a.zip': 'sid=abc; chip=p1' });
  assert.equal(add.pageUrl, 'https://site.org/p');
  assert.equal(add.referrer, 'https://site.org/p');
  assert.equal(add.incognito, false);
  port.reply({ reqId: add.reqId, ok: true, added: 1, skipped: 0 });
  await flush();
  assert.deepEqual(env.fake.callsOf('action.setBadgeText').pop(), [{ text: '+1' }]);

  menus.onClicked.fire({ menuItemId: 'wpc-page-links', pageUrl: 'https://site.org/p', frameId: 0 }, { id: 1, incognito: false });
  await flush();
  const exec = env.fake.callsOf('scripting.executeScript').pop()[0];
  assert.deepEqual(exec.target, { tabId: 1 });
  const add2 = port.last('add');
  assert.deepEqual(add2.urls, ['https://site.org/a.zip', 'https://other.net/b.bin']);
  assert.deepEqual(Object.keys(add2.cookiesByUrl), ['https://site.org/a.zip']);
  assert.notEqual(add2.reqId, add.reqId);
});

test('popup commands: state, status, setEnabled/skipHost update cached rules, openApp page', async () => {
  const { env, port } = await chromium();
  const send = (msg) => new Promise((resolve) => {
    const r = env.fake.api.runtime.onMessage.fire(msg, { id: env.fake.api.runtime.id }, resolve)[0];
    assert.equal(r, true);
  });
  const st = await send({ cmd: 'state' });
  assert.equal(st.state.state, 'ready');
  const statusP = send({ cmd: 'status' });
  await flush();
  port.reply({ reqId: port.last('status').reqId, ok: true, active: 2, queued: 1, speedBps: 1024, agentRunning: true });
  assert.equal((await statusP).reply.active, 2);
  const skipP = send({ cmd: 'skipHost', host: 'site.org', on: true });
  await flush();
  assert.deepEqual(Object.keys(port.last('skipHost')).sort(), ['host', 'on', 'reqId', 'type']);
  port.reply({ reqId: port.last('skipHost').reqId, ok: true, rules: { enabled: true, skipHosts: ['site.org'] } });
  await skipP;
  assert.deepEqual(Array.from(env.sandbox.WPC.bridge.rules.skipHosts), ['site.org']);
  const enP = send({ cmd: 'setEnabled', on: false });
  await flush();
  assert.equal(port.last('setEnabled').on, false);
  port.reply({ reqId: port.last('setEnabled').reqId, ok: true, rules: { enabled: false } });
  await enP;
  assert.equal(env.sandbox.WPC.bridge.rules.enabled, false);
  const openP = send({ cmd: 'openApp' });
  await flush();
  assert.equal(port.last('openApp').page, 'downloads');
  port.reply({ reqId: port.last('openApp').reqId, ok: true });
  assert.equal((await openP).ok, true);
  // Foreign senders are ignored.
  assert.equal(env.fake.api.runtime.onMessage.fire({ cmd: 'state' }, { id: 'other' }, () => {})[0], false);
});

// ---- Firefox (promise API, downloads.onCreated) ----

function created(env, props) {
  const item = env.fake.addItem(Object.assign({
    url: 'https://site.org/file.torrent', referrer: 'https://site.org/topic', filename: 'C:\\Users\\u\\Downloads\\file.torrent',
    mime: 'application/x-bittorrent', totalBytes: 2048, cookieStoreId: 'firefox-default'
  }, props || {}));
  env.fake.api.downloads.onCreated.fire(Object.assign({}, item));
  return item;
}

test('firefox boot: hello browser "firefox"; accept → cancel → erase → commit', async () => {
  const env = boot({ mode: 'promise', cookies: COOKIES, ua: 'Mozilla/5.0 (Windows NT 10.0; rv:140.0) Gecko/20100101 Firefox/140.0' });
  await flush();
  assert.equal(env.fake.port().last('hello').browser, 'firefox');
  const port = await answerHello(env);
  const item = created(env);
  await flush();
  const offer = port.last('offer');
  assert.equal(offer.finalUrl, 'https://site.org/file.torrent');
  assert.equal(offer.filename, 'file.torrent');
  assert.equal(offer.cookies, 'sid=abc; chip=p1');
  assert.ok(env.fake.callsOf('cookies.getAll').some((a) => a[0].storeId === 'firefox-default' && a[0].firstPartyDomain === null));
  port.reply({ reqId: offer.reqId, ok: true, action: 'accept' });
  await flush();
  assert.deepEqual(env.fake.callsOf('downloads.cancel'), [[item.id]]);
  assert.deepEqual(env.fake.callsOf('downloads.erase'), [[{ id: item.id }]]);
  assert.deepEqual(port.last('commit'), { type: 'commit', reqId: offer.reqId });
});

test('firefox: finished before the answer → abandon, file stays; decline → nothing', async () => {
  const env = boot({ mode: 'promise', cookies: COOKIES });
  const port = await answerHello(env);
  const a = created(env);
  await flush();
  env.fake.items.get(a.id).state = 'complete';
  port.reply({ reqId: port.last('offer').reqId, ok: true, action: 'accept' });
  await flush();
  assert.ok(port.last('abandon'));
  assert.equal(env.fake.callsOf('downloads.cancel').length, 0);
  assert.equal(env.fake.items.has(a.id), true);

  created(env, { url: 'https://site.org/other.zip' });
  await flush();
  port.reply({ reqId: port.last('offer').reqId, ok: true, action: 'decline' });
  await flush();
  assert.equal(env.fake.callsOf('downloads.cancel').length, 0);
  assert.equal(env.fake.callsOf('downloads.erase').length, 0);
});

test('firefox watch → browserFile on complete; own (byExtensionId) downloads skipped', async () => {
  const env = boot({ mode: 'promise', cookies: COOKIES });
  const port = await answerHello(env);
  created(env, { byExtensionId: 'kfbocmoigekddjcfodbhbiahndahdmal' });
  await flush();
  assert.equal(port.ofType('offer').length, 0);
  const item = created(env);
  await flush();
  const offer = port.last('offer');
  port.reply({ reqId: offer.reqId, ok: true, action: 'watch' });
  await flush();
  env.fake.items.get(item.id).state = 'complete';
  env.fake.api.downloads.onChanged.fire({ id: item.id, state: { current: 'complete' } });
  await flush();
  const bf = port.last('browserFile');
  assert.equal(bf.reqId, offer.reqId);
  assert.equal(bf.path, 'C:\\Users\\u\\Downloads\\file.torrent');
  port.reply({ reqId: bf.reqId, ok: true, removeBrowserCopy: true });
  await flush();
  assert.deepEqual(env.fake.callsOf('downloads.removeFile'), [[item.id]]);
});
