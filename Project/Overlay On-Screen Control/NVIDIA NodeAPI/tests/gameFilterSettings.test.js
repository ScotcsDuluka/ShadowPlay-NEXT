'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const express = require('express');
const mount = require('../shims/game-filter-route');
const { AnselClient, failure } = require('../shims/ansel-client');

const SETTINGS = '/Duluka/v.1.0/GameFilter/Settings';
const STATE = '/Duluka/v.1.0/GameFilter/State';
const BASE = '/NvCamera/v.1.0';
const DEFAULTS = { mode: 'reshade', allowConnection: true, connectionManager: true };
const clone = value => JSON.parse(JSON.stringify(value));

function makeClient() {
  const counts = { list: 0, state: 0, read: 0, command: 0 };
  const commands = [];
  const pending = {};
  async function waitIfBlocked(operation) {
    const blocked = pending[operation];
    if (!blocked) return;
    delete pending[operation];
    blocked.started();
    await blocked.release;
  }
  function blockNext(operation) {
    let started, resume;
    const startedPromise = new Promise(resolve => { started = resolve; });
    const release = new Promise(resolve => { resume = resolve; });
    pending[operation] = { started, release };
    return { started: startedPromise, resume };
  }
  const client = {
    counts, commands, online: true,
    native: {
      protocol: 1, pid: 12345, executable: 'settings-test.exe', session: 'settings-session', generation: 1,
      foreground: true, enabled: true, stack: ['test/effect'],
      filters: [{ id: 'test/effect', name: 'Existing ReShade effect', enabled: true,
        controls: [{ id: 'Strength', currentValue: [0.75] }] }]
    },
    async list() {
      counts.list++;
      await waitIfBlocked('list');
      return client.online ? ['NVIDIAAnsel.12345.1'] : [];
    },
    async state() {
      counts.state++;
      await waitIfBlocked('state');
      if (!client.online) throw Object.assign(new Error('Test runtime is offline'), { status: 503, code: 'ANSEL_UNAVAILABLE' });
      return { name: 'NVIDIAAnsel.12345.1', state: clone(client.native) };
    },
    async read() { counts.read++; return { name: 'NVIDIAAnsel.12345.1', state: clone(client.native) }; },
    async command(target, op, body) {
      counts.command++;
      commands.push({ op, body: clone(body) });
      await waitIfBlocked('command');
      if (op === 'mods') {
        client.native.enabled = body.globalEnable;
        client.native.filters.forEach(effect => { effect.enabled = body.globalEnable; });
      } else if (op === 'select') client.native.stack[body.stackIdx] = body.type;
      return clone(client.native);
    },
    blockNextState: () => blockNext('state'),
    blockNextList: () => blockNext('list'),
    blockNextCommand: () => blockNext('command')
  };
  return client;
}

async function fixture(t, options = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'duluka-settings-test-'));
  const settingsPath = options.path ? options.path(directory) : path.join(directory, 'settings.json');
  if (options.saved !== undefined) fs.writeFileSync(settingsPath, options.saved, 'utf8');
  const client = options.client || makeClient();
  const events = [];
  let route, server;
  async function start() {
    const app = express();
    route = mount(app, { emit: (channel, data) => events.push({ channel, data }) },
      { client, poll: false, settingsPath });
    app.use((req, res) => res.status(404).json({ unhandled: true }));
    server = await new Promise(resolve => {
      const listener = app.listen(0, '127.0.0.1', () => resolve(listener));
    });
  }
  async function stop() {
    if (route) route.close();
    if (server) await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    server = null;
  }
  await start();
  t.after(async () => {
    await stop();
    fs.rmSync(directory, { recursive: true, force: true });
  });
  return {
    client, events, settingsPath,
    async restart() { await stop(); await start(); },
    poll: () => route.poll(),
    call(method, endpoint, body) {
      const payload = body === undefined ? undefined : typeof body === 'string' ? body : JSON.stringify(body);
      return new Promise((resolve, reject) => {
        const request = http.request({ hostname: '127.0.0.1', port: server.address().port,
          method, path: endpoint, headers: payload === undefined ? {} : {
            'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload)
          } }, response => {
          let raw = '';
          response.on('data', chunk => { raw += chunk; });
          response.on('end', () => {
            try { resolve({ status: response.statusCode, body: JSON.parse(raw) }); }
            catch (error) { reject(error); }
          });
        });
        request.on('error', reject);
        request.end(payload);
      });
    }
  };
}

test('default settings are readable without native discovery or filesystem writes', async t => {
  const f = await fixture(t);
  const response = await f.call('GET', SETTINGS);
  assert.equal(response.status, 200);
  assert.deepEqual(response.body, { bridgeEnabled: true, ...DEFAULTS });
  assert.deepEqual(f.client.counts, { list: 0, state: 0, read: 0, command: 0 });
  assert.equal(fs.existsSync(f.settingsPath), false);
});

test('settings validate all fields, types, unknown fields, JSON shape and HTTP method', async t => {
  const f = await fixture(t);
  const invalid = [
    {}, { mode: 'reshade' }, { ...DEFAULTS, mode: 'ReShade' }, { ...DEFAULTS, mode: 'other' },
    { ...DEFAULTS, allowConnection: 1 }, { ...DEFAULTS, allowConnection: 'true' },
    { ...DEFAULTS, connectionManager: null }, { ...DEFAULTS, connectionManager: 'false' },
    { ...DEFAULTS, enabled: true }, [], 'null', '{invalid-json'
  ];
  for (const body of invalid) {
    const response = await f.call('POST', SETTINGS, body);
    assert.equal(response.status, 400, JSON.stringify(body));
    assert.ok(['INVALID_ARGUMENT', 'INVALID_JSON'].includes(response.body.codeText));
  }
  for (const method of ['PUT', 'PATCH', 'DELETE']) {
    const response = await f.call(method, SETTINGS, DEFAULTS);
    assert.equal(response.status, 405, method);
    assert.equal(response.body.codeText, 'METHOD_NOT_ALLOWED');
  }
  assert.deepEqual((await f.call('GET', SETTINGS)).body, { bridgeEnabled: true, ...DEFAULTS });
  assert.deepEqual(f.client.counts, { list: 0, state: 0, read: 0, command: 0 });
  assert.equal(fs.existsSync(f.settingsPath), false, 'rejected settings must not be persisted');
});

test('saved preferences survive remount and never change the current ReShade effects', async t => {
  const f = await fixture(t);
  const initialEffects = clone(f.client.native);
  for (const configuration of [
    { ...DEFAULTS, allowConnection: false },
    { ...DEFAULTS, connectionManager: false },
    { ...DEFAULTS, mode: 'duluka' },
    { ...DEFAULTS, mode: 'nvidia' },
    DEFAULTS
  ]) {
    const saved = await f.call('POST', SETTINGS, configuration);
    assert.equal(saved.status, 200);
    assert.equal(saved.body.bridgeEnabled,
      configuration.mode === 'reshade' && configuration.allowConnection && configuration.connectionManager);
    assert.deepEqual(JSON.parse(fs.readFileSync(f.settingsPath, 'utf8')), configuration);
    assert.equal(fs.existsSync(f.settingsPath + '.tmp'), false, 'successful save must replace the settings file');
    await f.restart();
    assert.deepEqual((await f.call('GET', SETTINGS)).body, saved.body);
  }
  assert.deepEqual(f.client.counts, { list: 0, state: 0, read: 0, command: 0 });
  assert.deepEqual(f.client.native, initialEffects);
});

for (const [label, configuration] of [
  ['connection permission is off', { ...DEFAULTS, allowConnection: false }],
  ['connection manager is off', { ...DEFAULTS, connectionManager: false }],
  ['Duluka mode is selected', { ...DEFAULTS, mode: 'duluka' }],
  ['NVIDIA mode is selected', { ...DEFAULTS, mode: 'nvidia' }]
]) {
  test(`when ${label}, runtime APIs and polling make zero native calls`, async t => {
    const f = await fixture(t);
    await f.call('GET', STATE);
    await f.call('POST', BASE + '/Capture/Control', { capture: true });
    const initialEffects = clone(f.client.native);
    const beforeSave = clone(f.client.counts);
    const saved = await f.call('POST', SETTINGS, configuration);
    assert.equal(saved.status, 200);
    assert.deepEqual(f.client.counts, beforeSave, 'disconnecting a bridge must not disable native effects');
    assert.ok(f.events.some(event => event.data.type === 'anselState' && event.data.connected === false));
    const disconnectedAt = f.events.length;
    for (const [method, endpoint, body] of [
      ['GET', STATE], ['GET', STATE + '?refresh=1'],
      ['POST', BASE + '/Capture/Control', { capture: true }],
      ['POST', BASE + '/GetFilters', {}], ['POST', BASE + '/Filter/GetInfo', { stackIdx: 0 }],
      ['POST', BASE + '/Filter', { type: 'test/effect', stackIdx: 0 }],
      ['POST', BASE + '/Filter/test%2Feffect/Attribute', { controlId: 'Strength', value: [0.2], stackIdx: 0 }],
      ['POST', BASE + '/Filter/ResetAll', {}], ['POST', BASE + '/EnableMods', { globalEnable: false }]
    ]) {
      const response = await f.call(method, endpoint, body);
      assert.equal(response.status, 503, endpoint);
      assert.equal(response.body.codeText || response.body.code, 'BRIDGE_DISABLED', endpoint);
    }
    assert.deepEqual((await f.call('POST', BASE + '/GetAvailable', {})).body, { available: false });
    assert.deepEqual((await f.call('GET', BASE + '/ReshadeSupported')).body, { reshadeSupported: false });
    assert.deepEqual((await f.call('GET', BASE + '/GetFreestyleSupport')).body,
      { freestyleWhitelisted: false, anselWhitelisted: false, supported: false });
    for (let count = 0; count < 3; count++) await f.poll();
    assert.deepEqual(f.client.counts, beforeSave, 'disabled APIs and heartbeat must not enumerate pipes or touch the game');
    assert.deepEqual(f.client.native, initialEffects);
    assert.equal(f.events.slice(disconnectedAt).some(event => event.data.type === 'anselState' && event.data.connected), false);

    await f.call('POST', SETTINGS, DEFAULTS);
    assert.deepEqual(f.client.counts, beforeSave, 're-enabling must not issue a shader-changing command');
    const reconnected = await f.call('GET', STATE);
    assert.equal(reconnected.status, 200);
    assert.equal(reconnected.body.connected, true);
    assert.equal(f.client.counts.state, beforeSave.state + 1, 're-enable must discover a new editing session');
    assert.equal((await f.call('POST', BASE + '/Capture/Control', { capture: true })).status, 200,
      'the editor must reopen its session before sending new filter mutations');
    const command = await f.call('POST', BASE + '/EnableMods', { globalEnable: false });
    assert.equal(command.status, 200);
    assert.equal(f.client.counts.command, beforeSave.command + 1, 'an explicit user command works after re-enable');
    assert.equal(f.client.native.enabled, false);
  });
}

test('unreadable JSON and invalid saved preferences fall back to defaults', async t => {
  for (const saved of ['{bad-json', JSON.stringify({ ...DEFAULTS, mode: 'unsupported' }),
    JSON.stringify({ ...DEFAULTS, allowConnection: 'false' })]) {
    const f = await fixture(t, { saved });
    assert.deepEqual((await f.call('GET', SETTINGS)).body, { bridgeEnabled: true, ...DEFAULTS });
    assert.deepEqual(f.client.counts, { list: 0, state: 0, read: 0, command: 0 });
    const corrected = { ...DEFAULTS, connectionManager: false };
    assert.equal((await f.call('POST', SETTINGS, corrected)).status, 200);
    await f.restart();
    assert.deepEqual((await f.call('GET', SETTINGS)).body, { bridgeEnabled: false, ...corrected });
  }
});

test('a backend restarted with a disabled saved configuration never contacts the runtime', async t => {
  for (const configuration of [
    { ...DEFAULTS, allowConnection: false }, { ...DEFAULTS, connectionManager: false },
    { ...DEFAULTS, mode: 'duluka' }, { ...DEFAULTS, mode: 'nvidia' }
  ]) {
    const f = await fixture(t, { saved: JSON.stringify(configuration) });
    assert.deepEqual((await f.call('GET', SETTINGS)).body, { bridgeEnabled: false, ...configuration });
    assert.equal((await f.call('GET', STATE)).status, 503);
    assert.equal((await f.call('POST', BASE + '/Filter', { type: 'None', stackIdx: 0 })).status, 503);
    await f.poll();
    assert.deepEqual(f.client.counts, { list: 0, state: 0, read: 0, command: 0 });
  }
});

test('save failure reports an error and leaves active preferences and runtime untouched', async t => {
  const f = await fixture(t, { path: directory => path.join(directory, 'missing-directory', 'settings.json') });
  const initialEffects = clone(f.client.native);
  assert.equal((await f.call('GET', STATE)).status, 200);
  const before = clone(f.client.counts);
  const savedAt = f.events.length;
  const response = await f.call('POST', SETTINGS, { ...DEFAULTS, allowConnection: false });
  assert.equal(response.status, 500);
  assert.equal(response.body.type, 'Error');
  assert.deepEqual((await f.call('GET', SETTINGS)).body, { bridgeEnabled: true, ...DEFAULTS });
  assert.deepEqual(f.client.counts, before);
  assert.equal(f.events.slice(savedAt).some(event => event.data.type === 'anselState' && event.data.connected === false), false);
  assert.deepEqual(f.client.native, initialEffects);
  assert.equal(fs.existsSync(f.settingsPath), false);
});

test('disabling while a snapshot is in flight cannot reconnect the disabled bridge', async t => {
  const f = await fixture(t);
  const blocked = f.client.blockNextState();
  const inFlight = f.call('GET', STATE);
  await blocked.started;
  try {
    assert.equal((await f.call('POST', SETTINGS, { ...DEFAULTS, allowConnection: false })).status, 200);
  } finally { blocked.resume(); }
  const response = await inFlight;
  assert.equal(response.status, 503, 'snapshot begun before disconnect must not publish a connected result afterward');
  assert.equal(response.body.codeText || response.body.code, 'BRIDGE_DISABLED');
  assert.equal(f.events.some(event => event.data.type === 'anselState' && event.data.connected), false);
  assert.equal(f.client.counts.command, 0);
});

test('disabling during mutation discovery cancels the pending native write', async t => {
  const f = await fixture(t);
  const initialEffects = clone(f.client.native);
  const blocked = f.client.blockNextState();
  const inFlight = f.call('POST', BASE + '/Filter', { type: 'None', stackIdx: 0 });
  await blocked.started;
  try {
    assert.equal((await f.call('POST', SETTINGS, { ...DEFAULTS, connectionManager: false })).status, 200);
  } finally { blocked.resume(); }
  const response = await inFlight;
  assert.equal(response.status, 503, 'pending filter command must honor the settings saved during discovery');
  assert.equal(response.body.codeText || response.body.code, 'BRIDGE_DISABLED');
  assert.equal(f.client.counts.command, 0, 'saving disconnect must not be followed by a late shader-changing command');
  assert.deepEqual(f.client.native, initialEffects);
});

test('rapid disconnect and reconnect cannot reuse an old in-flight snapshot or clear the new session', async t => {
  const f = await fixture(t);
  const blocked = f.client.blockNextState();
  const oldRequest = f.call('GET', STATE);
  await blocked.started;
  let fresh, resumedAt;
  try {
    await f.call('POST', SETTINGS, { ...DEFAULTS, allowConnection: false });
    await f.call('POST', SETTINGS, DEFAULTS);
    fresh = await f.call('GET', STATE);
    assert.equal(fresh.status, 200);
    assert.equal(fresh.body.connected, true);
    resumedAt = f.events.length;
  } finally { blocked.resume(); }
  const stale = await oldRequest;
  assert.equal(stale.status, 409, 'restoring the same preferences must not revive the previous request epoch');
  assert.equal(stale.body.codeText || stale.body.code, 'BRIDGE_CHANGED');
  assert.equal(f.client.counts.state, 2, 'the new session must not share the previous in-flight read');
  assert.equal(f.events.slice(resumedAt).some(event => event.data.type === 'anselState' && event.data.connected === false), false,
    'an obsolete read must not disconnect a newer healthy session');
  assert.equal((await f.call('GET', STATE)).body.connected, true);
  assert.equal(f.client.counts.state, 2, 'the new session remains cached after obsolete read completion');
});

test('disabling clears an offline pending shader preference before a later reconnect', async t => {
  const f = await fixture(t);
  f.client.online = false;
  const queued = await f.call('POST', BASE + '/EnableMods', { globalEnable: false });
  assert.equal(queued.status, 200);
  assert.equal(queued.body.applied, false);
  assert.equal(f.client.counts.command, 0);
  const initialEffects = clone(f.client.native);
  await f.call('POST', SETTINGS, { ...DEFAULTS, allowConnection: false });
  f.client.online = true;
  await f.poll();
  await f.call('POST', SETTINGS, DEFAULTS);
  await f.poll();
  assert.equal(f.client.counts.command, 0, 'an old offline preference must not change effects after disconnect/reconnect');
  assert.deepEqual(f.client.native, initialEffects);
  const reconnected = await f.call('GET', STATE);
  assert.equal(reconnected.status, 200);
  assert.equal(reconnected.body.enabled, true);
});

test('a command already sent before disconnect cannot publish a late connected state', async t => {
  const f = await fixture(t);
  await f.call('POST', BASE + '/Capture/Control', { capture: true });
  const blocked = f.client.blockNextCommand();
  const inFlight = f.call('POST', BASE + '/Filter', { type: 'None', stackIdx: 0 });
  await blocked.started;
  let disconnectedAt;
  try {
    assert.equal((await f.call('POST', SETTINGS, { ...DEFAULTS, allowConnection: false })).status, 200);
    disconnectedAt = f.events.length;
  } finally { blocked.resume(); }
  const response = await inFlight;
  assert.equal(response.status, 503, 'a command response must not revive an editor after disconnect');
  assert.equal(response.body.codeText || response.body.code, 'BRIDGE_DISABLED');
  assert.equal(f.client.counts.command, 1, 'only the preexisting explicit command may have been sent');
  assert.equal(f.events.slice(disconnectedAt).some(event => event.data.type === 'anselState' && event.data.connected), false);
  assert.equal((await f.call('GET', STATE)).status, 503);
});

test('disconnect during pending-preference polling stops before a native read or write', async t => {
  const f = await fixture(t);
  f.client.online = false;
  await f.call('POST', BASE + '/EnableMods', { globalEnable: false });
  f.client.online = true;
  const initialEffects = clone(f.client.native);
  const before = clone(f.client.counts);
  const blocked = f.client.blockNextList();
  const polling = f.poll();
  await blocked.started;
  try {
    assert.equal((await f.call('POST', SETTINGS, { ...DEFAULTS, mode: 'duluka' })).status, 200);
  } finally { blocked.resume(); }
  await polling;
  assert.equal(f.client.counts.state, before.state, 'poll must recheck the settings after pipe discovery');
  assert.equal(f.client.counts.command, 0);
  assert.deepEqual(f.client.native, initialEffects);
  assert.equal(f.events.some(event => event.data.type === 'anselState' && event.data.connected), false);
});

test('a native command queued behind another operation rechecks permission immediately before sending', async () => {
  const client = new AnselClient({ list: async () => [] });
  const sent = [];
  client.exchange = async (name, request) => { sent.push({ name, request }); return { enabled: false }; };
  let release, started;
  const ready = new Promise(resolve => { started = resolve; });
  const hold = new Promise(resolve => { release = resolve; });
  const blocker = client.serial(async () => { started(); await hold; });
  await ready;
  let allowed = true, checked = 0;
  const target = { name: 'NVIDIAAnsel.12345.1', state: { session: 'pinned-session', generation: 7 } };
  const pending = client.command(target, 'mods', { globalEnable: false }, () => {
    checked++;
    if (!allowed) throw failure(503, 'BRIDGE_DISABLED', 'Disconnected while queued');
  });
  assert.equal(checked, 0, 'guard must run inside the native queue, not when the command is enqueued');
  allowed = false;
  release();
  await blocker;
  await assert.rejects(pending, error => error.status === 503 && error.code === 'BRIDGE_DISABLED');
  assert.equal(checked, 1);
  assert.deepEqual(sent, [], 'permission revoked while queued must suppress the native exchange');
  allowed = true;
  await client.command(target, 'mods', { globalEnable: false }, () => { assert.equal(allowed, true); });
  assert.equal(sent.length, 1, 'a rejected queued command must not poison later explicit commands');
  assert.equal(sent[0].name, target.name);
  assert.equal(sent[0].request.session, 'pinned-session');
  assert.equal(sent[0].request.generation, 7);
});
