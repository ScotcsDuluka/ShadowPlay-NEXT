'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const register = require('../shims/capture-engine-status');

function mockResponse(resolve) {
  return {
    status(code) { this.code = code; return this; },
    json(data) { resolve({ code: this.code, data }); }
  };
}
async function withServer(reply, run) {
  const server = http.createServer((_req, res) => reply(res));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    await run(server.address().port);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
}
async function invoke(port) {
  let route;
  register({ get(path, callback) {
    assert.equal(path, '/DulukaCapture/v.1.0/engines');
    route = callback;
  } }, { port });
  return new Promise(resolve => route({}, mockResponse(resolve)));
}

test('captures API provides read-only catalog from Container without changing settings', async () => {
  await withServer(res => {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ schemaVersion: 1, readOnly: true,
      providers: [{ id: 'Duluka', runtimeReady: false }] }));
  }, async port => {
    const response = await invoke(port);
    assert.equal(response.code, 200);
    assert.equal(response.data.readOnly, true);
    assert.equal(response.data.providers[0].id, 'Duluka');
  });
});
test('catalog cannot present HTTP failure as readiness', async () => {
  await withServer(res => { res.writeHead(503); res.end('unavailable'); }, async port => {
    const response = await invoke(port);
    assert.equal(response.code, 503);
    assert.equal(response.data.captureRuntimeReady, false);
  });
});
test('catalog rejects invalid schema', async () => {
  await withServer(res => { res.writeHead(200); res.end(JSON.stringify({ providers: [] })); }, async port => {
    const response = await invoke(port);
    assert.equal(response.code, 503);
  });
});
test('catalog rejects malformed JSON', async () => {
  await withServer(res => { res.writeHead(200); res.end('{'); }, async port => {
    const response = await invoke(port);
    assert.equal(response.code, 503);
  });
});

test('passes through worker actual route without claiming a new provider ready', async () => {
  await withServer(res => {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
      schemaVersion: 1, readOnly: true,
      selected: { captureId: 'duluka:dxgi', policyOnly: true },
      providers: [{ id: 'FFmpeg', routeWired: true, runtimeReady: false }],
      workerRuntime: {
        schemaVersion: 2, readOnly: true,
        configured: { backend: 'FFmpeg', allowed: true, captureApi: 'gdigrab' },
        actualSessionBackend: 'FFmpeg'
      }
    }));
  }, async port => {
    const result = await invoke(port);
    assert.equal(result.code, 200);
    assert.equal(result.data.selected.policyOnly, true);
    assert.equal(result.data.workerRuntime.configured.backend, 'FFmpeg');
    assert.equal(result.data.workerRuntime.actualSessionBackend, 'FFmpeg');
    assert.equal(result.data.providers[0].runtimeReady, false);
  });
});
