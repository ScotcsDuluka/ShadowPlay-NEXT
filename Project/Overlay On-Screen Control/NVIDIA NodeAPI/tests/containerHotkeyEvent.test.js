'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const {normalizeContainerHotkeyEvent: normalize, toggleNativeOsc,
  createNativeOscDispatcher} = require('../shims/container-hotkey-event.js');
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

test('20 rapid jittered intents preserve order, correlation, and one native visibility owner', async t => {
  const calls = [];
  let active = 0, maxActive = 0, visible = false;
  const server = http.createServer((req, res) => {
    active++;
    maxActive = Math.max(maxActive, active);
    const id = req.headers['x-osc-request-id'];
    calls.push({ id, stamp: Number(req.headers['x-osc-created-at']) });
    setTimeout(() => { visible = !visible; active--; res.writeHead(200); res.end('ok'); },
      calls.length % 3 === 0 ? 12 : 3);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  const now = Date.now();
  const dispatch = createNativeOscDispatcher({ port: server.address().port,
    clock: () => now, monitor: () => ({ enabled: true, generation: 0 }) });
  const results = await Promise.all(Array.from({length: 20}, (_, i) =>
    dispatch({ requestId: 'cycle-' + i, createdAtUnixMs: now })));
  assert.equal(results.every(Boolean), true);
  assert.equal(maxActive, 1);
  assert.deepEqual(calls.map(c => c.id), Array.from({length: 20}, (_, i) => 'cycle-' + i));
  assert.equal(calls.every(c => c.stamp === now), true);
  assert.equal(visible, false, '10 full open/close mock cycles end closed');
  assert.equal(await dispatch({ requestId: 'cycle-19', createdAtUnixMs: now }), true);
  assert.equal(calls.length, 20, 'duplicate acknowledgement cannot toggle twice');
});

test('queued intents expire and editor generation cancels even after editor exit', async () => {
  let now = 10000, monitor = {enabled: true, generation: 0};
  let release, started;
  const entered = new Promise(resolve => { started = resolve; });
  const calls = [];
  const dispatch = createNativeOscDispatcher({ clock: () => now, monitor: () => monitor,
    send: intent => { calls.push(intent.requestId); started(); return new Promise(resolve => { release = resolve; }); } });
  const first = dispatch({ requestId: 'first', createdAtUnixMs: now });
  await entered;
  const queued = dispatch({ requestId: 'queued', createdAtUnixMs: now });
  monitor = {enabled: false, generation: 1};
  monitor = {enabled: true, generation: 2};
  release(true);
  assert.equal(await first, true);
  assert.equal(await queued, false);
  assert.deepEqual(calls, ['first']);
  assert.equal(await dispatch({ requestId: 'old', createdAtUnixMs: now - 2001 }), false);
  assert.equal(await dispatch({ requestId: 'future', createdAtUnixMs: now + 1001 }), false);
  assert.equal(await dispatch({ requestId: 'bad', createdAtUnixMs: '10000' }), false);
  monitor = {enabled: false, generation: 3};
  assert.equal(await dispatch({ requestId: 'editor', createdAtUnixMs: now }), false);
  assert.deepEqual(calls, ['first']);
});

test('unavailable native endpoint does not poison the next new keypress', async () => {
  let count = 0;
  const dispatch = createNativeOscDispatcher({monitor: () => ({enabled: true, generation: 0}),
    send: async () => { if (++count === 1) throw new Error('backend stopped'); return true; }});
  assert.equal(await dispatch({requestId: 'outage'}), false);
  assert.equal(await dispatch({requestId: 'recovered'}), true);
  assert.equal(count, 2);
});

test('native HTTP timeout is bounded and an ambiguous toggle is never retried', async t => {
  let calls = 0;
  const server = http.createServer((req, res) => { calls++; setTimeout(() => res.end('late'), 1200); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  const dispatch = createNativeOscDispatcher({port: server.address().port,
    monitor: () => ({enabled: true, generation: 0})});
  const before = Date.now();
  assert.equal(await dispatch({requestId: 'ambiguous'}), false);
  assert.ok(Date.now() - before < 1500);
  assert.equal(await dispatch({requestId: 'ambiguous'}), false);
  assert.equal(calls, 1);
});

test('disconnected native dispatcher has a bounded queue and drops overflow', async () => {
  let release;
  const blocked = new Promise(resolve => { release = resolve; });
  const dispatch = createNativeOscDispatcher({monitor: () => ({enabled: true, generation: 0}),
    send: () => blocked});
  const queued = Array.from({length: 64}, (_, i) => dispatch({requestId: 'bounded-' + i}));
  assert.equal(await dispatch({requestId: 'overflow'}), false);
  release(false);
  assert.equal((await Promise.all(queued)).every(ok => ok === false), true);
});
