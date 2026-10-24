'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const {normalizeContainerHotkeyEvent: normalize, toggleNativeOsc} = require('../shims/container-hotkey-event.js');
const initialRuntimeState = require('../shims/osc-runtime-state.js');

test('NodeAPI cold start enables global hotkey monitoring', () => {
  assert.equal(initialRuntimeState.hotkeyMonitor, true);
});

test('OpenShare routes to exactly one WindowState toggle', () => {
  const e = {name:'/ShadowPlay/v.1.0/Hotkey',data:{hotkeyId:'OSC',hotKeyName:'OpenShare'}};
  assert.deepEqual(normalize(e), {
    name:'/ShadowPlay/v.1.0/WindowState',data:{windowMsg:'overlayToggle'}
  });
});
test('OverlayToggle aliases the same window-state event', () => {
  const e = {name:'/ShadowPlay/v.1.0/Hotkey',data:{hotkeyId:'OSC',hotKeyName:'overlaytoggle'}};
  assert.equal(normalize(e).data.windowMsg,'overlayToggle');
});
test('other hotkey events retain the native event channel and payload', () => {
  const e = {name:'/ShadowPlay/v.1.0/Hotkey',data:{hotkeyId:'Screenshot',hotKeyName:'Screenshot'}};
  assert.strictEqual(normalize(e),e);
});
test('unexpected OSC event name does not trigger the overlay', () => {
  const e = {name:'/ShadowPlay/v.1.0/Hotkey',data:{hotkeyId:'OSC',hotKeyName:'unknown'}};
  assert.strictEqual(normalize(e),e);
});
test('non-hotkey events remain unchanged', () => {
  const e = {name:'/Duluka/v1/GpuRuntimeState',data:{some:'value'}};
  assert.strictEqual(normalize(e),e);
});

test('native OSC toggle sends exactly one POST and honors 200', async t => {
  const calls = [];
  const server = http.createServer((req,res) => {
    calls.push({method:req.method, path:req.url});
    res.writeHead(200);
    res.end('ok');
  });
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(() => server.close());
  assert.equal(await toggleNativeOsc(server.address().port), true);
  assert.deepEqual(calls,[{method:'POST',path:'/toggle'}]);
});

test('unavailable native OSC host is not falsely reported as delivered', async t => {
  const server = http.createServer((req,res) => {
    res.writeHead(503);
    res.end();
  });
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(() => server.close());
  assert.equal(await toggleNativeOsc(server.address().port), false);
});
