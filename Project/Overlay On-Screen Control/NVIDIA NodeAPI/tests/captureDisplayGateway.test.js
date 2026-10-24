'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const register = require('../shims/capture-display-routing');

function fakeResponse(resolve) {
  return { status(code) { this.code = code; return this; },
    json(data) { resolve({ code: this.code, data }); } };
}
async function localStub(reply, run) {
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => reply(req, res, body));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try { await run(server.address().port); }
  finally { await new Promise(resolve => server.close(resolve)); }
}
function routeRegistry(port) {
  const get = {}, post = {};
  register({ get(k, fn) { get[k] = fn; }, post(k, fn) { post[k] = fn; } }, { port });
  function invoke(method, path, body) {
    return new Promise(resolve => {
      const route = method === 'GET' ? get[path] : post[path];
      assert.equal(typeof route, 'function');
      route({ body }, fakeResponse(resolve));
    });
  }
  return { invoke };
}
test('display catalog comes from Container and carries exact LUID/output values', async () => {
  await localStub((req, res) => {
    assert.equal(req.method, 'GET');
    assert.equal(req.url, '/v1/capture/displays');
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ schemaVersion: 1, displays: [
      { OutputIndex: 2, AdapterLuidLow: 1, AdapterLuidHigh: 0 }
    ], selectionValid: true }));
  }, async port => {
    const result = await routeRegistry(port).invoke('GET', '/DulukaCapture/v.1.0/displays');
    assert.equal(result.code, 200);
    assert.equal(result.data.displays[0].OutputIndex, 2);
  });
});
test('unavailable Container never fabricates a monitor list', async () => {
  await localStub((req, res) => { res.writeHead(501); res.end('not supported'); },
    async port => {
      const result = await routeRegistry(port).invoke('GET', '/DulukaCapture/v.1.0/displays');
      assert.equal(result.code, 501);
      assert.equal(result.data.availability, 'Unavailable');
    });
});
test('display write requires confirmed stable identity', async () => {
  await localStub(() => { throw Error('unexpected upstream call'); }, async port => {
    const result = await routeRegistry(port).invoke('POST', '/DulukaCapture/v.1.0/display/select',
      { outputIndex: 0, adapterLuidLow: 13, adapterLuidHigh: 7 });
    assert.equal(result.code, 400);
    assert.equal(result.data.saved, false);
  });
});
test('display write forwards exact confirmed values without switching displays itself', async () => {
  await localStub((req, res, body) => {
    assert.equal(req.method, 'POST');
    assert.equal(req.url, '/v1/capture/display/select');
    assert.deepEqual(JSON.parse(body), {
      confirmed: true, outputIndex: 2, adapterLuidLow: 123, adapterLuidHigh: 7
    });
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ saved: true, effectiveNow: false }));
  }, async port => {
    const result = await routeRegistry(port).invoke('POST',
      '/DulukaCapture/v.1.0/display/select',
      { confirmed: true, outputIndex: 2, adapterLuidLow: 123, adapterLuidHigh: 7 });
    assert.equal(result.code, 200);
    assert.equal(result.data.effectiveNow, false);
  });
});
test('screenshot endpoint forwards only explicitly confirmed one-shot command', async () => {
  await localStub((req, res, body) => {
    assert.equal(req.url, '/v1/capture/screenshot');
    assert.deepEqual(JSON.parse(body), { confirmed: true });
    res.writeHead(501); res.end(JSON.stringify({ saved: false, error: 'unavailable' }));
  }, async port => {
    const routes = routeRegistry(port);
    const invalid = await routes.invoke('POST', '/DulukaCapture/v.1.0/screenshot', {});
    assert.equal(invalid.code, 400);
    const unavailable = await routes.invoke('POST',
      '/DulukaCapture/v.1.0/screenshot', { confirmed: true });
    assert.equal(unavailable.code, 501);
    assert.equal(unavailable.data.availability, 'Unavailable');
  });
});
