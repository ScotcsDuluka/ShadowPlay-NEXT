'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const install = require('../osc/Module/Base/Hotkey/hotkey-monitor-lifecycle.js');
const {createMonitorState} = require('../shims/hotkey-monitor-state');

test('page reload retires old writes and delayed old page-start cannot reclaim monitoring', () => {
  const state = {hotkeyMonitor: true, hotkeyMonitorGeneration: 0};
  const monitor = createMonitorState(state);
  const write = (session, stamp, sequence, enable, pageStart = false) => monitor.update({
    pageSession: session, pageStartedAtUnixMs: stamp, sequence, enable, pageStart });
  assert.equal(write('old', 100, 1, false, true), 200);
  assert.equal(state.hotkeyMonitor, false);
  assert.equal(write('new', 200, 1, true, true), 200);
  assert.equal(write('old', 100, 999, false, true), 409);
  assert.equal(write('never-arrived-old', 150, 1, false, true), 409);
  assert.equal(write('new', 200, 3, false), 200);
  assert.equal(write('new', 200, 2, true), 409);
  assert.equal(state.hotkeyMonitor, false, 'active editor stays suppressed without expiry');
  assert.equal(write('new', 200, 4, true), 200);
  assert.equal(state.hotkeyMonitor, true);
  assert.equal(monitor.update({enable: false}), 409, 'legacy in-flight page cannot override session');
  assert.equal(state.hotkeyMonitorGeneration, 4);
  assert.equal(monitor.update({pageSession: 'same-ms-unrelated', pageStartedAtUnixMs: 200,
    sequence: 1, pageStart: true, enable: false}), 409);
  assert.equal(monitor.update({pageSession: 'same-ms-reload', pagePreviousSession: 'new',
    pageStartedAtUnixMs: 200, sequence: 1, pageStart: true, enable: false}), 200);
  assert.equal(write('new', 200, 999, true, true), 409);
});

function fixture(monitor, stamp, storage = new Map()) {
  const shadow = {}, state = {current: {name: 'base'}}, listeners = {}, posts = [];
  let tick, cancelled = 0, fail = false;
  const interval = fn => { tick = fn; return 1; };
  interval.cancel = () => { cancelled++; };
  const services = {
    shadowPlayService: shadow, $state: state, $q: {when: () => Promise.resolve()},
    $rootScope: {$on: (event, fn) => { listeners[event] = fn; return () => { delete listeners[event]; }; }},
    $interval: interval,
    $window: {sessionStorage: {getItem: key => storage.get(key) || null,
      setItem: (key, value) => storage.set(key, value)}},
    $http: {post: async (url, body) => {
      posts.push({url, body});
      if (fail) throw new Error('NodeAPI unavailable');
      const status = monitor.update(body);
      if (status !== 200) throw new Error('monitor HTTP ' + status);
      return {status};
    }}
  };
  const injector = {get: name => services[name]};
  return {shadow, state, posts, injector, install: () => install(injector, {clock: () => stamp}),
    navigate: name => { state.current.name = name; listeners.$stateChangeSuccess(); },
    tick: () => tick(), destroy: () => listeners.$destroy(),
    failure: value => { fail = value; }, cancelled: () => cancelled};
}
const flush = () => new Promise(resolve => setImmediate(resolve));

test('same-millisecond normal page reload and NodeAPI restart preserve the editor guard', async () => {
  const state = {hotkeyMonitor: true, hotkeyMonitorGeneration: 0};
  let backend = createMonitorState(state);
  const mutableBackend = {update: body => backend.update(body)};
  const storage = new Map();
  const first = fixture(mutableBackend, 100, storage); first.install();
  await flush();
  first.navigate('main.preferences.keyboard-shortcuts'); await flush();
  assert.equal(state.hotkeyMonitor, false);
  const next = fixture(mutableBackend, 100, storage); next.install();
  await flush();
  assert.equal(state.hotkeyMonitor, true, 'same-clock replacement is accepted only with predecessor');
  next.navigate('main.preferences.keyboard-shortcuts'); await flush();
  state.hotkeyMonitor = true;
  backend = createMonitorState(state);
  next.tick(); await flush();
  assert.equal(state.hotkeyMonitor, false, 'mounted editor reconciles a cold backend');
  first.tick(); await flush();
  assert.equal(state.hotkeyMonitor, false, 'retired page cannot change replacement state');
  first.destroy();
  assert.equal(state.hotkeyMonitor, false, 'destroyed old page cannot release active replacement');
  next.destroy();
  await flush();
  assert.equal(state.hotkeyMonitor, true, 'destroyed current Angular root releases capture');
});

test('existing NVIDIA editor guard resumes on exit/reload, recovers outage, and installs once', async () => {
  const state = {hotkeyMonitor: true, hotkeyMonitorGeneration: 0};
  const backend = createMonitorState(state);
  const page = fixture(backend, 100);
  const installed = page.install();
  assert.strictEqual(page.install(), installed);
  await flush();
  await page.shadow.setHotkeyMonitoringEnabled(false);
  assert.equal(state.hotkeyMonitor, false, 'explicit capture suppression must stay explicit');
  await page.shadow.setHotkeyMonitoringEnabled(true);
  page.navigate('main.preferences.keyboard-shortcuts');
  await flush();
  assert.equal(state.hotkeyMonitor, false);
  for (let i = 0; i < 5; i++) { page.tick(); await flush(); }
  assert.equal(state.hotkeyMonitor, false, 'heartbeat never expires an active editor');
  page.failure(true);
  page.navigate('main.preferences');
  await flush();
  assert.equal(state.hotkeyMonitor, false, 'failed exit cannot pretend backend resumed');
  page.failure(false);
  page.tick();
  await flush();
  assert.equal(state.hotkeyMonitor, true);
  page.navigate('main.preferences.keyboard-shortcuts');
  await flush();
  assert.equal(state.hotkeyMonitor, false);
  // Hard reload skips the old controller's $destroy. The replacement page
  // explicitly takes a new session; its main menu restores native shortcuts.
  const replacement = fixture(backend, 200);
  replacement.install();
  await flush();
  assert.equal(state.hotkeyMonitor, true);
  page.tick();
  await flush();
  assert.equal(state.hotkeyMonitor, true, 'old page heartbeat cannot mute replacement');
  page.destroy(); replacement.destroy();
  assert.equal(page.cancelled(), 1);
  assert.equal(replacement.cancelled(), 1);
});

test('real monitor HTTP facade suppresses/resumes and rejects reordered sessions', async t => {
  const register = require('../shims/route-floor');
  const runtime = require('../shims/osc-runtime-state');
  const app = express(); register(app);
  const server = http.createServer(app);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  async function request(method, body) {
    return new Promise((resolve, reject) => {
      const req = http.request({hostname: '127.0.0.1', port: server.address().port,
        path: '/ShadowPlay/v.1.0/Hotkey/Monitor', method,
        headers: {'Content-Type': 'application/json'}}, res => {
        let bytes = ''; res.on('data', b => { bytes += b; });
        res.on('end', () => resolve({status: res.statusCode, body: JSON.parse(bytes)}));
      });
      req.on('error', reject); req.end(body ? JSON.stringify(body) : undefined);
    });
  }
  const page = {pageSession: 'http-page', pageStartedAtUnixMs: 100, pageStart: true};
  assert.equal((await request('POST', {...page, sequence: 1, enable: false})).status, 200);
  assert.deepEqual((await request('GET')).body, {enable: false});
  assert.equal((await request('POST', {...page, sequence: 0, enable: true})).status, 409);
  assert.equal((await request('POST', {...page, sequence: 2, enable: true})).status, 200);
  assert.deepEqual((await request('GET')).body, {enable: true});
  assert.equal((await request('POST', {enable: 'false'})).status, 400);
  assert.equal(runtime.hotkeyMonitor, true);
});

test('lifecycle mod is boot loaded once beside the native hotkey bridge', () => {
  const html = require('node:fs').readFileSync(require('node:path').join(__dirname, '../osc/index.html'), 'utf8');
  assert.equal((html.match(/src="\/Module\/Base\/Hotkey\/hotkey-monitor-lifecycle\.js\?/g) || []).length, 1);
  assert.ok(html.indexOf('/Module/Base/Hotkey/hotkey-live.js') < html.indexOf('/Module/Base/Hotkey/hotkey-monitor-lifecycle.js'));
});
