'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const bridge = require('../shims/capture-state-sync');
const shadowplay = require('../NVIDIA Web Helper/Backend/routes/shadowplay');
const duluka = require('../NVIDIA Web Helper/Backend/routes/duluka');
const listen = s => new Promise(r => s.listen(0, '127.0.0.1', () => r(s.address().port)));
const close = s => new Promise(r => s.close(r));
function request(port, method, url, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : '';
    const req = http.request({ hostname: '127.0.0.1', port, path: url, method,
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(data) } }, res => {
      let raw = ''; res.on('data', x => { raw += x; });
      res.on('end', () => resolve({ status: res.statusCode, body: JSON.parse(raw) }));
    }); req.on('error', reject); req.end(data);
  });
}
test('alternate backend bridges explicit record commands without persisted or optimistic record events', async () => {
  const previous = process.env.NV_CAPTURE_DESIRED_BRIDGE;
  process.env.NV_CAPTURE_DESIRED_BRIDGE = '1';
  let mode = 'container', recording = false;
  const commands = [], writes = [], events = [];
  const gateway = http.createServer((req, res) => {
    res.setHeader('content-type', 'application/json');
    if (req.method === 'POST') {
      commands.push(req.url); recording = req.url.endsWith('/start');
      return res.end(JSON.stringify({ accepted: true, action: recording ? 'start' : 'stop', saved: false }));
    }
    res.end(JSON.stringify({ schemaVersion: 1, source: 'NvCapture', runtimeEnabled: true,
      availability: 'Available', workerReady: true, runtimeReady: true, recording,
      recordCommandMode: mode, actualSessionBackend: recording ? 'Duluka' : '', actualRunningEngine: recording ? 'Duluka' : '' }));
  });
  const gwPort = await listen(gateway);
  const app = express();
  app.use((req, _res, next) => {
    if (req.method !== 'POST') return next(); let body = '';
    req.on('data', c => { body += c; }); req.on('end', () => { req.body = JSON.parse(body || '{}'); next(); });
  });
  const sections = {};
  const ctx = { store: { getSection: key => sections[key] || {}, storeSection: (key, value) => { writes.push(key); sections[key] = value; },
    replaceSection: (key, value) => { writes.push(key); sections[key] = value; } },
    socket: { io: { on() {} }, emitChannel: (channel, data) => events.push({ channel, data }) }, logger: { info() {} } };
  shadowplay(app, ctx);
  duluka(app, ctx);
  ctx.captureStateSync = bridge(app, { enabled: true, desiredBridge: true, port: gwPort, pollIntervalMs: 100000 });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    await new Promise(r => ctx.captureStateSync.pollNow(r));
    const start = await request(port, 'POST', '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    assert.equal(start.status, 200); assert.deepEqual(commands, ['/v1/capture/record/start']);
    assert.deepEqual(writes, []); assert.deepEqual(events, []);
    const settings = await request(port, 'POST', '/DulukaCapture/v.1.0/settings', { framerate: 30 });
    assert.equal(settings.status, 200);
    assert.equal(events.length, 1, 'existing settings push remains available');
    assert.equal(events[0].channel, '/DulukaCapture/v.1.0/state');
    assert.equal(events[0].data.framerate, 30);
    events.length = 0;
    assert.deepEqual(writes, ['dulukaCapture']);
    writes.length = 0;
    await new Promise(r => ctx.captureStateSync.pollNow(r));
    assert.equal((await request(port, 'GET', '/ShadowPlay/v.1.0/Record/Running')).body.running, true);
    const stop = await request(port, 'POST', '/ShadowPlay/v.1.0/Record/Enable', { status: false });
    assert.equal(stop.status, 200); assert.deepEqual(commands, ['/v1/capture/record/start', '/v1/capture/record/stop']);
    assert.deepEqual(writes, []); assert.deepEqual(events, []);
    mode = 'legacy'; await new Promise(r => ctx.captureStateSync.pollNow(r));
    assert.equal((await request(port, 'POST', '/ShadowPlay/v.1.0/Record/Enable', { status: true })).status, 503);
    assert.deepEqual(writes, []); assert.deepEqual(events, []);
    // Non-record features retain their prior routes and event semantics.
    assert.equal((await request(port, 'POST', '/ShadowPlay/v.1.0/InstantReplay/Enable', { status: true })).status, 200);
    assert.deepEqual(writes, ['instantreplayEnable']); assert.equal(events.length, 1);
  } finally {
    ctx.captureStateSync.stop(); await close(server); await close(gateway);
    if (previous === undefined) delete process.env.NV_CAPTURE_DESIRED_BRIDGE; else process.env.NV_CAPTURE_DESIRED_BRIDGE = previous;
  }
});
