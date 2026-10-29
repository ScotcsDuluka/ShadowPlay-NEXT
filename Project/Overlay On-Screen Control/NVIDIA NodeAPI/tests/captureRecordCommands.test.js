'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const register = require('../shims/capture-record-commands');

async function listen(server) {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return server.address().port;
}
async function close(server) {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
async function post(port, path, body) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? '' : JSON.stringify(body);
    const req = http.request({
      hostname: '127.0.0.1', port, path, method: 'POST',
      headers: data ? { 'content-type': 'application/json', 'content-length': Buffer.byteLength(data) } : {}
    }, res => {
      let raw = '';
      res.on('data', d => { raw += d; });
      res.on('end', () => {
        let parsed = null;
        try { parsed = JSON.parse(raw); } catch (_e) { }
        resolve({ code: res.statusCode, body: parsed, raw });
      });
    });
    req.on('error', reject);
    req.end(data);
  });
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

async function harness(gatewayHandler, options, verify) {
  const gateway = http.createServer(gatewayHandler);
  const gatewayPort = await listen(gateway);
  const app = express();
  app.use(rawJsonBody);
  register(app, { enabled: true, port: gatewayPort, ...(options || {}) });
  const api = http.createServer(app);
  try {
    const apiPort = await listen(api);
    await verify(apiPort, gatewayPort);
  } finally {
    await close(api);
    await close(gateway);
  }
}

test('disabled runtime rejects record commands with 403 and never dials the gateway', async () => {
  let dialed = 0;
  await harness((_req, res) => { dialed += 1; res.writeHead(200).end('{}'); },
    { enabled: false }, async port => {
      const answer = await post(port, '/DulukaCapture/v.1.0/record/start', { confirmed: true });
      assert.equal(answer.code, 403);
      assert.equal(answer.body.error, 'capture_record_commands_disabled');
      assert.equal(dialed, 0);
    });
});

test('only {"confirmed":true} is accepted', async () => {
  await harness((_req, res) => res.writeHead(200).end('{}'), {}, async port => {
    for (const body of [undefined, {}, { confirmed: false }, { confirmed: true, extra: 1 }, 'confirmed']) {
      const answer = await post(port, '/DulukaCapture/v.1.0/record/stop', body);
      assert.equal(answer.code, 400, `body ${JSON.stringify(body)} must be rejected`);
      assert.equal(answer.body.error, 'capture_record_confirmation_required');
    }
  });
});

test('confirmed start/stop relay the gateway verdict verbatim', async () => {
  await harness((req, res) => {
    if (req.method === 'POST' && req.url === '/v1/capture/record/start') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ started: true, backend: 'Duluka', session: 's-1' }));
    }
    if (req.method === 'POST' && req.url === '/v1/capture/record/stop') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ stopped: true, saved: true, finalizationState: 'Saved',
        media: { verified: true, videoStreams: 1 } }));
    }
    res.writeHead(404).end();
  }, {}, async port => {
    const start = await post(port, '/DulukaCapture/v.1.0/record/start', { confirmed: true });
    assert.equal(start.code, 200);
    assert.equal(start.body.started, true);
    assert.equal(start.body.backend, 'Duluka');
    const stop = await post(port, '/DulukaCapture/v.1.0/record/stop', { confirmed: true });
    assert.equal(stop.code, 200);
    assert.equal(stop.body.stopped, true);
    assert.equal(stop.body.saved, true);
    assert.equal(stop.body.finalizationState, 'Saved');
  });
});

test('upstream gateway 403 (policy disabled) passes through untouched', async () => {
  await harness((_req, res) => {
    res.writeHead(403, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ code: 'record_gateway_disabled', message: 'Enable captureRuntime.allowRecordCommands explicitly.' }));
  }, {}, async port => {
    const answer = await post(port, '/DulukaCapture/v.1.0/record/start', { confirmed: true });
    assert.equal(answer.code, 403);
    assert.equal(answer.body.code, 'record_gateway_disabled');
    assert.equal(answer.body.relayed, undefined, 'verbatim body, no wrapper fields');
  });
});

test('non-JSON 200 upstream never becomes a fabricated success', async () => {
  await harness((_req, res) => { res.writeHead(200); res.end('gateway hiccup'); }, {},
    async port => {
      const answer = await post(port, '/DulukaCapture/v.1.0/record/start', { confirmed: true });
      assert.equal(answer.code, 502);
      assert.equal(answer.body.error, 'capture_gateway_invalid_response');
      assert.equal(answer.body.upstreamStatus, 200);
    });
});

test('transport timeout reports outcome-unknown instead of failure', async () => {
  await harness((_req, res) => { setTimeout(() => res.writeHead(200).end('{}'), 200); },
    { timeoutMs: 30 }, async port => {
      const answer = await post(port, '/DulukaCapture/v.1.0/record/stop', { confirmed: true });
      assert.equal(answer.code, 504);
      assert.equal(answer.body.error, 'capture_record_stop_outcome_unknown');
    });
});

test('gateway disconnect after receiving a command reports unknown outcome', async () => {
  await harness((req) => {
    req.resume();
    req.on('end', () => req.socket.destroy());
  }, {}, async port => {
    const answer = await post(port, '/DulukaCapture/v.1.0/record/start', { confirmed: true });
    assert.equal(answer.code, 504);
    assert.equal(answer.body.error, 'capture_record_start_outcome_unknown');
  });
});

test('unreachable gateway fails closed as unavailable', async () => {
  const app = express();
  app.use(rawJsonBody);
  register(app, { enabled: true, port: 59999 });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    const answer = await post(port, '/DulukaCapture/v.1.0/record/start', { confirmed: true });
    assert.equal(answer.code, 503);
    assert.equal(answer.body.error, 'capture_gateway_unavailable');
  } finally { await close(server); }
});
