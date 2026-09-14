// Real background.js + media.js booted in a vm over the shared fakes plus a webRequest/tabs surface media.js alone uses.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createBrowser, createClock, flush, event, answerHello } = require('./harness.js');

const SRC = path.join(__dirname, '..', 'src');

// Same boot as harness.js, with webRequest + tabs added: those two are not in the shared surface.
function boot(opts) {
  opts = opts || {};
  const mode = opts.mode || 'callback';
  const clock = createClock();
  const fake = createBrowser({ mode: mode });
  const tabs = opts.tabs || {};
  fake.api.webRequest = { onResponseStarted: event(), onBeforeRequest: event() };
  fake.api.tabs = {
    onRemoved: event(),
    get(id, cb) {
      const tab = Object.assign({ id: id, incognito: false }, tabs[id] || {});
      if (mode === 'promise') return Promise.resolve(tab);
      setImmediate(() => cb(tab));
      return undefined;
    }
  };
  const logs = [];
  const sandbox = {
    URL,
    console: { log: (...a) => logs.push(a.join(' ')), warn: () => {}, error: () => {} },
    setTimeout: clock.setTimeout,
    clearTimeout: clock.clearTimeout,
    navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/153.0 Safari/537.36', userAgentData: { brands: [{ brand: 'Chromium', version: '153' }] } }
  };
  sandbox.self = sandbox;
  const run = (f) => vm.runInContext(fs.readFileSync(path.join(SRC, f), 'utf8'), sandbox, { filename: f });
  if (mode === 'promise') {
    sandbox.browser = fake.api;
    sandbox.chrome = fake.api;
    sandbox.navigator.userAgentData = undefined;
    vm.createContext(sandbox);
    for (const f of ['rules.js', 'bridge.js', 'background.js', 'intercept-firefox.js', 'menu.js', 'media.js']) run(f);
  } else {
    sandbox.chrome = fake.api;
    sandbox.importScripts = (...files) => files.forEach(run);
    vm.createContext(sandbox);
    run('background.js');
  }
  return { clock, fake, sandbox, logs };
}

const HEADERS = (type, length) => {
  const h = [{ name: 'Server', value: 'nginx' }];
  if (type) h.push({ name: 'Content-Type', value: type });
  if (length !== undefined) h.push({ name: 'content-length', value: String(length) });
  return h;
};

function fire(env, props) {
  const d = Object.assign({
    tabId: 1,
    frameId: 0,
    type: 'media',
    method: 'GET',
    statusCode: 200,
    url: 'https://cdn.site.org/v/movie.mp4?sig=a1',
    documentUrl: 'https://site.org/watch/1',
    responseHeaders: HEADERS('video/mp4', 5000000)
  }, props || {});
  env.fake.api.webRequest.onResponseStarted.fire(d);
  return d;
}

function popup(env, req) {
  return new Promise((resolve) => {
    env.fake.api.runtime.onMessage.fire(req, { id: env.fake.api.runtime.id }, resolve);
  });
}

function badgeText(env, tabId) {
  const calls = env.fake.callsOf('action.setBadgeText').map((a) => a[0]).filter((o) => o.tabId === tabId);
  return calls.length ? calls[calls.length - 1].text : null;
}

test('media: listener registered non-blocking with the contract filter and responseHeaders', () => {
  const env = boot();
  const started = env.fake.api.webRequest.onResponseStarted.listeners;
  const before = env.fake.api.webRequest.onBeforeRequest.listeners;
  assert.equal(started.length, 1);
  assert.equal(before.length, 1);
  assert.equal(typeof env.sandbox.WpcMedia.detect, 'function');
});

test('media: detection table by content type and path', () => {
  const d = boot().sandbox.WpcMedia.detect;
  const rows = [
    ['https://c.org/a/index.m3u8', '', -1, 'hls'],
    ['https://c.org/a/play?x=1', 'application/vnd.apple.mpegurl', -1, 'hls'],
    ['https://c.org/a/play', 'application/x-mpegURL; charset=utf-8', -1, 'hls'],
    ['https://c.org/a/manifest.mpd', '', -1, 'dash'],
    ['https://c.org/a/m', 'application/dash+xml', -1, 'dash'],
    ['https://c.org/a/v.mp4', 'video/mp4', 5000000, 'file'],
    ['https://c.org/a/s.m4a', 'audio/mp4', -1, 'file'],
    ['https://c.org/a/v.webm', 'video/webm', 65536, 'file'],
    ['https://c.org/a/tiny.mp4', 'video/mp4', 65535, ''],
    ['https://c.org/a/seg-5.ts', 'video/mp2t', 400000, ''],
    ['https://c.org/a/seg-5.m4s', 'video/iso.segment', 400000, ''],
    ['https://c.org/a/seg-5.aac', 'audio/aac', 400000, ''],
    ['https://c.org/a/chunk', 'video/mp2t', 400000, ''],
    ['https://c.org/a/page.html', 'text/html', 5000000, ''],
    ['https://c.org/a/poster.jpg', 'image/jpeg', 5000000, ''],
    ['data:video/mp4;base64,AAAA', 'video/mp4', 5000000, ''],
    ['ftp://c.org/a/v.mp4', 'video/mp4', 5000000, '']
  ];
  for (const [url, type, len, want] of rows) assert.equal(d(url, type, len), want, url + ' ' + type);
});

test('media: hits collected per tab, newest first, badge counts them', async () => {
  const env = boot();
  fire(env);
  fire(env, { url: 'https://cdn.site.org/v/master.m3u8', responseHeaders: HEADERS('application/vnd.apple.mpegurl') });
  fire(env, { tabId: 2, url: 'https://other.org/x.mp4' });
  await flush();
  const res = await popup(env, { cmd: 'media', tabId: 1, url: 'https://site.org/watch/1' });
  assert.equal(res.ok, true);
  assert.equal(Array.from(res.items, (i) => i.kind).join(','), 'hls,file');
  assert.equal(res.items[0].url, 'https://cdn.site.org/v/master.m3u8');
  assert.equal(res.items[1].bytes, 5000000);
  assert.equal(res.items[1].type, 'video/mp4');
  assert.equal(badgeText(env, 1), '2');
  assert.equal(badgeText(env, 2), '1');
  const other = await popup(env, { cmd: 'media', tabId: 2, url: 'https://other.org/' });
  assert.equal(other.items.length, 1);
});

test('media: same URL with a different signature is one item, the freshest link wins', async () => {
  const env = boot();
  fire(env, { url: 'https://cdn.site.org/v/master.m3u8?sig=old', responseHeaders: HEADERS('application/x-mpegurl') });
  fire(env, { url: 'https://cdn.site.org/v/master.m3u8?sig=new', responseHeaders: HEADERS('application/x-mpegurl') });
  await flush();
  const res = await popup(env, { cmd: 'media', tabId: 1 });
  assert.equal(res.items.length, 1);
  assert.equal(res.items[0].url, 'https://cdn.site.org/v/master.m3u8?sig=new');
  assert.equal(badgeText(env, 1), '1');
});

test('media: list capped at 50, oldest dropped', async () => {
  const env = boot();
  for (let i = 0; i < 60; i++) fire(env, { url: 'https://cdn.site.org/v/' + i + '.mp4' });
  await flush();
  const res = await popup(env, { cmd: 'media', tabId: 1 });
  assert.equal(res.items.length, 50);
  assert.equal(res.items[0].url, 'https://cdn.site.org/v/59.mp4');
  assert.equal(res.items[49].url, 'https://cdn.site.org/v/10.mp4');
  assert.equal(badgeText(env, 1), '50');
});

test('media: incognito ignored — flag on the request and incognito tab alike', async () => {
  const env = boot({ tabs: { 7: { incognito: true } } });
  fire(env, { tabId: 5, incognito: true });
  fire(env, { tabId: 7 });
  fire(env, { tabId: 8 });
  await flush();
  assert.equal((await popup(env, { cmd: 'media', tabId: 5 })).items.length, 0);
  assert.equal((await popup(env, { cmd: 'media', tabId: 7 })).items.length, 0);
  assert.equal((await popup(env, { cmd: 'media', tabId: 8 })).items.length, 1);
});

test('media: top-frame navigation and tab close clear the list', async () => {
  const env = boot();
  fire(env);
  await flush();
  env.fake.api.webRequest.onBeforeRequest.fire({ tabId: 1, frameId: 0, type: 'sub_frame', url: 'https://site.org/ad' });
  await flush();
  assert.equal((await popup(env, { cmd: 'media', tabId: 1 })).items.length, 1, 'a sub frame is not a navigation');
  env.fake.api.webRequest.onBeforeRequest.fire({ tabId: 1, frameId: 0, type: 'main_frame', url: 'https://site.org/watch/2' });
  await flush();
  const after = await popup(env, { cmd: 'media', tabId: 1 });
  assert.equal(after.items.length, 0);
  assert.equal(after.page, 'https://site.org/watch/2');
  assert.equal(badgeText(env, 1), '');
  fire(env);
  await flush();
  env.fake.api.tabs.onRemoved.fire(1, { isWindowClosing: false });
  await flush();
  assert.equal((await popup(env, { cmd: 'media', tabId: 1 })).items.length, 0);
});

test('media: yt-dlp offer for video sites and for pages with nothing found', async () => {
  const env = boot();
  const m = env.sandbox.WpcMedia;
  assert.equal(m.offerYtdlp('https://www.youtube.com/watch?v=x'), true);
  assert.equal(m.offerYtdlp('https://vimeo.com/1'), true);
  assert.equal(m.offerYtdlp('https://notyoutube.com/watch'), false);
  assert.equal(m.offerYtdlp('https://site.org/watch'), false);
  fire(env);
  await flush();
  const withItems = await popup(env, { cmd: 'media', tabId: 1, url: 'https://site.org/watch/1' });
  assert.equal(withItems.ytdlp, false, 'plain site with a file offers the file, not yt-dlp');
  const empty = await popup(env, { cmd: 'media', tabId: 3, url: 'https://site.org/watch/1' });
  assert.deepEqual([empty.items.length, empty.ytdlp], [0, true]);
  const yt = await popup(env, { cmd: 'media', tabId: 4, url: 'https://www.youtube.com/watch?v=x' });
  assert.equal(yt.ytdlp, true);
});

test('media: addMedia message shape, page kind, and http-only guard', async () => {
  const env = boot();
  const port = await answerHello(env);
  fire(env, { url: 'https://cdn.site.org/v/master.m3u8?sig=1', responseHeaders: HEADERS('application/vnd.apple.mpegurl') });
  await flush();
  const p = popup(env, {
    cmd: 'addMedia', url: 'https://cdn.site.org/v/master.m3u8?sig=1', kind: 'hls', tabId: 1,
    pageUrl: 'https://site.org/watch/1', title: 'Movie'
  });
  await flush();
  const msg = port.last('addMedia');
  assert.deepEqual(Object.keys(msg).sort(), ['contentType', 'kind', 'pageUrl', 'referrer', 'reqId', 'title', 'type', 'url']);
  assert.equal(msg.url, 'https://cdn.site.org/v/master.m3u8?sig=1');
  assert.equal(msg.kind, 'hls');
  assert.equal(msg.pageUrl, 'https://site.org/watch/1');
  assert.equal(msg.referrer, 'https://site.org/watch/1');
  assert.equal(msg.title, 'Movie');
  assert.equal(msg.contentType, 'application/vnd.apple.mpegurl');
  port.reply({ reqId: msg.reqId, ok: true, id: 'd1' });
  const res = await p;
  assert.equal(res.ok, true);
  assert.equal(res.reply.id, 'd1');

  const page = popup(env, { cmd: 'addMedia', url: 'https://www.youtube.com/watch?v=x', kind: 'page', tabId: 4, title: 'Big Buck Bunny' });
  await flush();
  const msg2 = port.last('addMedia');
  assert.equal(msg2.kind, 'page');
  assert.equal(msg2.url, 'https://www.youtube.com/watch?v=x');
  assert.equal(msg2.contentType, '');
  port.reply({ reqId: msg2.reqId, ok: true, id: 'd2' });
  await page;

  const bad = await popup(env, { cmd: 'addMedia', url: 'file:///C:/secret.txt', kind: 'file', tabId: 1 });
  assert.equal(bad.ok, false);
  assert.equal(bad.error.code, 'bad_request');
  assert.equal(port.ofType('addMedia').length, 2, 'no message for a non-http address');
});

test('media: unknown commands still get bad_request, media state survives a worker restart', async () => {
  const env = boot();
  fire(env);
  await flush();
  const unknown = await popup(env, { cmd: 'nonsense' });
  assert.equal(unknown.ok, false);
  assert.equal(unknown.error.code, 'bad_request');
  // storage.session is what carries the list across a service worker restart.
  assert.equal(Object.keys(env.fake.session.wpcMedia['1'].items).length, 1);
  assert.equal(env.fake.session.wpcMedia['1'].page, 'https://site.org/watch/1');
});

test('media: firefox build collects the same way through browser.* promises', async () => {
  const env = boot({ mode: 'promise' });
  fire(env, { originUrl: 'https://site.org/watch/1', documentUrl: undefined });
  await flush();
  const res = await popup(env, { cmd: 'media', tabId: 1 });
  assert.equal(res.items.length, 1);
  assert.equal(res.page, 'https://site.org/watch/1');
});
