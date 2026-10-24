'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Test the actual CEF module in a minimal non-DOM VM. Append test-only
// introspection IN MEMORY: the production file and its browser globals are
// unchanged. No REST connection or real capture backend is started.
const source = fs.readFileSync(
  path.join(__dirname, '../osc/mods/video-capture-options.js'), 'utf8');
const ending = /\}\)\(\);\s*$/;
assert.match(source, ending, 'CEF module IIFE ending changed');
const exposed = source.replace(ending, `
  window.__captureUiTest = {
    prime: function () {
      statusNode = { textContent: '', classList: {
        values: new Set(),
        add: function (v) { this.values.add(v); },
        remove: function (v) { this.values.delete(v); }
      }};
      previewNode = { textContent: '' };
      runtimeStatusNode = { textContent: '', classList: {
        values: new Set(),
        add: function (v) { this.values.add(v); },
        remove: function (v) { this.values.delete(v); }
      }};
      return { scope: createPanelScope(), status: statusNode, runtime: runtimeStatusNode };
    },
    fail: setUnavailableSettings,
    state: function () { return state; },
    setState: function (value) { state = value; },
    valid: usableCaptureSettings,
    save: save,
    refreshStatus: refreshCaptureRuntimeStatus
  };
})();
`);

function startHarness() {
  const requests = [];
  let nextResponse = () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(goodSettings()) });
  const win = {
    location: { hash: '#/base/preferences/video' },
    setInterval() { return 1; }, clearInterval() {},
    setTimeout() { return 1; }, clearTimeout() {},
    addEventListener() {},
    angular: {
      element() {
        return { injector() {
          return { get(name) {
            assert.equal(name, '$rootScope');
            return { $new() {
              return { $applyAsync() {}, $destroy() {}, captureSettingsReady: false };
            }};
          }};
        }};
      }
    }
  };
  const ctx = {
    window: win,
    document: { documentElement: {} },
    navigator: {},
    fetch(url, options) { requests.push({ url, options }); return nextResponse(url, options); },
    Promise, Set, Error, Number, Object, Array, String, JSON
  };
  vm.runInNewContext(exposed, ctx, { filename: 'video-capture-options.js' });
  return {
    hooks: win.__captureUiTest,
    requests,
    respond(fn) { nextResponse = fn; }
  };
}
function goodSettings() {
  return {
    engineMode: 'FFmpeg', apiCapture: 'gdigrab', quality: 'Custom',
    encoder: 'NVENC_H264', encoderPreset: 4, captureCursor: false,
    fps: 60, bitrateKbps: 50000, useNativeResolution: true,
    width: 1920, height: 1080
  };
}

test('GET failure locks all capture options instead of silently persisting fallback defaults', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.hooks.fail(new Error('Backend offline'));
  assert.equal(view.scope.captureSettingsReady, false);
  assert.match(view.status.textContent, /Reload to retry/);
  assert.match(view.status.textContent, /Backend offline/);
  const unchanged = JSON.stringify(h.hooks.state());
  view.scope.captureUi.engineMode = 'Duluka';
  view.scope.onCaptureEngineChange();
  view.scope.onCaptureApiChange();
  view.scope.onEncoderChange();
  view.scope.onPresetChange();
  view.scope.onCursorChange();
  assert.equal(JSON.stringify(h.hooks.state()), unchanged, 'UI handlers must not mutate defaults');
  assert.equal(await h.hooks.save(), false);
  assert.equal(h.requests.length, 0, 'NO POST allowed after unconfirmed GET');
});

test('only complete supported capture settings become editable and can be saved', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  const settings = goodSettings();
  assert.equal(h.hooks.valid(settings), true);
  assert.equal(h.hooks.valid({ ...settings, encoder: 'unexpected' }), false);
  assert.equal(h.hooks.valid({ ...settings, engineMode: 'Duluka', apiCapture: 'gdigrab' }), false);
  assert.equal(h.hooks.valid({ ...settings, width: null }), false);
  h.hooks.setState(settings);
  view.scope.captureSettingsReady = true;
  h.respond(async () => ({ ok: true, status: 200, json: async () => goodSettings() }));
  await h.hooks.save();
  assert.equal(h.requests.length, 1);
  assert.equal(h.requests[0].url, '/DulukaCapture/v.1.0/settings');
  assert.equal(h.requests[0].options.method, 'POST');
  assert.equal(JSON.parse(h.requests[0].options.body).fps, 60);
  assert.equal(view.status.textContent, 'Saved');
});

test('a malformed save response is not presented as Saved', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.hooks.setState(goodSettings());
  view.scope.captureSettingsReady = true;
  h.respond(async () => ({ ok: true, status: 200, json: async () => ({}) }));
  await h.hooks.save();
  assert.match(view.status.textContent, /Invalid capture settings response/);
  assert.equal(view.status.classList.values.has('highlights-item-disabled'), true);
});

test('runtime status HTTP failure is never displayed as Ready or Recording', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.respond(async () => ({ status: 503, json: async () =>
    ({ enabled: true, ready: true, recording: true }) }));
  await h.hooks.refreshStatus();
  assert.match(view.runtime.textContent, /unavailable/);
  assert.equal(view.runtime.classList.values.has('nv-video-engine-error'), true);
});

test('runtime status 200 with verified fields continues rendering Ready/Recording', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.respond(async () => ({ status: 200, json: async () =>
    ({ enabled: true, ready: true, recording: true }) }));
  await h.hooks.refreshStatus();
  assert.match(view.runtime.textContent, /Recording/);
  assert.equal(view.runtime.classList.values.has('nv-video-engine-error'), false);
});
