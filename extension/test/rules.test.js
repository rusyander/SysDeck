// node --test extension/test  — pure rules.js checks + manifest invariants.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');

const R = require('../src/rules.js');
const EXT = path.join(__dirname, '..');

test('rules.js also loads as a plain browser script (global WpcRules)', () => {
  const sandbox = { URL };
  sandbox.self = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(path.join(EXT, 'src', 'rules.js'), 'utf8'), sandbox, { filename: 'rules.js' });
  assert.equal(typeof sandbox.WpcRules.shouldOffer, 'function');
  assert.equal(sandbox.WpcRules.hostOf('https://A.example.com/x'), 'a.example.com');
});

test('normalizeRules: defaults, lowercase lists, holdMs clamped under the browser cap', () => {
  const d = R.normalizeRules(null);
  assert.deepEqual(d, { enabled: true, incognito: false, skipHosts: [], skipExt: [], catchAll: true, holdMs: 5000 });
  const r = R.normalizeRules({ enabled: false, incognito: true, skipHosts: [' Example.COM ', 7, ''], skipExt: ['.EXE', 'Zip'], holdMs: 60000 });
  assert.equal(r.enabled, false);
  assert.equal(r.incognito, true);
  assert.deepEqual(r.skipHosts, ['example.com']);
  assert.deepEqual(r.skipExt, ['exe', 'zip']);
  assert.equal(r.holdMs, R.HOLD_MAX);
  assert.ok(R.HOLD_MAX < 15000, 'Edge takes over after ~15 s');
  assert.equal(R.normalizeRules({ holdMs: 1 }).holdMs, 500);
  assert.equal(R.normalizeRules({ holdMs: 'x' }).holdMs, 5000);
});

test('scheme filter: only http/https', () => {
  for (const u of ['http://a.b/x', 'https://a.b/x', 'HTTPS://A.B/']) assert.equal(R.isHttpUrl(u), true, u);
  for (const u of ['data:text/plain,hi', 'blob:https://a.b/uuid', 'filesystem:https://a.b/temporary/x', 'file:///C:/x',
    'chrome://downloads', 'chrome-extension://abc/x', 'edge://settings', 'about:blank', 'javascript:alert(1)', 'ftp://a.b/x', '', null]) {
    assert.equal(R.isHttpUrl(u), false, String(u));
  }
});

test('hostMatches: exact host and *.domain (domain itself + subdomains), no suffix confusion', () => {
  const list = ['example.com', '*.cdn.net'];
  assert.equal(R.hostMatches('example.com', list), 'example.com');
  assert.equal(R.hostMatches('EXAMPLE.com.', list), 'example.com');
  assert.equal(R.hostMatches('www.example.com', list), '');
  assert.equal(R.hostMatches('cdn.net', list), '*.cdn.net');
  assert.equal(R.hostMatches('a.b.cdn.net', list), '*.cdn.net');
  assert.equal(R.hostMatches('evilcdn.net', list), '');
  assert.equal(R.hostMatches('', list), '');
});

test('extOf: from browser filename, else URL path; lowercase, no dot', () => {
  assert.equal(R.extOf('C:\\Users\\me\\Downloads\\Movie.MKV', ''), 'mkv');
  assert.equal(R.extOf('', 'https://h/dl/file.torrent?x=1#y'), 'torrent');
  assert.equal(R.extOf('', 'https://h/dl/%D1%84%D0%B0%D0%B9%D0%BB.ZIP'), 'zip');
  assert.equal(R.extOf('noext', 'https://h/dl.php?id=5'), 'php');
  assert.equal(R.extOf('.bashrc', 'https://h/'), '');
  // '#' and '?' are legal parts of a browser-chosen file name.
  assert.equal(R.baseName('C:\\dl\\C# notes #2.pdf'), 'C# notes #2.pdf');
  assert.equal(R.extOf('C# notes #2.pdf', ''), 'pdf');
});

test('shouldOffer: every extension-side filter from the contract', () => {
  const rules = R.normalizeRules({ skipHosts: ['skip.me', '*.cdn.example'], skipExt: ['exe'] });
  const base = { url: 'https://site.org/a.bin', finalUrl: 'https://files.site.org/a.bin', referrer: 'https://site.org/', filename: 'a.bin', incognito: false };
  const v = (patch, r) => R.shouldOffer(Object.assign({}, base, patch), r === undefined ? rules : r);
  assert.deepEqual(v({}), { offer: true, reason: '' });
  assert.equal(v({ byExtension: true }).reason, 'byExtension');
  assert.equal(v({ url: 'blob:https://site.org/1', finalUrl: '' }).reason, 'scheme');
  assert.equal(v({ finalUrl: 'data:application/octet-stream,xx' }).reason, 'scheme');
  assert.equal(v({}, R.normalizeRules({ enabled: false })).reason, 'disabled');
  assert.equal(v({ incognito: true }).reason, 'incognito');
  assert.equal(v({ incognito: true }, R.normalizeRules({ incognito: true })).offer, true);
  assert.equal(v({ url: 'https://skip.me/f.zip', finalUrl: '' }).reason, 'skipHost');
  assert.equal(v({ finalUrl: 'https://x.cdn.example/a.bin' }).reason, 'skipHost');
  assert.equal(v({ referrer: 'https://skip.me/page' }).reason, 'skipHost');
  assert.equal(v({ filename: 'setup.EXE' }).reason, 'skipExt');
  // No size threshold: a tiny .torrent is offered.
  assert.equal(v({ filename: 'x.torrent', totalBytes: 12 }).offer, true);
  // Rules unknown yet (SW just woke): only hard filters apply.
  assert.equal(v({ filename: 'setup.exe', incognito: true }, null).offer, true);
  assert.equal(v({ byExtension: true }, null).offer, false);
});

test('buildOffer: exact contract field set', () => {
  const item = { id: 7, url: 'https://a.org/dl?id=1', finalUrl: 'https://cdn.a.org/f.torrent', referrer: 'https://a.org/topic',
    filename: 'C:\\Users\\u\\Downloads\\f.torrent', mime: 'application/x-bittorrent', totalBytes: 42, incognito: true };
  const o = R.buildOffer(item, { cookies: 'a=1; b=2', ua: 'UA/1' });
  assert.deepEqual(Object.keys(o).sort(), ['byExtension', 'cookies', 'filename', 'finalUrl', 'incognito', 'mime', 'pageUrl',
    'referrer', 'totalBytes', 'type', 'ua', 'url'].sort());
  assert.equal(o.type, 'offer');
  assert.equal(o.filename, 'f.torrent');
  assert.equal(o.pageUrl, 'https://a.org/topic');
  assert.equal(o.totalBytes, 42);
  assert.equal(o.incognito, true);
  assert.equal(o.byExtension, false);
  const u = R.buildOffer({ url: 'https://a.org/x', totalBytes: -1 }, {});
  assert.equal(u.finalUrl, 'https://a.org/x');
  assert.equal(u.totalBytes, -1);
  assert.equal(R.buildOffer({ url: 'https://a.org/x' }, {}).totalBytes, -1);
  assert.equal(u.cookies, '');
});

test('joinCookies: "n=v; n2=v2", partition merge without duplicates, longer path first', () => {
  const plain = [{ name: 'sid', value: '1', domain: '.a.org', path: '/' }, { name: 'deep', value: '2', domain: 'a.org', path: '/forum/' }];
  const parts = [{ name: 'sid', value: '1', domain: '.a.org', path: '/' }, { name: 'chip', value: '3', domain: 'a.org', path: '/', partitionKey: { topLevelSite: 'https://b.org' } }];
  assert.equal(R.joinCookies([plain, parts]), 'deep=2; sid=1; chip=3');
  assert.equal(R.joinCookies([[], null]), '');
  assert.equal(R.joinCookies([[{ name: '', value: 'bare', path: '/' }]]), 'bare');
});

test('dedupUrls: http/https only, fragment dropped, order kept, ≤ 500', () => {
  const urls = ['https://a.org/1#top', 'https://a.org/1', 'javascript:void(0)', 'mailto:x@y', ' http://b.org/2 ', 'ftp://c/3', 42];
  assert.deepEqual(R.dedupUrls(urls), ['https://a.org/1', 'http://b.org/2']);
  const many = [];
  for (let i = 0; i < 800; i++) many.push('https://a.org/f' + i);
  assert.equal(R.dedupUrls(many).length, 500);
  assert.equal(R.dedupUrls(many, 10000).length, 500);
  assert.equal(R.dedupUrls(many, 3).length, 3);
});

test('extractUrlsFromText: URLs inside selected text', () => {
  assert.deepEqual(R.extractUrlsFromText('see https://a.org/x.zip, and (http://b.org/y).'), ['https://a.org/x.zip', 'http://b.org/y']);
  assert.deepEqual(R.extractUrlsFromText(''), []);
});

test('sanitizeFilename: basename only, no reserved or control characters', () => {
  assert.equal(R.sanitizeFilename('..\\..\\Windows\\evil.dll'), 'evil.dll');
  assert.equal(R.sanitizeFilename('../../etc/passwd'), 'passwd');
  assert.equal(R.sanitizeFilename('a<b>:c"d|e?.txt'), 'a_b__c_d_e_.txt');
  assert.equal(R.sanitizeFilename('bad\u0001name.bin'), 'bad_name.bin');
  assert.equal(R.sanitizeFilename('CON.txt'), '_CON.txt');
  assert.equal(R.sanitizeFilename('  ...  '), '');
  assert.equal(R.sanitizeFilename(''), '');
  assert.equal(R.sanitizeFilename(null), '');
  const long = R.sanitizeFilename('x'.repeat(300) + '.torrent');
  assert.ok(long.length <= 180 && long.endsWith('.torrent'));
});

test('detectBrowser: brands first, UA fallback', () => {
  const b = (...names) => names.map((brand) => ({ brand, version: '1' }));
  assert.equal(R.detectBrowser(b('Not)A;Brand', 'Chromium', 'Microsoft Edge'), ''), 'edge');
  assert.equal(R.detectBrowser(b('Chromium', 'YaBrowser'), ''), 'yandex');
  assert.equal(R.detectBrowser(b('Chromium', 'Google Chrome'), ''), 'chrome');
  assert.equal(R.detectBrowser(b('Chromium', 'Brave'), ''), 'chromium');
  assert.equal(R.detectBrowser(null, 'Mozilla/5.0 ... Chrome/153 Safari/537.36 Edg/153.0'), 'edge');
  assert.equal(R.detectBrowser(null, 'Mozilla/5.0 ... Chrome/140 YaBrowser/26.6 Safari/537.36'), 'yandex');
  assert.equal(R.detectBrowser(null, 'Mozilla/5.0 (Windows NT 10.0; rv:140.0) Gecko/20100101 Firefox/140.0'), 'firefox');
  assert.equal(R.detectBrowser(null, 'Mozilla/5.0 ... HeadlessChrome/153 Safari/537.36'), 'chromium');
});

test('backoffMs: 1, 2, 4 … 30 s', () => {
  assert.deepEqual([0, 1, 2, 3, 4, 5, 6, 50].map(R.backoffMs), [1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000]);
});

test('formatRate and connectionKind (popup helpers)', () => {
  assert.deepEqual(R.formatRate(0), ['0', 0]);
  assert.deepEqual(R.formatRate(512), ['512', 0]);
  assert.deepEqual(R.formatRate(1536), ['1.5', 1]);
  assert.deepEqual(R.formatRate(250 * 1024 * 1024), ['250', 2]);
  assert.equal(R.connectionKind('Specified native messaging host not found.'), 'missing');
  assert.equal(R.connectionKind('No such native application org.wpc.downloads'), 'missing');
  assert.equal(R.connectionKind('Access to the specified native messaging host is forbidden.'), 'forbidden');
  assert.equal(R.connectionKind('hello: not_allowed'), 'forbidden');
  assert.equal(R.connectionKind('Native host has exited.'), 'lost');
});

test('manifests: fixed ids, contract permissions, nothing forbidden', () => {
  const chromium = JSON.parse(fs.readFileSync(path.join(EXT, 'manifest.chromium.json'), 'utf8'));
  const firefox = JSON.parse(fs.readFileSync(path.join(EXT, 'manifest.firefox.json'), 'utf8'));
  const keyFile = path.join(EXT, '..', '.agent', 'keys', 'extension-chromium.key.txt');
  if (fs.existsSync(keyFile)) assert.equal(chromium.key, fs.readFileSync(keyFile, 'utf8').trim());
  // Chromium id = first 16 bytes of sha256(SPKI) mapped 0-f → a-p.
  const hex = crypto.createHash('sha256').update(Buffer.from(chromium.key, 'base64')).digest('hex').slice(0, 32);
  const id = hex.replace(/[0-9a-f]/g, (c) => String.fromCharCode(97 + parseInt(c, 16)));
  assert.equal(id, 'kfbocmoigekddjcfodbhbiahndahdmal');
  assert.equal(chromium.manifest_version, 3);
  assert.equal(chromium.minimum_chrome_version, '110');
  // webRequest = наблюдение за запросами ради поиска потоков (HLS/DASH); webRequestBlocking намеренно нет — чужой трафик не изменяем.
  assert.deepEqual(chromium.permissions.slice().sort(), ['activeTab', 'contextMenus', 'cookies', 'downloads', 'nativeMessaging', 'scripting', 'storage', 'webRequest']);
  assert.deepEqual(chromium.host_permissions, ['<all_urls>']);
  assert.equal(firefox.browser_specific_settings.gecko.id, 'wpc-downloads@windows-process-cleaner');
  assert.equal(firefox.browser_specific_settings.gecko.strict_min_version, '115.0');
  assert.ok(Array.isArray(firefox.background.scripts) && !firefox.background.service_worker);
  for (const m of [chromium, firefox]) {
    assert.equal(m.default_locale, 'en');
    const all = (m.permissions || []).concat(m.optional_permissions || []);
    for (const bad of ['tabs', 'history', 'webRequestBlocking', 'management']) assert.ok(!all.includes(bad), bad);
    assert.ok(!m.content_scripts && !m.externally_connectable && !m.content_security_policy);
  }
});

test('locales: en and ru define the same keys, every used key exists', () => {
  const en = JSON.parse(fs.readFileSync(path.join(EXT, 'src', '_locales', 'en', 'messages.json'), 'utf8'));
  const ru = JSON.parse(fs.readFileSync(path.join(EXT, 'src', '_locales', 'ru', 'messages.json'), 'utf8'));
  assert.deepEqual(Object.keys(ru).sort(), Object.keys(en).sort());
  const used = new Set();
  for (const f of ['background.js', 'menu.js', 'popup.js', 'popup.html']) {
    const text = fs.readFileSync(path.join(EXT, 'src', f), 'utf8');
    for (const m of text.matchAll(/(?:data-i18n="|getMessage\('|\bt\(')([A-Za-z]+)/g)) used.add(m[1]);
    for (const m of text.matchAll(/:\s*'((?:conn|hint)[A-Z][A-Za-z]+)'/g)) used.add(m[1]);
    for (const m of text.matchAll(/'(unit[A-Za-z]*Bps|agentRunning|agentStopped)'/g)) used.add(m[1]);
  }
  for (const m of ['manifest.chromium.json', 'manifest.firefox.json']) {
    for (const k of fs.readFileSync(path.join(EXT, m), 'utf8').matchAll(/__MSG_([A-Za-z]+)__/g)) used.add(k[1]);
  }
  for (const k of used) assert.ok(en[k], 'missing locale key ' + k);
  assert.deepEqual(Object.keys(en).filter((k) => !used.has(k)), [], 'unused locale keys');
  for (const k of Object.keys(ru)) assert.ok(ru[k].message.trim(), 'empty ru message ' + k);
});

test('no eval / remote code in extension sources', () => {
  for (const f of fs.readdirSync(path.join(EXT, 'src')).filter((n) => /\.(js|html)$/.test(n))) {
    const text = fs.readFileSync(path.join(EXT, 'src', f), 'utf8');
    assert.ok(!/\beval\s*\(|new Function\s*\(|\bfetch\s*\(|XMLHttpRequest|WebSocket|<script[^>]+src=["']https?:/.test(text), f);
  }
});
