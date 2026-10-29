'use strict';
// Builds an offline Container gateway host; no production ports or worker.
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
const express = require('express');
const install = require('../shims/capture-state-sync');
const floor = require('../shims/route-floor');
const state = require('../shims/osc-runtime-state');
const listen = server => new Promise(r => server.listen(0, '127.0.0.1', () => r(server.address().port)));
const close = server => new Promise(r => server.close(r));
function post(port, url, body) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify(body);
    const req = http.request({ hostname: '127.0.0.1', port, path: url, method: 'POST',
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(data) } }, res => {
      let raw = ''; res.on('data', x => { raw += x; });
      res.on('end', () => resolve({ status: res.statusCode, body: JSON.parse(raw) }));
    });
    req.on('error', reject); req.end(data);
  });
}
function get(port, url) {
  return new Promise((resolve, reject) => http.get({ hostname: '127.0.0.1', port, path: url }, res => {
    let raw = ''; res.on('data', x => { raw += x; }); res.on('end', () => resolve(JSON.parse(raw)));
  }).on('error', reject));
}
test('OSC -> NodeAPI -> actual Container gateway -> stub worker', async () => {
  const buildRoot = path.join(process.env.TEMP, 'ShadowPlay-CaptureGatewayIntegration');
  const project = path.resolve(__dirname, '../../../NVIDIA Container/Tests/CaptureGatewayIntegrationHost.csproj');
  const build = spawnSync('dotnet', ['build', project, '-v:q',
    '-p:BaseIntermediateOutputPath=' + path.join(buildRoot, 'obj') + path.sep,
    '-p:MSBuildProjectExtensionsPath=' + path.join(buildRoot, 'obj') + path.sep,
    '-p:OutputPath=' + path.join(buildRoot, 'bin') + path.sep], { encoding: 'utf8' });
  assert.equal(build.status, 0, build.stdout + build.stderr);
  const old = process.env.NV_CAPTURE_RUNTIME_ENABLED;
  process.env.NV_CAPTURE_RUNTIME_ENABLED = '1';
  state.recordEnabled = false; state.recordRunning = false;
  let recording = false, mode = 'container', available = true;
  let delayStart = false, ambiguousStart = false;
  const commands = [];
  const worker = http.createServer((req, res) => {
    res.setHeader('Content-Type', 'application/json');
    if (!available) { res.statusCode = 503; return res.end('{}'); }
    if (req.method === 'POST') {
      if (ambiguousStart && req.url.endsWith('start')) {
        commands.push(req.url); return res.end('{"unexpected":true}');
      }
      commands.push(req.url); recording = req.url.endsWith('start');
      const reply = JSON.stringify(recording ? { started: true, backend: 'Duluka' } :
        { stopped: true, saved: false, finalizationState: 'Partial' });
      if (delayStart && recording) return setTimeout(() => res.end(reply), 500);
      return res.end(reply);
    }
    if (req.url === '/health') return res.end(JSON.stringify({ ok: true, ready: true, recording }));
    if (req.url === '/state') return res.end(JSON.stringify({ ready: true, recording,
      actualSessionBackend: recording ? 'Duluka' : '', recordCommandMode: mode, elapsedSec: 0 }));
    return res.end(JSON.stringify({ schemaVersion: 2, readOnly: true,
      configured: { backend: 'Duluka', captureApi: 'dxgi_desktop_duplication', encoder: 'NVENC_H264', allowed: true },
      actualSessionBackend: recording ? 'Duluka' : '', nativeRuntimeReady: true,
      nativeRuntimeCodec: 'NVENC_H264', recording,
      ffmpegBinary: { binaryRunnable: true, captureApis: ['ddagrab'], encoders: ['libx264'], hardwareEncoderVerified: false } }));
  });
  const workerPort = await listen(worker);
  const reservation = http.createServer(); const gatewayPort = await listen(reservation); await close(reservation);
  const host = spawn('dotnet', [path.join(buildRoot, 'bin', 'CaptureGatewayIntegrationHost.dll'), String(gatewayPort), String(workerPort)]);
  let hostError = ''; host.stderr.on('data', x => { hostError += x; });
  const ready = new Promise((resolve, reject) => {
    host.stdout.on('data', x => { if (x.toString().includes('READY')) resolve(); });
    host.once('exit', code => reject(new Error('gateway host exited ' + code + ': ' + hostError)));
  });
  let sync, server;
  try {
    await ready;
    const app = express();
    sync = install(app, { enabled: true, desiredBridge: true, port: gatewayPort, pollIntervalMs: 100000 });
    floor(app, { emit() {} }, { captureStateSync: sync, captureBridgeRequested: true });
    server = http.createServer(app); const nodePort = await listen(server);
    await new Promise(r => sync.pollNow(r));
    assert.equal((await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: true })).status, 200);
    await new Promise(r => setTimeout(r, 100));
    assert.deepEqual(commands, ['/record/container/start']);
    await new Promise(r => sync.pollNow(r));
    assert.equal((await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: false })).status, 200);
    await new Promise(r => setTimeout(r, 100));
    assert.deepEqual(commands, ['/record/container/start', '/record/container/stop']);
    // The last poll saw container mode; the worker changes mode before the
    // next command. The command's own preflight must observe the new mode.
    mode = 'legacy';
    assert.equal((await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: true })).status, 503);
    recording = true;
    assert.equal((await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: false })).status, 200,
      'active legacy recording remains stoppable');
    assert.deepEqual(commands, ['/record/container/start', '/record/container/stop', '/record/stop']);
    mode = 'container'; available = false; await new Promise(r => sync.pollNow(r));
    assert.equal((await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: true })).status, 503);
    available = true; await new Promise(r => sync.pollNow(r));
    await new Promise(r => setTimeout(r, 100));
    assert.deepEqual(commands, ['/record/container/start', '/record/container/stop', '/record/stop'], 'no replay on recovery');
    delayStart = true;
    const pendingStart = post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    for (let i = 0; i < 20 && commands.length < 4; i++) await new Promise(r => setTimeout(r, 20));
    assert.equal(commands.length, 4);
    const busy = await post(gatewayPort, '/v1/capture/record/stop', { confirmed: true });
    assert.equal(busy.status, 409);
    assert.equal(busy.body.code, 'capture_command_in_flight');
    const uiBusy = await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: false });
    assert.equal(uiBusy.status, 409);
    assert.equal((await pendingStart).status, 200); delayStart = false;
    await new Promise(r => sync.pollNow(r));
    await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: false });
    await new Promise(r => setTimeout(r, 100));
    await new Promise(r => sync.pollNow(r));
    ambiguousStart = true;
    const ambiguous = await post(nodePort, '/ShadowPlay/v.1.0/Record/Enable', { status: true });
    assert.equal(ambiguous.status, 502);
    assert.equal(ambiguous.body.outcomeUnknown, true);
    await new Promise(r => setTimeout(r, 150));
    const uncertain = await get(gatewayPort, '/v1/capture/status');
    assert.equal(uncertain.startOutcomeUnresolved, true);
    const count = commands.length;
    await new Promise(r => sync.pollNow(r)); await new Promise(r => setTimeout(r, 100));
    assert.equal(commands.length, count, 'ambiguous start must not be retried');
  } finally {
    if (sync) sync.stop(); if (server) await close(server); host.kill(); await close(worker);
    state.recordEnabled = false; state.recordRunning = false;
    if (old === undefined) delete process.env.NV_CAPTURE_RUNTIME_ENABLED; else process.env.NV_CAPTURE_RUNTIME_ENABLED = old;
  }
});
