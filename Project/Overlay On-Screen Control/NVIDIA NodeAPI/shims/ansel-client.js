'use strict';
const fs = require('fs');
const net = require('net');
const MAX_WIRE = 16 * 1024 * 1024;
const PREFIX = 'NVIDIAAnsel.';

function failure(status, code, message) { return Object.assign(new Error(message), { status, code }); }

class AnselClient {
  constructor(options) {
    options = options || {};
    this.list = options.list || (() => new Promise((resolve, reject) => {
      fs.readdir('\\\\.\\pipe\\', (err, names) => err ? reject(err) : resolve(names.filter(n => /^NVIDIAAnsel\.\d+\.\d+$/.test(n))));
    }));
    this.connect = options.connect || (name => net.createConnection('\\\\.\\pipe\\' + name));
    this.timeout = options.timeout || 1800;
    this.queue = Promise.resolve();
  }
  exchange(name, request) {
    return new Promise((resolve, reject) => {
      let socket, chunks = [], receivedBytes = 0, settled = false;
      const finish = (err, reply) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        if (socket) socket.destroy();
        err ? reject(err) : resolve(reply);
      };
      const timer = setTimeout(() => finish(failure(504, 'ANSEL_TIMEOUT', 'Game render thread did not acknowledge the request')), this.timeout);
      try {
        socket = this.connect(name);
        socket.setEncoding('utf8');
        socket.on('error', () => finish(failure(503, 'ANSEL_DISCONNECTED', 'ReShade runtime is unavailable')));
        socket.on('end', () => finish(failure(503, 'ANSEL_DISCONNECTED', 'ReShade runtime disconnected')));
        socket.on('connect', () => socket.write(JSON.stringify(Object.assign({ protocol: 1, deadline: Date.now() + this.timeout - 100 }, request)) + '\n'));
        socket.on('data', chunk => {
          receivedBytes += Buffer.byteLength(chunk);
          if (receivedBytes > MAX_WIRE) return finish(failure(502, 'ANSEL_INVALID_REPLY', 'Adapter response exceeds the 16 MiB transport limit'));
          const end = chunk.indexOf('\n');
          chunks.push(end < 0 ? chunk : chunk.slice(0, end));
          if (end < 0) return;
          let reply;
          try { reply = JSON.parse(chunks.join('')); chunks = []; }
          catch (_) { return finish(failure(502, 'ANSEL_INVALID_REPLY', 'Adapter returned invalid JSON')); }
          if (!reply || reply.ok !== true) return finish(failure(reply && reply.status || 502,
            reply && reply.code || 'ANSEL_INVALID_REPLY', reply && reply.message || 'Adapter rejected the command'));
          const s = reply.state;
          if (!s || s.protocol !== 1 || !Number.isInteger(s.pid) || s.pid <= 0 ||
              typeof s.session !== 'string' || !s.session || !Number.isInteger(s.generation) ||
              !Array.isArray(s.filters) || !Array.isArray(s.stack) || typeof s.enabled !== 'boolean')
            return finish(failure(502, 'ANSEL_INVALID_REPLY', 'Adapter returned an invalid runtime snapshot'));
          finish(null, s);
        });
      } catch (err) { finish(err); }
    });
  }
  serial(work) {
    const result = this.queue.then(work);
    this.queue = result.catch(() => {});
    return result;
  }
  async discover(pid, beforeSend) {
    let names;
    try { names = await this.list(); }
    catch (_) { throw failure(503, 'ANSEL_UNAVAILABLE', 'Ansel Adapter discovery is unavailable'); }
    if (beforeSend) beforeSend();
    names = names.filter(name => name.indexOf(PREFIX) === 0 && (!pid || Number(name.split('.')[1]) === pid));
    if (names.length > 32) throw failure(409, 'ANSEL_AMBIGUOUS', 'Too many ReShade runtimes; select a game process');
    const errors = [];
    const snapshots = await Promise.all(names.map(async name => {
      try { return { name, state: await this.exchange(name, { op: 'state' }) }; }
      catch (error) { errors.push(error); return null; }
    }));
    const live = snapshots.filter(Boolean);
    const foreground = live.filter(item => item.state.foreground === true);
    if (foreground.length === 1) return foreground[0];
    if (live.length === 1) return live[0];
    if (!live.length) {
      const runtimeError = errors.find(error => !['ANSEL_TIMEOUT', 'ANSEL_DISCONNECTED'].includes(error.code));
      if (runtimeError) throw runtimeError;
      throw failure(503, 'ANSEL_UNAVAILABLE', 'No responding D3D11 ReShade runtime is connected');
    }
    throw failure(409, 'ANSEL_AMBIGUOUS', 'More than one game/runtime is connected; select a game process');
  }
  state(pid, beforeSend) { return this.serial(() => { if (beforeSend) beforeSend(); return this.discover(pid, beforeSend); }); }
  read(target) { return this.serial(async () => ({ name: target.name, state: await this.exchange(target.name, { op: 'state' }) })); }
  command(target, op, body, beforeSend) {
    // Do not rediscover between reading state and writing. A foreground change
    // must never retarget an already-issued mutation to another game.
    return this.serial(() => {
      if (beforeSend) beforeSend();
      return this.exchange(target.name, { op, body,
        session: target.state.session, generation: target.state.generation });
    });
  }
}
module.exports = { AnselClient, failure };
