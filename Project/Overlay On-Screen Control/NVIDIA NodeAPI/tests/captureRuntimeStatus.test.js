'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const register = require('../shims/capture-runtime-status');

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

test('GET reads actual runtime health; legacy POST is read-only compatible', async () => {
  await harness(res => {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true, engine: 'NvCapture', ready: true, recording: false }));
  }, {}, async port => {
    for (const method of ['GET', 'POST']) {
      const answer = await request(port, method);
      assert.equal(answer.code, 200);
      assert.deepEqual(answer.body, { enabled: true, ready: true, recording: false });
    }
  });
});
test('a healthy worker can truthfully report recording and disabled runtime fails closed', async () => {
  await harness(res => { res.writeHead(200); res.end(JSON.stringify({ ok: true, ready: true, recording: true })); },
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
    assert.deepEqual(answer.body, { enabled: false, ready: false, recording: false });
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
  for (const content of ['{', '{"ready":true,"recording":true}', '{"ok":true,"ready":"true","recording":true}', '{"ok":true,"ready":false,"recording":true}']) {
    await harness(res => { res.writeHead(200); res.end(content); }, {}, async port => {
      const answer = await request(port);
      if (content.includes('"ready":false')) {
        assert.equal(answer.code, 200);
        assert.equal(answer.body.recording, false);
      } else {
        assert.equal(answer.code, 502);
        assert.equal(answer.body.ready, false);
      }
    });
  }
});
test('oversized responses are rejected', async () => {
  await harness(res => {
    res.writeHead(200);
    res.end(JSON.stringify({ ok: true, ready: true, recording: false, extra: 'x'.repeat(33000) }));
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
