'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const state = require('../shims/osc-runtime-state');
const register = require('../shims/route-floor');

function jsonGet(port, path) {
  return new Promise((resolve, reject) => {
    http.get({ hostname: '127.0.0.1', port, path }, response => {
      let bytes = '';
      response.on('data', chunk => { bytes += chunk; });
      response.on('end', () => resolve({
        status: response.statusCode, data: JSON.parse(bytes)
      }));
    }).on('error', reject);
  });
}
function jsonPost(port, path, body) {
  return new Promise((resolve, reject) => {
    const bytes = JSON.stringify(body);
    const req = http.request({ hostname: '127.0.0.1', port, path,
      method: 'POST', headers: { 'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(bytes) } }, response => {
      let result = '';
      response.on('data', c => { result += c; });
      response.on('end', () => resolve({
        status: response.statusCode, data: JSON.parse(result)
      }));
    });
    req.on('error', reject);
    req.end(bytes);
  });
}
test('legacy GET reads Container worker truth; a crash never yields Saved', async () => {
  const prevPort = process.env.NV_CONTAINER_CONTROL_API_PORT;
  const prevRuntime = process.env.NV_CAPTURE_RUNTIME_ENABLED;
  let upstream = { availability: 'Available', runtimeReady: true,
    recording: false, actualSessionBackend: '',
    startOutcomeUnresolved: false, stopOutcomeUnresolved: false };
  const upstreamServer = http.createServer((req, res) => {
    if (req.url !== '/v1/capture/status') { res.writeHead(404); return res.end('{}'); }
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify(upstream));
  });
  await new Promise(resolve => upstreamServer.listen(0, '127.0.0.1', resolve));
  process.env.NV_CONTAINER_CONTROL_API_PORT = String(upstreamServer.address().port);
  process.env.NV_CAPTURE_RUNTIME_ENABLED = '1';
  const events = [];
  const app = express();
  register(app, { emit(name, data) { events.push({ name, data }); } });
  const server = http.createServer(app);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  try {
    // Simulate a worker dying while NodeAPI still remembers "Recording".
    state.recordRunning = true;
    state.recordEnabled = false;
    const idle = await jsonGet(port, '/ShadowPlay/v.1.0/Record/Running');
    assert.equal(idle.status, 200);
    assert.deepEqual(idle.data, { running: false });
    assert.equal(state.recordRunning, false);
    assert.deepEqual(events.map(e => e.name),
      ['/ShadowPlay/v.1.0/Record/Running'],
      'repair only the running indicator, never the saved toast');
    events.length = 0;
    const view = await jsonGet(port, '/ShadowPlay/v.1.0/Capture/State');
    assert.deepEqual(view.data, { captureMode: 0, recordingState: 0 });
    assert.deepEqual(events, []);

    upstream = { ...upstream, recording: true, actualSessionBackend: 'Duluka' };
    const active = await jsonGet(port, '/ShadowPlay/v.1.0/Record/Running');
    assert.deepEqual(active.data, { running: true });
    assert.equal(state.recordRunning, true);
    events.length = 0;

    // Worker becomes unavailable: must not assert idle or "saved".
    upstream = { ...upstream, availability: 'Unavailable', runtimeReady: false };
    const unavailable = await jsonGet(port, '/ShadowPlay/v.1.0/Record/Running');
    assert.equal(unavailable.status, 503);
    assert.equal(unavailable.data.codeText, 'capture_worker_truth_unavailable');
    assert.equal(state.recordRunning, true, 'unknown truth must not rewrite cached state');
    assert.deepEqual(events, []);
    const badView = await jsonGet(port, '/ShadowPlay/v.1.0/Capture/State');
    assert.equal(badView.status, 503);

    // A confirmed session ending must retire the desired Start edge too.
    await jsonPost(port, '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    assert.equal(state.recordEnabled, true);
    const ending = await jsonPost(port, '/Duluka/v.1.0/Actual',
      { running: false, recordingSaved: false });
    assert.equal(ending.status, 200);
    assert.equal(state.recordEnabled, false);
    assert.equal(state.recordRunning, false);
    const after = await jsonGet(port, '/Duluka/v.1.0/State');
    assert.equal(after.data.recordState, 'Idle');
    assert.equal(events.filter(e =>
      e.name === '/ShadowPlay/v.1.0/Record/Enable' &&
      e.data.status === false).length, 0, 'false saved toast forbidden');
  } finally {
    await new Promise(resolve => server.close(resolve));
    await new Promise(resolve => upstreamServer.close(resolve));
    state.recordEnabled = false;
    state.recordRunning = false;
    if (prevPort === undefined) delete process.env.NV_CONTAINER_CONTROL_API_PORT;
    else process.env.NV_CONTAINER_CONTROL_API_PORT = prevPort;
    if (prevRuntime === undefined) delete process.env.NV_CAPTURE_RUNTIME_ENABLED;
    else process.env.NV_CAPTURE_RUNTIME_ENABLED = prevRuntime;
  }
});
