// Native messaging bridge (protocol v1): reqId/pushId routing, hello, reconnect backoff, pending map.
// Browser-agnostic: the port factory and timers are injected, so node tests drive it with a fake port.
(function (root, factory) {
  var rules = typeof module === 'object' && module.exports ? require('./rules.js') : root.WpcRules;
  var api = factory(rules);
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.WpcBridge = api;
})(typeof self !== 'undefined' ? self : this, function (R) {
  'use strict';

  var HOST_NAME = 'org.wpc.downloads';
  var PROTOCOL_V = 1;

  function noop() {}

  function bridgeError(code, msg) {
    var e = new Error(msg || code);
    e.code = code;
    return e;
  }

  // opts: connect(name) → port, lastError(port) → string, hello() → fields|Promise, onPush(msg) → fields|Promise,
  // setTimeout/clearTimeout, prefix, helloTimeoutMs, log.
  function Bridge(opts) {
    opts = opts || {};
    this.hostName = opts.hostName || HOST_NAME;
    this._connect = opts.connect;
    this._lastError = opts.lastError || function (port) {
      return port && port.error ? String(port.error.message || port.error) : '';
    };
    this._hello = opts.hello || function () { return {}; };
    this._onPush = opts.onPush || null;
    this._setTimeout = opts.setTimeout || function (fn, ms) { return setTimeout(fn, ms); };
    this._clearTimeout = opts.clearTimeout || function (t) { clearTimeout(t); };
    this._log = opts.log || noop;
    this._helloTimeoutMs = opts.helloTimeoutMs || 10000;
    this.prefix = opts.prefix || (Date.now().toString(36) + Math.floor(Math.random() * 1296).toString(36));

    this.state = 'idle'; // idle | connecting | ready | waiting | stopped
    this.rules = null;
    this.appVersion = '';
    this.lastError = '';
    this.attempt = 0;

    this._counter = 0;
    this._gen = 0;
    this._port = null;
    this._retryTimer = null;
    this._pending = {};
    this._queue = [];
    this._listeners = { state: [], rules: [] };
  }

  Bridge.HOST_NAME = HOST_NAME;
  Bridge.PROTOCOL_V = PROTOCOL_V;
  Bridge.error = bridgeError;

  Bridge.prototype.on = function (event, fn) {
    if (this._listeners[event]) this._listeners[event].push(fn);
  };

  Bridge.prototype._emit = function (event, arg) {
    var list = this._listeners[event] || [];
    for (var i = 0; i < list.length; i++) {
      try { list[i](arg); } catch (e) { this._log('listener error', e && e.message); }
    }
  };

  Bridge.prototype._setState = function (s) {
    if (this.state === s) return;
    this.state = s;
    this._emit('state', s);
  };

  Bridge.prototype.nextReqId = function () {
    this._counter += 1;
    return this.prefix + '-' + this._counter;
  };

  Bridge.prototype.snapshot = function () {
    return {
      state: this.state,
      rules: this.rules,
      appVersion: this.appVersion,
      lastError: this.lastError,
      attempt: this.attempt
    };
  };

  Bridge.prototype.start = function () {
    if (this.state === 'idle' || this.state === 'waiting') this._open();
  };

  // Reconnect now if sleeping in backoff (user-triggered actions only).
  Bridge.prototype.kick = function () {
    if (this.state === 'idle' || this.state === 'waiting') this._open();
  };

  Bridge.prototype.stop = function () {
    this._setState('stopped');
    if (this._retryTimer !== null) { this._clearTimeout(this._retryTimer); this._retryTimer = null; }
    var port = this._port;
    this._port = null;
    this._gen += 1;
    if (port) { try { port.disconnect(); } catch (e) { /* already closed */ } }
    this._failAll(bridgeError('disconnected', 'bridge stopped'));
  };

  Bridge.prototype.setRules = function (rules) {
    this.rules = R.normalizeRules(rules);
    this._emit('rules', this.rules);
    return this.rules;
  };

  Bridge.prototype._open = function () {
    var self = this;
    if (this._retryTimer !== null) { this._clearTimeout(this._retryTimer); this._retryTimer = null; }
    this._gen += 1;
    var gen = this._gen;
    this._setState('connecting');
    var port;
    try {
      port = this._connect(this.hostName);
    } catch (e) {
      this._drop(gen, e && e.message ? e.message : String(e));
      return;
    }
    if (!port) { this._drop(gen, 'connectNative returned nothing'); return; }
    this._port = port;
    port.onMessage.addListener(function (msg) { self._onMessage(gen, msg); });
    port.onDisconnect.addListener(function (p) {
      self._drop(gen, self._lastError(p || port) || 'native host disconnected');
    });

    Promise.resolve().then(function () { return self._hello(); }).then(function (fields) {
      if (gen !== self._gen) return null;
      var msg = { type: 'hello', v: PROTOCOL_V };
      fields = fields || {};
      for (var k in fields) if (Object.prototype.hasOwnProperty.call(fields, k)) msg[k] = fields[k];
      msg.type = 'hello';
      msg.v = PROTOCOL_V;
      msg.reqId = self.nextReqId();
      return self._send(msg, self._helloTimeoutMs);
    }).then(function (reply) {
      if (!reply || gen !== self._gen) return;
      if (reply.ok !== true || reply.v !== PROTOCOL_V) {
        var code = reply.error && reply.error.code ? reply.error.code : 'bad_hello';
        self._log('hello refused', code);
        self._closeWith(gen, 'hello: ' + code);
        return;
      }
      self.attempt = 0;
      self.lastError = '';
      self.appVersion = typeof reply.appVersion === 'string' ? reply.appVersion : '';
      self.setRules(reply.rules);
      self._setState('ready');
      self._flush();
    }).catch(function (e) {
      if (gen !== self._gen) return;
      self._closeWith(gen, 'hello: ' + (e && (e.code || e.message)));
    });
  };

  // Local close (bad hello): disconnect() does not fire onDisconnect for our own side.
  Bridge.prototype._closeWith = function (gen, reason) {
    var port = this._port;
    if (port) { try { port.disconnect(); } catch (e) { /* ignore */ } }
    this._drop(gen, reason);
  };

  Bridge.prototype._drop = function (gen, reason) {
    if (gen !== this._gen || this.state === 'stopped') return;
    this._gen += 1;
    this._port = null;
    this.lastError = reason || 'disconnected';
    this._log('native port closed:', this.lastError);
    this._failAll(bridgeError('disconnected', this.lastError));
    var delay = R.backoffMs(this.attempt);
    this.attempt += 1;
    var self = this;
    this._retryTimer = this._setTimeout(function () {
      self._retryTimer = null;
      if (self.state === 'waiting') self._open();
    }, delay);
    this._setState('waiting');
  };

  Bridge.prototype._failAll = function (err) {
    var pending = this._pending;
    this._pending = {};
    var queue = this._queue;
    this._queue = [];
    for (var id in pending) {
      if (!Object.prototype.hasOwnProperty.call(pending, id)) continue;
      this._clearTimeout(pending[id].timer);
      pending[id].reject(err);
    }
    for (var i = 0; i < queue.length; i++) {
      this._clearTimeout(queue[i].timer);
      queue[i].reject(err);
    }
  };

  Bridge.prototype._flush = function () {
    var queue = this._queue;
    this._queue = [];
    for (var i = 0; i < queue.length; i++) this._post(queue[i]);
  };

  // Register pending entry and write to the port.
  Bridge.prototype._post = function (entry) {
    var id = entry.msg.reqId;
    if (this._pending[id]) {
      this._clearTimeout(entry.timer);
      entry.reject(bridgeError('duplicate', 'reqId in flight: ' + id));
      return;
    }
    this._pending[id] = entry;
    try {
      this._port.postMessage(entry.msg);
    } catch (e) {
      delete this._pending[id];
      this._clearTimeout(entry.timer);
      entry.reject(bridgeError('disconnected', e && e.message));
      this._closeWith(this._gen, 'postMessage: ' + (e && e.message));
    }
  };

  Bridge.prototype._makeEntry = function (msg, timeoutMs, resolve, reject) {
    var self = this;
    var entry = { msg: msg, resolve: resolve, reject: reject, timer: null };
    entry.timer = this._setTimeout(function () {
      if (self._pending[msg.reqId] === entry) delete self._pending[msg.reqId];
      var qi = self._queue.indexOf(entry);
      if (qi >= 0) self._queue.splice(qi, 1);
      reject(bridgeError('timeout', msg.type + ' timed out'));
    }, timeoutMs);
    return entry;
  };

  // Hello goes out while still "connecting".
  Bridge.prototype._send = function (msg, timeoutMs) {
    var self = this;
    return new Promise(function (resolve, reject) {
      self._post(self._makeEntry(msg, timeoutMs, resolve, reject));
    });
  };

  // Resolves with the host reply (ok may be false); rejects with code disconnected|timeout|duplicate.
  // options: timeoutMs (default 30 s), kick (reconnect now if in backoff).
  Bridge.prototype.request = function (msg, options) {
    options = options || {};
    var self = this;
    if (msg.reqId === undefined || msg.reqId === null || msg.reqId === '') msg.reqId = this.nextReqId();
    msg.reqId = String(msg.reqId);
    return new Promise(function (resolve, reject) {
      if (self.state !== 'ready' && self.state !== 'connecting') {
        if (options.kick && (self.state === 'idle' || self.state === 'waiting')) self._open();
      }
      if (self.state !== 'ready' && self.state !== 'connecting') {
        reject(bridgeError('disconnected', self.lastError || 'not connected'));
        return;
      }
      var entry = self._makeEntry(msg, options.timeoutMs || 30000, resolve, reject);
      if (self.state === 'ready') self._post(entry);
      else self._queue.push(entry);
    });
  };

  Bridge.prototype._onMessage = function (gen, msg) {
    if (gen !== this._gen || !msg || typeof msg !== 'object') return;
    if (msg.reqId !== undefined && msg.reqId !== null) {
      var id = String(msg.reqId);
      var entry = this._pending[id];
      if (entry) {
        delete this._pending[id];
        this._clearTimeout(entry.timer);
        entry.resolve(msg);
      } else {
        this._log('reply without request', id);
      }
      return;
    }
    if (msg.pushId !== undefined && msg.pushId !== null && typeof msg.type === 'string') {
      this._handlePush(gen, msg);
      return;
    }
    this._log('unroutable message', msg.type);
  };

  Bridge.prototype._handlePush = function (gen, msg) {
    var self = this;
    if (msg.type === 'rules') {
      if (msg.rules && typeof msg.rules === 'object') this.setRules(msg.rules);
      return;
    }
    if (!this._onPush) { this._log('push ignored', msg.type); return; }
    var resultType = msg.type + 'Result';
    Promise.resolve().then(function () { return self._onPush(msg); }).then(function (fields) {
      return fields;
    }, function (e) {
      return { ok: false, error: { code: 'internal', msg: e && e.message ? e.message : String(e) } };
    }).then(function (fields) {
      if (fields === undefined || fields === null) return; // handler chose not to answer
      if (gen !== self._gen || !self._port) return;
      var out = {};
      for (var k in fields) if (Object.prototype.hasOwnProperty.call(fields, k)) out[k] = fields[k];
      out.type = resultType;
      out.pushId = msg.pushId;
      try { self._port.postMessage(out); } catch (e) { self._log('push answer lost', e && e.message); }
    });
  };

  return Bridge;
});
