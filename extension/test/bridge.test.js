// bridge.js against a fake native port and a manual clock: request/reply, pushes, drop, backoff reconnect.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createClock, createPort, flush } = require('./harness.js');

const Bridge = require('../src/bridge.js');

function setup(extra) {
  const clock = createClock();
  const ports = [];
  let connectError = null;
  const bridge = new Bridge(Object.assign({
    prefix: 't',
    connect(name) {
      if (connectError) throw connectError;
      const p = createPort(name);
      ports.push(p);
      return p;
    },
    lastError: (p) => (p && p.error ? p.error.message : ''),
    hello: () => ({ browser: 'edge', extVersion: '1.0.0', incognitoAllowed: false }),
    setTimeout: clock.setTimeout,
    clearTimeout: clock.clearTimeout
  }, extra || {}));
  return { clock, ports, bridge, failConnect: (e) => { connectError = e; } };
}

async function ready(s, rules) {
  s.bridge.start();
  await flush();
  const port = s.ports[s.ports.length - 1];
  const hello = port.last();
  port.reply({ reqId: hello.reqId, ok: true, v: 1, appVersion: '9.9', rules: rules || { enabled: true, skipHosts: [], skipExt: [], holdMs: 5000 } });
  await flush();
  return port;
}

test('rules.js/bridge.js load as browser scripts (importScripts path)', () => {
  const sandbox = { URL, console };
  sandbox.self = sandbox;
  vm.createContext(sandbox);
  for (const f of ['rules.js', 'bridge.js']) {
    vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'src', f), 'utf8'), sandbox, { filename: f });
  }
  assert.equal(typeof sandbox.WpcBridge, 'function');
  assert.equal(sandbox.WpcBridge.HOST_NAME, 'org.wpc.downloads');
});

test('hello: host name, v:1 + fields, string reqId; ready with normalized rules', async () => {
  const s = setup();
  const states = [];
  s.bridge.on('state', (x) => states.push(x));
  s.bridge.start();
  await flush();
  assert.equal(s.ports.length, 1);
  assert.equal(s.ports[0].name, 'org.wpc.downloads');
  const hello = s.ports[0].last();
  assert.deepEqual(hello, { type: 'hello', v: 1, browser: 'edge', extVersion: '1.0.0', incognitoAllowed: false, reqId: 't-1' });
  s.ports[0].reply({ reqId: hello.reqId, ok: true, v: 1, appVersion: '2.0', rules: { enabled: true, skipHosts: ['A.org'], holdMs: 99999 } });
  await flush();
  assert.equal(s.bridge.state, 'ready');
  assert.equal(s.bridge.appVersion, '2.0');
  assert.deepEqual(s.bridge.rules.skipHosts, ['a.org']);
  assert.equal(s.bridge.rules.holdMs, 12000);
  assert.deepEqual(states, ['connecting', 'ready']);
});

test('request/reply routed by reqId, out-of-order replies, ok:false resolves', async () => {
  const s = setup();
  const port = await ready(s);
  const a = s.bridge.request({ type: 'status' });
  const b = s.bridge.request({ type: 'skipHost', host: 'x.org', on: true });
  await flush();
  const [ma, mb] = port.sent.slice(-2);
  assert.equal(ma.type, 'status');
  assert.notEqual(ma.reqId, mb.reqId);
  port.reply({ reqId: mb.reqId, ok: false, error: { code: 'not_allowed', msg: 'no' } });
  port.reply({ reqId: ma.reqId, ok: true, active: 1, queued: 2, speedBps: 3, agentRunning: true });
  const [ra, rb] = await Promise.all([a, b]);
  assert.equal(ra.active, 1);
  assert.equal(rb.ok, false);
  assert.equal(rb.error.code, 'not_allowed');
});

test('explicit reqId reuse (commit/abandon for an offer) after the offer settled', async () => {
  const s = setup();
  const port = await ready(s);
  const offer = s.bridge.request({ type: 'offer', url: 'https://a.org/x' });
  await flush();
  const id = port.last().reqId;
  port.reply({ reqId: id, ok: true, action: 'accept' });
  await offer;
  const commit = s.bridge.request({ type: 'commit', reqId: id });
  await flush();
  assert.deepEqual(port.last(), { type: 'commit', reqId: id });
  port.reply({ reqId: id, ok: true, id: 'item-1' });
  assert.equal((await commit).id, 'item-1');
  // In-flight duplicate is refused rather than cross-wired.
  const p1 = s.bridge.request({ type: 'abandon', reqId: 'same' });
  const p2 = s.bridge.request({ type: 'abandon', reqId: 'same' });
  await assert.rejects(p2, (e) => e.code === 'duplicate');
  port.reply({ reqId: 'same', ok: true });
  assert.equal((await p1).ok, true);
});

test('timeout rejects and a late reply is ignored', async () => {
  const s = setup();
  const port = await ready(s);
  const p = s.bridge.request({ type: 'status' }, { timeoutMs: 3000 });
  await flush();
  const id = port.last().reqId;
  const rejected = assert.rejects(p, (e) => e.code === 'timeout');
  await s.clock.advance(3000);
  await rejected;
  port.reply({ reqId: id, ok: true });
  await flush();
  assert.equal(s.bridge.state, 'ready');
});

test('requests before hello are queued and flushed; offline without kick fails fast', async () => {
  const s = setup();
  s.bridge.start();
  const early = s.bridge.request({ type: 'status' });
  await flush();
  assert.equal(s.ports[0].sent.length, 1, 'only hello goes out while connecting');
  const hello = s.ports[0].last();
  s.ports[0].reply({ reqId: hello.reqId, ok: true, v: 1, rules: {} });
  await flush();
  const st = s.ports[0].last();
  assert.equal(st.type, 'status');
  s.ports[0].reply({ reqId: st.reqId, ok: true, active: 0 });
  assert.equal((await early).ok, true);

  s.ports[0].drop('Native host has exited.');
  await flush();
  assert.equal(s.bridge.state, 'waiting');
  await assert.rejects(s.bridge.request({ type: 'status' }), (e) => e.code === 'disconnected');
});

test('port drop: every pending request fails, reconnect backoff 1,2,4 … 30 s, reset after hello', async () => {
  const s = setup();
  const port = await ready(s);
  const pending = [s.bridge.request({ type: 'offer' }), s.bridge.request({ type: 'status' })];
  await flush();
  const rejected = pending.map((p) => assert.rejects(p, (e) => e.code === 'disconnected'));
  port.drop('Native host has exited.');
  await Promise.all(rejected);
  assert.equal(s.bridge.state, 'waiting');
  assert.equal(s.bridge.lastError, 'Native host has exited.');

  const expected = [1000, 2000, 4000, 8000, 16000, 30000, 30000];
  for (let i = 0; i < expected.length; i++) {
    const before = s.ports.length;
    await s.clock.advance(expected[i] - 1);
    assert.equal(s.ports.length, before, 'no reconnect before ' + expected[i]);
    await s.clock.advance(1);
    assert.equal(s.ports.length, before + 1, 'reconnect at ' + expected[i]);
    s.ports[s.ports.length - 1].drop('Specified native messaging host not found.');
    await flush();
  }
  assert.equal(s.bridge.state, 'waiting');
  await s.clock.advance(30000);
  const p = s.ports[s.ports.length - 1];
  p.reply({ reqId: p.last().reqId, ok: true, v: 1, rules: {} });
  await flush();
  assert.equal(s.bridge.state, 'ready');
  assert.equal(s.bridge.attempt, 0);
  p.drop('gone');
  await flush();
  const n = s.ports.length;
  await s.clock.advance(1000);
  assert.equal(s.ports.length, n + 1, 'backoff restarts at 1 s');
});

test('kick reconnects immediately from backoff and the request rides the new hello', async () => {
  const s = setup();
  const port = await ready(s);
  port.drop('gone');
  await flush();
  const p = s.bridge.request({ type: 'offer', url: 'https://a.org/f' }, { kick: true });
  await flush();
  assert.equal(s.ports.length, 2);
  const hello = s.ports[1].last();
  assert.equal(hello.type, 'hello');
  s.ports[1].reply({ reqId: hello.reqId, ok: true, v: 1, rules: {} });
  await flush();
  const offer = s.ports[1].last();
  assert.equal(offer.type, 'offer');
  s.ports[1].reply({ reqId: offer.reqId, ok: true, action: 'decline' });
  assert.equal((await p).action, 'decline');
});

test('connect throwing and refused hello both go to backoff', async () => {
  const s = setup();
  s.failConnect(new Error('Access to the specified native messaging host is forbidden.'));
  s.bridge.start();
  await flush();
  assert.equal(s.bridge.state, 'waiting');
  assert.match(s.bridge.lastError, /forbidden/);
  s.failConnect(null);
  await s.clock.advance(1000);
  const port = s.ports[0];
  port.reply({ reqId: port.last().reqId, ok: false, error: { code: 'not_allowed' } });
  await flush();
  assert.equal(s.bridge.state, 'waiting');
  assert.equal(port.closed, true);
  assert.equal(s.bridge.lastError, 'hello: not_allowed');
  // Hello with the wrong protocol version is refused too.
  await s.clock.advance(2000);
  const p2 = s.ports[1];
  p2.reply({ reqId: p2.last().reqId, ok: true, v: 2, rules: {} });
  await flush();
  assert.equal(s.bridge.state, 'waiting');
});

test('hello without answer times out into backoff', async () => {
  const s = setup({ helloTimeoutMs: 10000 });
  s.bridge.start();
  await flush();
  await s.clock.advance(10000);
  assert.equal(s.bridge.state, 'waiting');
  assert.equal(s.ports[0].closed, true);
});

test('pushes: rules replaces cache without answer; others answered as <type>Result with pushId', async () => {
  const seen = [];
  const s = setup({
    onPush(msg) {
      seen.push(msg.type);
      if (msg.type === 'needCookies') return Promise.resolve({ cookies: 'a=1' });
      if (msg.type === 'giveBack') return Promise.reject(new Error('boom'));
      return null;
    }
  });
  const port = await ready(s);
  const n = port.sent.length;
  port.reply({ type: 'rules', pushId: 'p1', rules: { enabled: false, skipExt: ['EXE'] } });
  await flush();
  assert.equal(s.bridge.rules.enabled, false);
  assert.deepEqual(s.bridge.rules.skipExt, ['exe']);
  assert.equal(port.sent.length, n, 'rules push needs no answer');

  port.reply({ type: 'needCookies', pushId: 'p2', url: 'https://a.org/x' });
  port.reply({ type: 'giveBack', pushId: 7, url: 'https://a.org/y', filename: 'y' });
  port.reply({ type: 'mystery', pushId: 'p4' });
  await flush();
  const answers = port.sent.slice(n);
  assert.deepEqual(answers[0], { cookies: 'a=1', type: 'needCookiesResult', pushId: 'p2' });
  assert.deepEqual(answers[1], { ok: false, error: { code: 'internal', msg: 'boom' }, type: 'giveBackResult', pushId: 7 });
  assert.equal(answers.length, 2, 'handler returning null sends nothing');
  assert.deepEqual(seen, ['needCookies', 'giveBack', 'mystery']);
});

test('messages from a dropped port are ignored', async () => {
  const s = setup();
  const port = await ready(s);
  const p = s.bridge.request({ type: 'status' });
  await flush();
  const id = port.last().reqId;
  port.drop('gone');
  await assert.rejects(p);
  port.reply({ type: 'rules', pushId: 'x', rules: { enabled: false } });
  port.reply({ reqId: id, ok: true });
  await flush();
  assert.equal(s.bridge.rules.enabled, true);
});

test('reqId = "<per-instance prefix>-<counter>", prefix carries start time (unique across SW restarts)', () => {
  const realNow = Date.now;
  try {
    Date.now = () => 1000000;
    const a = new Bridge({ connect() {} });
    Date.now = () => 2000000;
    const b = new Bridge({ connect() {} });
    assert.match(a.nextReqId(), /^[0-9a-z]+-1$/);
    assert.match(a.nextReqId(), /^[0-9a-z]+-2$/);
    assert.ok(a.prefix.startsWith((1000000).toString(36)));
    assert.notEqual(a.prefix.slice(0, 4), b.prefix.slice(0, 4));
  } finally {
    Date.now = realNow;
  }
});
