'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const register = require('../shims/capture-runtime-status');
function snapshot(overrides) {
  return { schemaVersion: 1, source: 'NvCapture', runtimeEnabled: true,
    workerReady: true, runtimeReady: true, recording: false, ...overrides };
}

async function listen(server) {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return server.address().port;
}
async function close(server) {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
async function request(port, method) {
  return new Promise((resolve, reject) => {
    const req = http.request({
      hostname: '127.0.0.1', port, path: '/DulukaCapture/v.1.0/status',
      method: method || 'GET'
    }, res => {
      let body = '';
      res.on('data', d => { body += d; });
      res.on('end', () => resolve({ code: res.statusCode, body: JSON.parse(body) }));
    });
    req.on('error', reject);
    req.end();
  });
}
async function harness(reply, opts, verify) {
  const worker = http.createServer((_req, res) => reply(res));
  const port = await listen(worker);
  const app = express();
  register(app, { enabled: true, port, ...opts });
  const api = http.createServer(app);
  try {
    const apiPort = await listen(api);
    await verify(apiPort);
  } finally {
    await close(api);
    await close(worker);
  }
}

test('GET reads gateway runtime readiness; legacy POST is read-only compatible', async () => {
  await harness(res => {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(snapshot()));
  }, {}, async port => {
    for (const method of ['GET', 'POST']) {
      const answer = await request(port, method);
      assert.equal(answer.code, 200);
      assert.equal(answer.body.enabled, true);
      assert.equal(answer.body.ready, true);
      assert.equal(answer.body.recording, false);
      assert.equal(answer.body.stateKnown, true);
    }
  });
});
test('a healthy worker can truthfully report recording and disabled runtime fails closed', async () => {
  await harness(res => { res.writeHead(200); res.end(JSON.stringify(snapshot({ recording: true }))); },
    {}, async port => {
      const answer = await request(port);
      assert.equal(answer.code, 200);
      assert.equal(answer.body.recording, true);
    });
  const app = express();
  register(app, { enabled: false });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    const answer = await request(port);
    assert.deepEqual(answer.body, {
      enabled: false, ready: false, recording: null, stateKnown: false,
      workerReady: false, actualSessionBackend: '', actualRunningEngine: '',
      lifecycleState: 'Unknown', busy: null, isCapturing: null,
      runtimeTruth: null, codeText: 'CAPTURE_RUNTIME_DISABLED'
    });
  } finally { await close(server); }
});
test('non-200 upstream does not claim readiness', async () => {
  await harness(res => { res.writeHead(503); res.end('nope'); }, {}, async port => {
    const answer = await request(port);
    assert.equal(answer.code, 503);
    assert.equal(answer.body.ready, false);
  });
});
test('rejects malformed JSON and success-shaped invalid payload', async () => {
  for (const content of ['{', '{"ready":true,"recording":true}', JSON.stringify(snapshot({ runtimeReady: 'true' }))]) {
    await harness(res => { res.writeHead(200); res.end(content); }, {}, async port => {
      const answer = await request(port);
      assert.equal(answer.code, 502);
      assert.equal(answer.body.ready, false);
      assert.equal(answer.body.recording, null);
    });
  }
});
test('oversized responses are rejected', async () => {
  await harness(res => {
    res.writeHead(200);
    res.end(JSON.stringify(snapshot({ extra: 'x'.repeat(132000) })));
  }, {}, async port => {
    const answer = await request(port);
    assert.equal(answer.code, 503);
    assert.equal(answer.body.ready, false);
  });
});
test('unreachable worker and invalid ports fail closed', async () => {
  const app = express();
  register(app, { enabled: true, port: 65536 });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    const answer = await request(port);
    assert.equal(answer.code, 503);
    assert.equal(answer.body.codeText, 'CAPTURE_RUNTIME_INVALID_PORT');
  } finally { await close(server); }
});
test('worker ready does not mean selected engine ready and active recording is never masked', async () => {
  await harness(res => { res.writeHead(200); res.end(JSON.stringify(snapshot({
    runtimeReady: false, recording: true, actualRunningEngine: 'Duluka', busy: true
  }))); }, {}, async port => {
    const answer = await request(port);
    assert.equal(answer.body.ready, false);
    assert.equal(answer.body.workerReady, true);
    assert.equal(answer.body.recording, true);
    assert.equal(answer.body.actualRunningEngine, 'Duluka');
    assert.equal(answer.body.busy, true);
  });
});
test('unknown recording state stays unknown through the gateway', async () => {
  await harness(res => { res.writeHead(200); res.end(JSON.stringify(snapshot({
    workerReady: false, runtimeReady: false, recording: null
  }))); }, {}, async port => {
    const answer = await request(port);
    assert.equal(answer.body.recording, null);
    assert.equal(answer.body.stateKnown, false);
  });
});
