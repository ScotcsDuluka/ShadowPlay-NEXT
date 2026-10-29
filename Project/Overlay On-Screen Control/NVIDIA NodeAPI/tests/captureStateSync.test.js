'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const install = require('../shims/capture-state-sync');
const registerFloor = require('../shims/route-floor');
const runtimeState = require('../shims/osc-runtime-state');

function snapshot(overrides) {
  return Object.assign({
    schemaVersion: 1, source: 'NvCapture', runtimeEnabled: true,
    availability: 'Available', workerReady: true, runtimeReady: true, recording: false,
    recordCommandMode: 'container',
    actualSessionBackend: '', actualRunningEngine: '', lifecycleState: 'Idle',
    isCapturing: false, workerRuntime: { realFramesCaptured: 0 }
  }, overrides);
}

function fakeSocket() {
  const channelLog = [];
  const connectionHandlers = [];
  return {
    emitChannel: (name, data) => channelLog.push({ name, data }),
    io: { on: (event, handler) => { if (event === 'connection') connectionHandlers.push(handler); } },
    channelLog,
    connect: () => connectionHandlers.forEach(handler => handler({}))
  };
}

// The real backend parses raw JSON bodies itself (index.js); mirror that
// here because the workspace express version has no bundled json middleware.
function rawJsonBody(req, res, next) {
  if (req.method !== 'POST' && req.method !== 'PUT') return next();
  let size = 0;
  const chunks = [];
  req.on('data', c => { size += c.length; if (size > 5 * 1024 * 1024) { req.destroy(); return; } chunks.push(c); });
  req.on('end', () => {
    const raw = Buffer.concat(chunks).toString('utf8');
    try { req.body = raw ? JSON.parse(raw) : {}; } catch (_e) { req.body = raw; }
    next();
  });
  req.on('error', () => next());
}

async function listen(server) {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return server.address().port;
}
async function close(server) {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
async function get(port, path) {
  return new Promise((resolve, reject) => {
    http.get({ hostname: '127.0.0.1', port, path }, res => {
      let raw = '';
      res.on('data', d => { raw += d; });
      res.on('end', () => resolve({ code: res.statusCode, body: JSON.parse(raw) }));
    }).on('error', reject);
  });
}
async function post(port, path, body) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify(body || {});
    const req = http.request({ hostname: '127.0.0.1', port, path, method: 'POST',
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(data) } }, res => {
      let raw = '';
      res.on('data', d => { raw += d; });
      res.on('end', () => resolve({ code: res.statusCode, body: JSON.parse(raw) }));
    });
    req.on('error', reject);
    req.end(data);
  });
}

// Gateway stub: serves /v1/capture/status from a mutable box and records
// record/start|stop commands.
function gatewayBox(initial) {
  const box = { current: initial, commands: [] };
  const server = http.createServer((req, res) => {
    if (req.method === 'GET' && req.url === '/v1/capture/status') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify(box.current));
    }
    if (req.method === 'POST' && (req.url === '/v1/capture/record/start' || req.url === '/v1/capture/record/stop')) {
      box.commands.push(req.url.endsWith('start') ? 'start' : 'stop');
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ accepted: true, started: req.url.endsWith('start'), stopped: req.url.endsWith('stop') }));
    }
    res.writeHead(404).end();
  });
  return { box, server };
}

test('disabled sync serves the unknown payload and never polls', async () => {
  const app = express();
  const sync = install(app, { enabled: false });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    const answer = await get(port, '/DulukaCapture/v.1.0/state');
    assert.equal(answer.code, 200);
    assert.equal(answer.body.stateKnown, false);
    assert.equal(answer.body.recording, null);
    assert.equal(answer.body.reason, 'capture_state_sync_disabled');
    assert.equal(sync.pollCount(), 0);
  } finally { sync.stop(); await close(server); }
});

test('polls the gateway, validates schema, and pushes socket state on change', async () => {
  const { box, server: gateway } = gatewayBox(snapshot());
  const gwPort = await listen(gateway);
  const app = express();
  const socket = fakeSocket();
  const sync = install(app, { enabled: true, port: gwPort, socket, pollIntervalMs: 100000 });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    await new Promise(r => sync.pollNow(r));
    const state = await get(port, '/DulukaCapture/v.1.0/state');
    assert.equal(state.body.recording, false);
    assert.equal(state.body.lifecycleState, 'Idle');
    assert.equal(state.body.runtimeTruth.realFramesCaptured, 0);
    assert.equal(socket.channelLog.length, 1, 'initial change pushed once');

    box.current = snapshot({ recording: true, actualSessionBackend: 'Duluka',
      actualRunningEngine: 'Duluka', lifecycleState: 'Recording', isCapturing: true,
      workerRuntime: { realFramesCaptured: 42, framesEncoded: 42 } });
    await new Promise(r => sync.pollNow(r));
    await new Promise(r => sync.pollNow(r)); // unchanged truth must not re-publish
    assert.equal(socket.channelLog.length, 2, 'changed truth pushed once, stable truth not re-pushed');
    assert.equal(socket.channelLog[1].data.recording, true);
    assert.equal(socket.channelLog[1].data.runtimeTruth.framesEncoded, 42);

    socket.connect();
    assert.equal(socket.channelLog.length, 3, 'new socket client receives current state immediately');
    assert.equal(socket.channelLog[2].name, '/DulukaCapture/v.1.0/state');
  } finally { sync.stop(); await close(server); await close(gateway); }
});

test('invalid gateway payloads publish unknown truth, never fabricated state', async () => {
  const { box, server: gateway } = gatewayBox('{broken');
  // raw string is sent verbatim by the stub; force an invalid shape instead
  box.current = { ready: true, recording: true }; // missing schemaVersion/source
  const gwPort = await listen(gateway);
  const app = express();
  const sync = install(app, { enabled: true, port: gwPort, pollIntervalMs: 100000 });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    await new Promise(r => sync.pollNow(r));
    const state = await get(port, '/DulukaCapture/v.1.0/state');
    assert.equal(state.body.stateKnown, false);
    assert.equal(state.body.recording, null);
    assert.equal(state.body.reason, 'capture_worker_truth_unavailable');
  } finally { sync.stop(); await close(server); await close(gateway); }
});

test('alternate backend can publish runtime updates on a separate socket channel', async () => {
  const { server: gateway } = gatewayBox(snapshot());
  const gwPort = await listen(gateway);
  const socket = fakeSocket();
  const sync = install(express(), { enabled: true, port: gwPort, socket,
    channel: '/DulukaCapture/v.1.0/runtime-state', pollIntervalMs: 100000 });
  try {
    await new Promise(r => sync.pollNow(r));
    assert.equal(socket.channelLog[0].name, '/DulukaCapture/v.1.0/runtime-state');
    assert.equal(socket.channelLog[0].data.recording, false);
  } finally { sync.stop(); await close(gateway); }
});

test('pushes changes in capture activity and worker progress without a lifecycle change', async () => {
  const { box, server: gateway } = gatewayBox(snapshot());
  const gwPort = await listen(gateway);
  const socket = fakeSocket();
  const sync = install(express(), { enabled: true, port: gwPort, socket, pollIntervalMs: 100000 });
  try {
    await new Promise(r => sync.pollNow(r));
    const initial = socket.channelLog.length;
    box.current = snapshot({ busy: true, isCapturing: true, workerRuntime: { realFramesCaptured: 42 } });
    await new Promise(r => sync.pollNow(r));
    assert.equal(socket.channelLog.length, initial + 1);
    assert.equal(socket.channelLog.at(-1).data.busy, true);
    assert.equal(socket.channelLog.at(-1).data.isCapturing, true);
    assert.equal(socket.channelLog.at(-1).data.runtimeTruth.realFramesCaptured, 42);
    box.current.workerRuntime.realFramesCaptured = 43;
    await new Promise(r => sync.pollNow(r));
    assert.equal(socket.channelLog.length, initial + 2);
  } finally { sync.stop(); await close(gateway); }
});

test('opt-in desired bridge sends exactly one verified-truth command', async () => {
  const { box, server: gateway } = gatewayBox(snapshot());
  const gwPort = await listen(gateway);
  const app = express();
  app.use(rawJsonBody);
  const sync = install(app, { enabled: true, port: gwPort, pollIntervalMs: 100000, desiredBridge: true });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    await new Promise(r => sync.pollNow(r)); // truth: idle
    // Desired record=true while worker truth says idle â†’ exactly one start.
    const edge = await post(port, '/DulukaCapture/v.1.0/desired-record', { status: true });
    assert.equal(edge.body.accepted, true);
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, ['start']);
    await new Promise(r => sync.pollNow(r)); // reconcile must not re-send while truth has not flipped
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, ['start'], 'no duplicate start before truth changes');
  } finally { sync.stop(); await close(server); await close(gateway); }
});

test('desired feed cannot send commands when the bridge or runtime is disabled', async () => {
  const { box, server: gateway } = gatewayBox(snapshot());
  const gwPort = await listen(gateway);
  try {
    for (const options of [
      { enabled: true, desiredBridge: false },
      { enabled: false, desiredBridge: true }
    ]) {
      const app = express();
      app.use(rawJsonBody);
      const sync = install(app, Object.assign({ port: gwPort, pollIntervalMs: 100000 }, options));
      const server = http.createServer(app);
      try {
        const port = await listen(server);
        if (options.enabled) await new Promise(r => sync.pollNow(r));
        await post(port, '/DulukaCapture/v.1.0/desired-record', { status: true });
        await new Promise(resolve => setTimeout(resolve, 60));
        assert.deepEqual(box.commands, []);
      } finally { sync.stop(); await close(server); }
    }
  } finally { await close(gateway); }
});

test('a stale desired snapshot at startup does not start a recording', async () => {
  const { box, server: gateway } = gatewayBox(snapshot());
  const gwPort = await listen(gateway);
  let desired = true;
  const desiredServer = http.createServer((_req, res) => {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ status: desired }));
  });
  const localPort = await listen(desiredServer);
  const app = express();
  const sync = install(app, { enabled: true, desiredBridge: true, port: gwPort, localPort, pollIntervalMs: 100000 });
  try {
    await new Promise(resolve => setTimeout(resolve, 100));
    await new Promise(r => sync.pollNow(r));
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, [], 'initial true is not a new start request');
    desired = false;
    await new Promise(r => sync.pollNow(r));
    await new Promise(resolve => setTimeout(resolve, 60));
    desired = true;
    await new Promise(r => sync.pollNow(r));
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, [], 'polling is read-only, even when desired values change');
    await new Promise(resolve => sync.requestRecord(true, resolve));
    assert.deepEqual(box.commands, ['start'], 'an explicit command can start');
  } finally { sync.stop(); await close(desiredServer); await close(gateway); }
});

test('OSC record edges relay through Container when the bridge is enabled', async () => {
  const oldRuntime = process.env.NV_CAPTURE_RUNTIME_ENABLED;
  process.env.NV_CAPTURE_RUNTIME_ENABLED = '1';
  runtimeState.recordEnabled = false;
  runtimeState.recordRunning = false;
  const { box, server: gateway } = gatewayBox(snapshot());
  const gwPort = await listen(gateway);
  const app = express();
  const sync = install(app, { enabled: true, desiredBridge: true, port: gwPort, pollIntervalMs: 100000 });
  registerFloor(app, { emit() {} }, { captureStateSync: sync });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    await new Promise(r => sync.pollNow(r));
    await post(port, '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, ['start']);
    box.current = snapshot({ recording: true, actualSessionBackend: 'Duluka', actualRunningEngine: 'Duluka' });
    await new Promise(r => sync.pollNow(r));
    await post(port, '/ShadowPlay/v.1.0/Record/Enable', { status: false });
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, ['start', 'stop']);
  } finally {
    sync.stop(); await close(server); await close(gateway);
    runtimeState.recordEnabled = false;
    runtimeState.recordRunning = false;
    if (oldRuntime === undefined) delete process.env.NV_CAPTURE_RUNTIME_ENABLED;
    else process.env.NV_CAPTURE_RUNTIME_ENABLED = oldRuntime;
  }
});

test('OSC command while worker truth is unavailable fails and is not replayed on recovery', async () => {
  const oldRuntime = process.env.NV_CAPTURE_RUNTIME_ENABLED;
  process.env.NV_CAPTURE_RUNTIME_ENABLED = '1';
  runtimeState.recordEnabled = false;
  runtimeState.recordRunning = false;
  const { box, server: gateway } = gatewayBox(snapshot({ availability: 'Unavailable', runtimeReady: false, recording: null }));
  const gwPort = await listen(gateway);
  const app = express();
  const sync = install(app, { enabled: true, desiredBridge: true, port: gwPort, pollIntervalMs: 100000 });
  registerFloor(app, { emit() {} }, { captureStateSync: sync });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    await new Promise(r => sync.pollNow(r));
    const response = await post(port, '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    assert.equal(response.code, 503);
    assert.equal(response.body.codeText, 'capture_record_truth_unavailable');
    assert.equal(runtimeState.recordEnabled, false);
    box.current = snapshot();
    await new Promise(r => sync.pollNow(r));
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, [], 'worker recovery must not replay a rejected request');
    const retry = await post(port, '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    assert.equal(retry.code, 200);
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, ['start']);
  } finally {
    sync.stop(); await close(server); await close(gateway);
    runtimeState.recordEnabled = false;
    runtimeState.recordRunning = false;
    if (oldRuntime === undefined) delete process.env.NV_CAPTURE_RUNTIME_ENABLED;
    else process.env.NV_CAPTURE_RUNTIME_ENABLED = oldRuntime;
  }
});

test('bridge fails closed without worker confirmation of Container command mode', async () => {
  for (const mode of [undefined, 'legacy', 'unexpected']) {
    const { box, server: gateway } = gatewayBox(snapshot({ recordCommandMode: mode }));
    const gwPort = await listen(gateway);
    const app = express();
    app.use(rawJsonBody);
    const sync = install(app, { enabled: true, desiredBridge: true, port: gwPort, pollIntervalMs: 100000 });
    const server = http.createServer(app);
    try {
      const port = await listen(server);
      await new Promise(r => sync.pollNow(r));
      const response = await post(port, '/DulukaCapture/v.1.0/desired-record', { status: true });
      assert.equal(response.code, 503);
      await new Promise(resolve => setTimeout(resolve, 30));
      assert.deepEqual(box.commands, []);
    } finally { sync.stop(); await close(server); await close(gateway); }
  }
});

test('requested bridge with disabled runtime rejects the OSC command', async () => {
  const app = express();
  const sync = install(app, { enabled: false, desiredBridge: true });
  registerFloor(app, { emit() {} }, { captureStateSync: sync, captureBridgeRequested: true });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    const response = await post(port, '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    assert.equal(response.code, 503);
    assert.equal(response.body.codeText, 'capture_record_bridge_unavailable');
  } finally { sync.stop(); await close(server); }
});

test('desired bridge with unverified worker truth sends nothing', async () => {
  const { box, server: gateway } = gatewayBox(snapshot({
    availability: undefined, runtimeReady: false, recording: null, startOutcomeUnresolved: true
  }));
  const gwPort = await listen(gateway);
  const app = express();
  app.use(rawJsonBody);
  const sync = install(app, { enabled: true, port: gwPort, pollIntervalMs: 100000, desiredBridge: true });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    await new Promise(r => sync.pollNow(r));
    await post(port, '/DulukaCapture/v.1.0/desired-record', { status: true });
    await new Promise(resolve => setTimeout(resolve, 60));
    assert.deepEqual(box.commands, [], 'unknown truth must never trigger a command');
  } finally { sync.stop(); await close(server); await close(gateway); }
});

test('desired-record endpoint rejects non-boolean status', async () => {
  const app = express();
  app.use(rawJsonBody);
  const sync = install(app, { enabled: true });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    const answer = await post(port, '/DulukaCapture/v.1.0/desired-record', { status: 'yes' });
    assert.equal(answer.code, 400);
    assert.equal(answer.body.accepted, false);
  } finally { sync.stop(); await close(server); }
});

