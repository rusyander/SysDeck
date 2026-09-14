// Test doubles: manual clock, fake native port, fake chrome/browser APIs running the real background scripts in a vm.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SRC = path.join(__dirname, '..', 'src');
const EXT_ID = 'kfbocmoigekddjcfodbhbiahndahdmal';

async function flush() {
  for (let i = 0; i < 30; i++) await new Promise((r) => setImmediate(r));
}

function createClock() {
  let now = 0;
  let seq = 0;
  const timers = new Map();
  return {
    setTimeout(fn, ms) {
      const id = ++seq;
      timers.set(id, { at: now + Math.max(0, Number(ms) || 0), fn });
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
    async advance(ms) {
      const end = now + ms;
      for (;;) {
        await flush();
        let next = null;
        for (const [id, t] of timers) if (t.at <= end && (!next || t.at < next[1].at)) next = [id, t];
        if (!next) break;
        timers.delete(next[0]);
        now = next[1].at;
        next[1].fn();
      }
      now = end;
      await flush();
    },
    get now() { return now; },
    get pending() { return timers.size; }
  };
}

function event() {
  const listeners = [];
  return {
    listeners,
    addListener(fn) { listeners.push(fn); },
    removeListener(fn) { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
    fire(...args) { return listeners.map((fn) => fn(...args)); }
  };
}

// env.setLastError/clearLastError emulate chrome.runtime.lastError around onDisconnect.
function createPort(name, env) {
  const port = {
    name,
    sent: [],
    closed: false,
    error: null,
    onMessage: event(),
    onDisconnect: event(),
    postMessage(msg) {
      if (port.closed) throw new Error('Attempting to use a disconnected port object');
      port.sent.push(JSON.parse(JSON.stringify(msg)));
    },
    disconnect() { port.closed = true; },
    last(type) {
      if (!type) return port.sent[port.sent.length - 1];
      for (let i = port.sent.length - 1; i >= 0; i--) if (port.sent[i].type === type) return port.sent[i];
      return undefined;
    },
    ofType(type) { return port.sent.filter((m) => m.type === type); },
    reply(msg) { if (!port.closed) port.onMessage.fire(JSON.parse(JSON.stringify(msg))); },
    drop(message) {
      port.closed = true;
      port.error = { message };
      if (env && env.setLastError) env.setLastError(message);
      try { port.onDisconnect.fire(port); } finally { if (env && env.clearLastError) env.clearLastError(); }
    }
  };
  return port;
}

// Fake WebExtension surface. mode 'callback' = Chromium (callbacks + runtime.lastError), 'promise' = Firefox.
function createBrowser(opts) {
  opts = opts || {};
  const mode = opts.mode || 'callback';
  const calls = [];
  const ports = [];
  const items = new Map();
  const session = {};
  const cookieJar = opts.cookies || [];
  let nextId = 100;
  let hostMissing = !!opts.hostMissing;
  let api;

  function wrap(name, impl) {
    return function (...args) {
      if (mode === 'callback') {
        const cb = typeof args[args.length - 1] === 'function' ? args.pop() : null;
        calls.push([name, JSON.parse(JSON.stringify(args))]);
        Promise.resolve().then(() => impl(...args)).then((res) => {
          if (cb) cb(res);
        }, (err) => {
          api.runtime.lastError = { message: err.message };
          try { if (cb) cb(); } finally { api.runtime.lastError = undefined; }
        });
        return undefined;
      }
      calls.push([name, JSON.parse(JSON.stringify(args))]);
      return Promise.resolve().then(() => impl(...args));
    };
  }

  function addItem(props) {
    const item = Object.assign({ id: nextId++, state: 'in_progress', exists: true, incognito: false, mime: '', totalBytes: -1,
      referrer: '', filename: '' }, props);
    items.set(item.id, item);
    return item;
  }

  const env = {
    setLastError(message) { api.runtime.lastError = { message }; },
    clearLastError() { api.runtime.lastError = undefined; }
  };

  api = {
    runtime: {
      id: EXT_ID,
      lastError: undefined,
      getManifest: () => ({ version: '1.0.0' }),
      connectNative(name) {
        const port = createPort(name, mode === 'callback' ? env : null);
        ports.push(port);
        if (hostMissing) {
          setImmediate(() => port.drop(mode === 'callback' ? 'Specified native messaging host not found.' : 'No such native application ' + name));
        }
        return port;
      },
      onMessage: event(),
      onInstalled: event(),
      onStartup: event()
    },
    extension: {
      isAllowedIncognitoAccess: wrap('extension.isAllowedIncognitoAccess', () => false)
    },
    i18n: {
      getMessage: (key, subs) => key + (subs && subs.length ? '(' + subs.join(',') + ')' : ''),
      getUILanguage: () => 'ru'
    },
    downloads: {
      onDeterminingFilename: event(),
      onCreated: event(),
      onChanged: event(),
      cancel: wrap('downloads.cancel', (id) => {
        const it = items.get(id);
        if (!it) throw new Error('Invalid download id');
        if (it.state !== 'in_progress') throw new Error('Download must be in progress');
        it.state = 'interrupted';
        if (mode === 'promise') it.exists = false;
      }),
      erase: wrap('downloads.erase', (q) => { const ok = items.delete(q.id); return ok ? [q.id] : []; }),
      search: wrap('downloads.search', (q) => (items.has(q.id) ? [Object.assign({}, items.get(q.id))] : [])),
      removeFile: wrap('downloads.removeFile', (id) => {
        const it = items.get(id);
        if (!it || it.state !== 'complete') throw new Error('Download must be complete');
        it.exists = false;
      }),
      download: wrap('downloads.download', (o) => addItem({ url: o.url, finalUrl: o.url, filename: o.filename || '' }).id)
    },
    cookies: {
      getAll: wrap('cookies.getAll', (q) => {
        const host = new URL(q.url).hostname;
        return cookieJar.filter((c) => host.endsWith(c.domain.replace(/^\./, '')) && (!!c.partitionKey === !!q.partitionKey));
      }),
      getAllCookieStores: wrap('cookies.getAllCookieStores', () => [{ id: '0', tabIds: [1] }, { id: '1', tabIds: [9] }])
    },
    storage: {
      session: {
        get: wrap('storage.session.get', (k) => ({ [k]: session[k] !== undefined ? JSON.parse(JSON.stringify(session[k])) : undefined })),
        set: wrap('storage.session.set', (o) => { Object.assign(session, JSON.parse(JSON.stringify(o))); })
      }
    },
    action: {
      setBadgeText: wrap('action.setBadgeText', () => {}),
      setBadgeBackgroundColor: wrap('action.setBadgeBackgroundColor', () => {}),
      setTitle: wrap('action.setTitle', () => {})
    },
    scripting: {
      executeScript: wrap('scripting.executeScript', () => [{ frameId: 0, result: opts.scriptResult || { links: [], text: '' } }])
    }
  };
  const menus = {
    onClicked: event(),
    create: wrap('menus.create', (p) => p.id),
    removeAll: wrap('menus.removeAll', () => {})
  };
  if (mode === 'callback') api.contextMenus = menus;
  else {
    api.menus = menus;
    api.runtime.getBrowserInfo = () => Promise.resolve({ name: 'Firefox', version: '140.0' });
  }

  return {
    api, calls, ports, items, session, addItem, env,
    setHostMissing(v) { hostMissing = v; },
    port() { return ports[ports.length - 1]; },
    callsOf(name) { return calls.filter((c) => c[0] === name).map((c) => c[1]); }
  };
}

// Boots the real background scripts in a vm. mode 'callback' = Chromium SW via importScripts, 'promise' = Firefox list.
function boot(opts) {
  opts = opts || {};
  const clock = createClock();
  const fake = createBrowser(opts);
  const logs = [];
  const sandbox = {
    URL,
    console: { log: (...a) => logs.push(a.join(' ')), warn: () => {}, error: () => {} },
    setTimeout: clock.setTimeout,
    clearTimeout: clock.clearTimeout,
    navigator: {
      userAgent: opts.ua || 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/153.0 Safari/537.36 Edg/153.0',
      userAgentData: opts.mode === 'promise' ? undefined : { brands: [{ brand: 'Chromium', version: '153' }, { brand: 'Microsoft Edge', version: '153' }] }
    }
  };
  sandbox.self = sandbox;
  const run = (f) => vm.runInContext(fs.readFileSync(path.join(SRC, f), 'utf8'), sandbox, { filename: f });
  if (opts.mode === 'promise') {
    sandbox.browser = fake.api;
    sandbox.chrome = fake.api;
    vm.createContext(sandbox);
    for (const f of ['rules.js', 'bridge.js', 'background.js', 'intercept-firefox.js', 'menu.js']) run(f);
  } else {
    sandbox.chrome = fake.api;
    sandbox.importScripts = (...files) => files.forEach(run);
    vm.createContext(sandbox);
    run('background.js');
  }
  return { clock, fake, sandbox, logs };
}

// Answers the pending hello on the newest port.
async function answerHello(env, rules) {
  await flush();
  const port = env.fake.port();
  const hello = port.last('hello');
  port.reply({ reqId: hello.reqId, ok: true, v: 1, appVersion: '1.0', rules: Object.assign({ enabled: true, incognito: false, skipHosts: [], skipExt: [], catchAll: true, holdMs: 5000 }, rules || {}) });
  await flush();
  return port;
}

module.exports = { flush, createClock, createPort, createBrowser, boot, answerHello, EXT_ID, event };
