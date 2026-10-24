'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const state = require('../shims/osc-runtime-state');
const register = require('../shims/route-floor');

async function harness(run) {
  const prior = process.env.NV_CAPTURE_RUNTIME_ENABLED;
  process.env.NV_CAPTURE_RUNTIME_ENABLED = '1';
  state.recordEnabled = false;
  state.recordRunning = false;
  const events = [];
  const app = express();
  register(app, { emit(name, data) { events.push({ name, data }); } });
  const server = http.createServer(app);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  async function post(path, body) {
    const json = JSON.stringify(body);
    return new Promise((resolve, reject) => {
      const req = http.request({
        hostname: '127.0.0.1', port, path, method: 'POST',
        headers: { 'Content-Type': 'application/json',
          'Content-Length': Buffer.byteLength(json) }
      }, res => {
        let bytes = '';
        res.on('data', x => { bytes += x; });
        res.on('end', () => resolve({ code: res.statusCode, body: JSON.parse(bytes) }));
      });
      req.on('error', reject);
      req.end(json);
    });
  }
  try { await run({ post, events }); }
  finally {
    await new Promise(resolve => server.close(resolve));
    state.recordEnabled = false;
    state.recordRunning = false;
    if (prior === undefined) delete process.env.NV_CAPTURE_RUNTIME_ENABLED;
    else process.env.NV_CAPTURE_RUNTIME_ENABLED = prior;
  }
}

test('actual start and unverified stop do not emit a saved notification', async () => {
  await harness(async ({ post, events }) => {
    assert.equal((await post('/Duluka/v.1.0/Actual', { running: true })).code, 200);
    assert.equal(state.recordRunning, true);
    assert.ok(events.some(e => e.name.endsWith('/Record/Enable') && e.data.status === true));
    events.length = 0;
    assert.equal((await post('/Duluka/v.1.0/Actual', { running: false })).code, 200);
    assert.equal(state.recordRunning, false);
    assert.deepEqual(events.map(e => e.name), ['/ShadowPlay/v.1.0/Record/Running']);
  });
});

test('requested stop does not fake Recording saved while actual session is running', async () => {
  await harness(async ({ post, events }) => {
    await post('/ShadowPlay/v.1.0/Record/Enable', { status: true });
    assert.equal(state.recordEnabled, true);
    assert.deepEqual(events, []);
    await post('/Duluka/v.1.0/Actual', { running: true });
    events.length = 0;
    await post('/ShadowPlay/v.1.0/Record/Enable', { status: false });
    assert.equal(state.recordEnabled, false);
    assert.equal(state.recordRunning, true);
    assert.deepEqual(events, [], 'no optimistic saved notification on requested stop');
  });
});

test('verified native completion triggers exactly one stop/saved event', async () => {
  await harness(async ({ post, events }) => {
    await post('/Duluka/v.1.0/Actual', { running: true });
    events.length = 0;
    const completed = { running: false, recordingSaved: true, outputPath: 'C:\\Test\\record.mp4' };
    assert.equal((await post('/Duluka/v.1.0/Actual', completed)).code, 200);
    assert.deepEqual(events.map(e => e.name), [
      '/ShadowPlay/v.1.0/Record/Enable',
      '/ShadowPlay/v.1.0/Record/Running'
    ]);
    assert.equal(events[0].data.status, false);
    events.length = 0;
    await post('/Duluka/v.1.0/Actual', completed);
    assert.deepEqual(events, [], 'retransmitted completion must not toast twice');
  });
});
