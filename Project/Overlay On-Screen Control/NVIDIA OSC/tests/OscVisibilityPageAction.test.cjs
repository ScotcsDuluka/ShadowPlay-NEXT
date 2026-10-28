'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const emitter = process.env.OSC_VISIBILITY_TEST_EXE;
assert.ok(emitter, 'Set OSC_VISIBILITY_TEST_EXE to the compiled native regression executable');
function script(generation, open) {
  const result = spawnSync(emitter, ['--emit-js', String(generation), open ? 'open' : 'close'], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout;
}
function fixture(options = {}) {
  let now = 0, next = 0, injectorReady = false, internalOpen = false, transitionBusy = false;
  const timers = new Map(), listeners = {}, acknowledgements = [], failures = [], errors = [], warnings = [];
  const state = { current: { name: 'base' } };
  const calls = { open: 0, close: 0, actualClose: 0 };
  const setTimeout = (fn, delay) => { const id = ++next; timers.set(id, { at: now + delay, fn }); return id; };
  const fire = name => { for (const fn of listeners[name] || []) fn(); };
  const root = { $$phase: false, $apply(fn) { fn(); }, $on(name, fn) { (listeners[name] ||= []).push(fn); } };
  state.go = name => {
    state.transition = {};
    setTimeout(() => { state.current.name = name; state.transition = null; transitionBusy = false; fire('$viewContentLoaded'); }, 30);
  };
  const display = {
    // Original NVIDIA openOSC performs an HTTP promise before starting its route;
    // closeOSC is suppressed while its private transition flag is true.
    openOSC() {
      ++calls.open;
      if (options.dropOpenCalls !== undefined && calls.open <= options.dropOpenCalls) return;
      if (transitionBusy) return;
      if (options.neverResolveOpen) return;
      setTimeout(() => {
        internalOpen = true; transitionBusy = true;
        setTimeout(() => { state.current.name = 'main.main-menu'; }, 30);
        setTimeout(() => { if (options.dropFirstView && calls.open === 1) return;
          transitionBusy = false; fire('$viewContentLoaded'); }, 60);
      }, 300);
    },
    closeOSC() {
      ++calls.close;
      if (!internalOpen || transitionBusy) return;
      ++calls.actualClose; internalOpen = false;
      setTimeout(() => { state.current.name = 'base'; fire('$viewContentLoaded'); }, 30);
    }
  };
  const injector = { get(name) { return { oscDisplayService: display, $rootScope: root, $state: state }[name]; } };
  const context = { document: {}, setTimeout, clearTimeout: id => timers.delete(id),
    angular: { element() { return { injector() { return injectorReady ? injector : null; } }; } },
    console: { error: text => errors.push(text), warn: text => warnings.push(text) }, addEventListener() {},
    cefQuery(options) { const query = JSON.parse(options.request);
      (query.command === 'QUERY_OSC_PAGE_ACTION_ACK' ? acknowledgements : failures).push(query); } };
  context.window = context;
  vm.createContext(context);
  return { calls, state, timers, acknowledgements, failures, errors, display,
    ready() { injectorReady = true; },
    request(g, open) { vm.runInContext(script(g, open), context); },
    until(end) {
      for (let count = 0; count < 1000; ++count) {
        const first = [...timers].sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
        if (!first || first[1].at > end) { now = end; return; }
        timers.delete(first[0]); now = first[1].at; first[1].fn();
      }
      assert.fail('unbounded timer chain');
    }
  };
}

test('delayed injector coalesces OPEN then CLOSE without ever issuing obsolete OPEN', () => {
  const f = fixture(); f.request(1, true); f.until(50); f.request(2, false); f.ready(); f.until(1000);
  assert.equal(f.calls.open, 0); assert.equal(f.state.current.name, 'base');
  assert.deepEqual(f.acknowledgements.map(a => [a.generation, a.open]), [['2', false]]);
  assert.equal(f.timers.size, 0);
});
test('CLOSE waits through already-issued asynchronous NVIDIA OPEN and closes after its view settles', () => {
  const f = fixture(); f.ready(); f.request(1, true); f.until(50); f.request(2, false); f.until(1000);
  assert.equal(f.calls.open, 1); assert.equal(f.calls.actualClose, 1);
  assert.equal(f.state.current.name, 'base');
  assert.deepEqual(f.acknowledgements.map(a => [a.generation, a.open]), [['2', false]]);
  assert.equal(f.timers.size, 0);
});
test('OPEN CLOSE OPEN while the first HTTP is delayed uses one original OPEN and acknowledges latest intent', () => {
  const f = fixture(); f.ready(); f.request(1, true); f.until(50); f.request(2, false); f.until(75); f.request(3, true); f.until(1000);
  assert.equal(f.calls.open, 1); assert.equal(f.calls.actualClose, 0);
  assert.deepEqual(f.acknowledgements.map(a => [a.generation, a.open]), [['3', true]]);
});
test('genuine Escape is preserved after native convergence acknowledgement', () => {
  const f = fixture(); f.ready(); f.request(1, true); f.until(1000); f.display.closeOSC(); f.until(2000);
  assert.equal(f.state.current.name, 'base'); assert.equal(f.calls.open, 1); assert.equal(f.timers.size, 0);
});
test('older queued scripts cannot reverse a newer generation', () => {
  const f = fixture(); f.ready(); f.request(2, false); f.request(1, true); f.until(1000);
  assert.equal(f.calls.open, 0); assert.equal(f.state.current.name, 'base');
});
test('20 jittered toggles preserve final latest intent and never duplicate a pending asynchronous OPEN', () => {
  const f = fixture(); f.ready();
  for (let n = 1; n <= 20; ++n) { f.request(n, n % 2 === 1); f.until(n * 17 + n % 3); }
  f.until(2000); assert.equal(f.state.current.name, 'base');
  assert.equal(f.calls.open, 1); assert.equal(f.acknowledgements.at(-1).generation, '20');
  assert.equal(f.timers.size, 0);
});
test('injector unavailable is bounded and reports failure without orphan timers or fake ACK', () => {
  const f = fixture(); f.request(1, true); f.until(10000);
  assert.equal(f.timers.size, 0); assert.equal(f.acknowledgements.length, 0);
  assert.equal(f.errors.length, 1); assert.match(f.errors[0], /convergence unavailable/);
  assert.equal(f.failures.length, 1); assert.equal(f.failures[0].generation, '1');
});
test('unresolved NVIDIA open HTTP is bounded and reports native recovery instead of fake convergence', () => {
  const f = fixture({ neverResolveOpen: true }); f.ready(); f.request(1, true); f.until(50); f.request(2, false); f.until(10000);
  assert.equal(f.calls.open, 1); assert.equal(f.acknowledgements.length, 0);
  assert.equal(f.timers.size, 0); assert.equal(f.failures.length, 1);
  assert.equal(f.failures[0].generation, '2');
});
test('a silently lost OPEN is re-issued inside the same generation and converges without native recovery', () => {
  // Regression for the 2026-10-26 20:53 wedge: one lost open used to pin
  // openPending forever, so every later OPEN generation skipped openOSC.
  const f = fixture({ dropOpenCalls: 1 }); f.ready(); f.request(1, true); f.until(3000);
  assert.equal(f.calls.open, 2);
  assert.deepEqual(f.acknowledgements.map(a => [a.generation, a.open]), [['1', true]]);
  assert.equal(f.failures.length, 0); assert.equal(f.timers.size, 0);
});
test('a permanently lost OPEN is re-issued at most twice and still reports native recovery instead of fake convergence', () => {
  const f = fixture({ dropOpenCalls: 99 }); f.ready(); f.request(1, true); f.until(6000);
  assert.equal(f.calls.open, 3);
  assert.equal(f.acknowledgements.length, 0);
  assert.equal(f.failures.length, 1); assert.equal(f.failures[0].generation, '1');
  assert.equal(f.timers.size, 0);
});
test('a failed main controller before view-content event repairs stale closed route and permits the next open', () => {
  const f = fixture({ dropFirstView: true }); f.ready(); f.request(1, true); f.until(500);
  assert.equal(f.state.current.name, 'main.main-menu');
  f.request(2, false); f.until(2000);
  assert.equal(f.state.current.name, 'base'); assert.equal(f.acknowledgements.at(-1).generation, '2');
  f.request(3, true); f.until(3000);
  assert.equal(f.state.current.name, 'main.main-menu'); assert.equal(f.acknowledgements.at(-1).generation, '3');
  assert.equal(f.failures.length, 0); assert.equal(f.timers.size, 0);
});

// Reuse the established native-node/RAF harness, executing the real shipped
// Motion engine and decorator. Do not substitute a second animation engine.
function realMotionFixture() {
  const testPath = path.resolve(__dirname, '../../NVIDIA NodeAPI/tests/oscMotion.test.js');
  const source = fs.readFileSync(testPath, 'utf8');
  const marker = source.indexOf('\ntest(');
  assert.ok(marker > 0, 'Motion fixture must precede its tests');
  const loaded = { exports: {} };
  vm.runInNewContext(source.slice(0, marker) + '\nmodule.exports={fixture,open};',
    { require, module: loaded, __dirname: path.dirname(testPath), console });
  return loaded.exports;
}
test('real Motion decorator plus original-close no-op settles animation before one bounded base-route repair', () => {
  const tools = realMotionFixture(), f = tools.fixture(); tools.open(f);
  let repairs = 0, closes = 0;
  const acks = [], nativeState = { current: { name: 'main.main-menu' }, transition: null };
  nativeState.go = name => {
    ++repairs; assert.equal(f.api.getState().phase, 'idle'); assert.equal(f.api.getState().target, 'closed');
    f.emitRoute('$stateChangeStart', {}, { name }, {}, nativeState.current);
    nativeState.current = { name };
    f.emitRoute('$stateChangeSuccess', {}, nativeState.current);
    f.emitRoute('$viewContentLoaded');
  };
  const display = f.decorate({ closeOSC() { ++closes; }, openOSC() {} });
  f.rootScope.$$phase = false; f.rootScope.$apply = fn => fn();
  const injector = { get(name) { return { oscDisplayService: display, $rootScope: f.rootScope, $state: nativeState }[name]; } };
  f.window.angular.element = () => ({ injector: () => injector });
  f.window.cefQuery = options => acks.push(JSON.parse(options.request));
  vm.runInNewContext(script(1, false), { window: f.window, document: f.document,
    angular: f.window.angular, setTimeout: f.clock.setTimeout, clearTimeout: f.clock.clearTimeout, console });
  f.clock.advance(2000);
  assert.equal(nativeState.current.name, 'base'); assert.equal(repairs, 1);
  assert.ok(closes >= 1); assert.equal(acks.at(-1).command, 'QUERY_OSC_PAGE_ACTION_ACK');
  assert.equal(acks.at(-1).open, false); assert.equal(f.frames.size, 0); assert.equal(f.timers.size, 0);
});
test('OPEN on an interactive route left visually closed reuses the real Motion open API before ACK', () => {
  const tools = realMotionFixture(), f = tools.fixture(); tools.open(f); f.api.close(() => {}); f.clock.settle();
  const acks = [], nativeState = { current: { name: 'main.main-menu' } };
  const display = f.decorate({ closeOSC() {}, openOSC() { assert.fail('Route is already open; preserve its original state'); } });
  f.rootScope.$$phase = false; f.rootScope.$apply = fn => fn();
  const injector = { get(name) { return { oscDisplayService: display, $rootScope: f.rootScope, $state: nativeState }[name]; } };
  f.window.angular.element = () => ({ injector: () => injector });
  f.window.cefQuery = options => acks.push(JSON.parse(options.request));
  vm.runInNewContext(script(1, true), { window: f.window, document: f.document,
    angular: f.window.angular, setTimeout: f.clock.setTimeout, clearTimeout: f.clock.clearTimeout, console });
  f.clock.advance(2000);
  assert.equal(f.api.getState().phase, 'idle'); assert.equal(f.api.getState().target, 'open');
  assert.equal(acks.at(-1).command, 'QUERY_OSC_PAGE_ACTION_ACK'); assert.equal(acks.at(-1).open, true);
  assert.equal(f.frames.size, 0); assert.equal(f.timers.size, 0);
});
