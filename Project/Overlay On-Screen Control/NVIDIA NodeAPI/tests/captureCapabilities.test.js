'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const register = require('../shims/capture-capabilities');

async function listen(server) {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return server.address().port;
}
async function close(server) {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
async function query(port) {
  return new Promise((resolve, reject) => {
    const request = http.get({
      hostname: '127.0.0.1', port,
      path: '/DulukaCapture/v.1.0/capabilities'
    }, res => {
      let raw = '';
      res.on('data', chunk => { raw += chunk; });
      res.on('end', () => resolve({ code: res.statusCode, body: JSON.parse(raw) }));
    });
    request.on('error', reject);
  });
}
async function harness(upstream, options, verify) {
  const worker = http.createServer(upstream);
  const port = await listen(worker);
  const app = express();
  register(app, { port, ...(options || {}) });
  const api = http.createServer(app);
  try {
    const apiPort = await listen(api);
    await verify(apiPort);
  } finally {
    await close(api);
    await close(worker);
  }
}

function response(res, code, body) {
  res.writeHead(code, { 'Content-Type': 'application/json' });
  res.end(typeof body === 'string' ? body : JSON.stringify(body));
}
function valid() {
  return {
    schemaVersion: 1, source: 'NvCapture',
    availability: 'Unavailable', workerError: 'endpoint_not_found',
    policyValid: false, providers: [], workflows: {}
  };
}
test('capabilities transparently preserve authoritative unavailable and invalid policy', async () => {
  const body = valid();
  await harness((_req, res) => response(res, 200, body), {}, async port => {
    const result = await query(port);
    assert.equal(result.code, 200);
    assert.deepEqual(result.body, body);
    assert.equal(result.body.policyValid, false);
  });
});
test('capabilities can pass through verified catalog without editing it', async () => {
  const body = { ...valid(), availability: 'Available', policyValid: true,
    providers: [{ id: 'Duluka', runtimeReady: false }] };
  await harness((_req, res) => response(res, 200, body), {}, async port => {
    const result = await query(port);
    assert.equal(result.code, 200);
    assert.deepEqual(result.body, body);
  });
});
test('non-200 upstream, including undeployed 404, fails closed', async () => {
  await harness((_req, res) => response(res, 404, { error: 'not_found' }), {},
    async port => {
      const result = await query(port);
      assert.equal(result.code, 503);
      assert.equal(result.body.availability, 'Unavailable');
      assert.equal(result.body.policyValid, false);
      assert.equal(result.body.codeText, 'CAPTURE_CAPABILITIES_UPSTREAM_UNAVAILABLE');
      assert.equal(result.body.providers, undefined);
    });
});
test('malformed or invalid schema never returns availability from an untrusted body', async () => {
  for (const body of ['{', { ...valid(), providers: null },
      { ...valid(), schemaVersion: 99 },
      { ...valid(), policyValid: 'true' }]) {
    await harness((_req, res) => response(res, 200, body), {}, async port => {
      const result = await query(port);
      assert.equal(result.code, 503);
      assert.equal(result.body.policyValid, false);
    });
  }
});
test('oversized response is bounded and returns unavailable', async () => {
  await harness((_req, res) => response(res, 200,
    { ...valid(), extra: 'x'.repeat(135000) }), {}, async port => {
    const result = await query(port);
    assert.equal(result.code, 503);
    assert.equal(result.body.availability, 'Unavailable');
  });
});
test('invalid Container port is rejected without dialing anywhere', async () => {
  const app = express();
  register(app, { port: 65536 });
  const server = http.createServer(app);
  try {
    const port = await listen(server);
    const result = await query(port);
    assert.equal(result.code, 503);
    assert.equal(result.body.codeText, 'CAPTURE_CAPABILITIES_INVALID_PORT');
  } finally {
    await close(server);
  }
});
