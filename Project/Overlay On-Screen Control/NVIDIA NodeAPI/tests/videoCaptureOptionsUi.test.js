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
    parseCatalog: parseEncoderCatalog,
    allowed: allowedEncoders,
    setCatalog: function (catalog) { encoderCatalog = catalog; },
    syncCodecs: syncEncoderControls,
    loadSettingsAndCatalog: readCaptureSettingsAndCatalog,
    save: save,
    refreshStatus: refreshCaptureRuntimeStatus,
    refreshDisplays: refreshCaptureDisplays,
    displayKey: captureDisplayKey
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
function containerCatalog() {
  return {
    schemaVersion: 1,
    readOnly: true,
    policyValid: false,
    captureRuntimeReady: true,
    providers: [
      { id: 'Duluka', routeWired: true, runtimeReady: false,
        encoders: ['NVENC_H264'] },
      { id: 'FFmpeg', routeWired: true, runtimeReady: false,
        encoders: ['h264_nvenc', 'libx264'] },
      { id: 'OBS', routeWired: false, encoders: ['obs_managed'] }
    ]
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
  assert.equal(view.scope.captureEncoderChoices.length, 0,
    'Unfetched catalog must not label a guessed default as saved/usable');
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
  h.hooks.setCatalog(h.hooks.parseCatalog(containerCatalog()));
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
  h.hooks.setCatalog(h.hooks.parseCatalog(containerCatalog()));
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

test('NVIDIA Container catalog allows only wired routes, not nine speculative codecs', () => {
  const h = startHarness();
  const view = h.hooks.prime();
  const catalog = h.hooks.parseCatalog(containerCatalog());
  assert.deepEqual(Array.from(catalog.routes.Duluka), ['NVENC_H264']);
  assert.deepEqual(Array.from(catalog.routes.FFmpeg), ['NVENC_H264', 'LibX264']);
  h.hooks.setCatalog(catalog);
  h.hooks.setState({ ...goodSettings(), engineMode: 'Duluka',
    apiCapture: 'dxgi_desktop_duplication' });
  h.hooks.syncCodecs();
  assert.deepEqual(Array.from(view.scope.captureEncoderChoices, x => x.value),
    ['NVENC_H264']);
  assert.match(view.scope.captureCodecStatus, /routing policy unverified/);
  assert.match(view.scope.captureCodecStatus, /H.264 NVENC only/);
  assert.equal(view.scope.captureEncoderChoices.some(x => /Intel|AMD|AV1/.test(x.label)), false);
});

test('saved unsupported codec remains visible and cannot be silently POSTed', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.hooks.setCatalog(h.hooks.parseCatalog(containerCatalog()));
  h.hooks.setState({ ...goodSettings(), engineMode: 'Duluka',
    apiCapture: 'dxgi_desktop_duplication', encoder: 'NVENC_HEVC' });
  view.scope.captureSettingsReady = true;
  h.hooks.syncCodecs();
  assert.equal(h.hooks.state().encoder, 'NVENC_HEVC');
  assert.equal(view.scope.captureEncoderChoices.length, 2);
  assert.equal(view.scope.captureEncoderChoices[1].disabled, true);
  assert.match(view.scope.captureEncoderChoices[1].label, /saved; unsupported/);
  assert.equal(await h.hooks.save(), false);
  assert.equal(h.requests.length, 0);
  assert.match(view.status.textContent, /Choose a codec available/);
  view.scope.captureUi.encoder = 'NVENC_H264';
  view.scope.onEncoderChange();
  await Promise.resolve();
  assert.equal(h.hooks.state().encoder, 'NVENC_H264');
  assert.equal(h.requests.length, 1);
  assert.equal(JSON.parse(h.requests[0].options.body).encoder, 'NVENC_H264');
});

test('catalog unavailable blocks edits and does not silently enable hardcoded fallback', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.respond(url => Promise.resolve(url.endsWith('/engines') ?
    { ok: false, status: 503 } :
    { ok: true, status: 200, json: async () => goodSettings() }));
  await assert.rejects(h.hooks.loadSettingsAndCatalog(), /codec routes/);
  assert.equal(view.scope.captureSettingsReady, false);
  assert.equal(view.scope.captureEncoderChoices.length, 0);
  assert.equal(await h.hooks.save(), false);
  assert.deepEqual(h.requests.map(x => x.url).sort(),
    ['/DulukaCapture/v.1.0/engines', '/DulukaCapture/v.1.0/settings'].sort());
  assert.equal(h.requests.some(x => x.options && x.options.method === 'POST'), false);
});

test('successful settings plus Container catalog allows only catalog-selected choices', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.respond(url => Promise.resolve({
    ok: true, status: 200,
    json: async () => url.endsWith('/engines') ? containerCatalog() : goodSettings()
  }));
  await h.hooks.loadSettingsAndCatalog();
  assert.equal(view.scope.captureSettingsReady, true);
  assert.deepEqual(Array.from(view.scope.captureEncoderChoices, x => x.value),
    ['NVENC_H264', 'LibX264']);
  assert.equal(h.hooks.state().encoder, 'NVENC_H264');
  assert.equal(h.requests.every(x => !x.options || x.options.method !== 'POST'), true);
});

test('invalid Container schema/unsupported route fails closed rather than claiming encoder availability', () => {
  const h = startHarness();
  assert.throws(() => h.hooks.parseCatalog({ providers: [] }), /Invalid/);
  assert.throws(() => h.hooks.parseCatalog({
    ...containerCatalog(), providers: [{ id: 'Duluka', routeWired: true,
      encoders: ['codec_that_is_not_registered'] }, ...containerCatalog().providers.slice(1)]
  }), /No known codec route for Duluka/);
  assert.throws(() => h.hooks.parseCatalog({
    ...containerCatalog(), providers: [{ id: 'Duluka', routeWired: false,
      encoders: ['NVENC_H264'] }, ...containerCatalog().providers.slice(1)]
  }), /Capture route unavailable/);
});

function displayCatalog() {
  return { schemaVersion: 1, selectionValid: true, recording: false,
    displays: [
      { OutputIndex: 0, AdapterLuidLow: 111, AdapterLuidHigh: 8,
        Width: 1680, Height: 1050, Label: 'Display 1 — 1680x1050 (Primary)' },
      { OutputIndex: 2, AdapterLuidLow: 111, AdapterLuidHigh: 8,
        Width: 1080, Height: 1920, Label: 'Display 2 — 1080x1920' }
    ],
    selected: { OutputIndex: 0, AdapterLuidLow: 111, AdapterLuidHigh: 8 } };
}

test('capture display list comes from Capture-selected LUID and output, not guessed monitor indexes', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.respond(async () => ({ ok: true, status: 200, json: async () => displayCatalog() }));
  assert.equal(await h.hooks.refreshDisplays(), true);
  assert.equal(view.scope.captureDisplayReady, true);
  assert.equal(view.scope.captureDisplayChoices.length, 2);
  assert.equal(view.scope.captureUi.displayKey, '8:111:0');
  assert.match(view.scope.captureDisplayStatus, /Record \/ Replay \/ Screenshot/);
  assert.equal(h.requests.some(x => x.options && x.options.method === 'POST'), false);
});

test('cannot pick a monitor when Container cannot supply a verified display catalog', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.respond(async () => ({ ok: false, status: 503 }));
  assert.equal(await h.hooks.refreshDisplays(), false);
  assert.equal(view.scope.captureDisplayReady, false);
  assert.equal(view.scope.captureDisplayChoices.length, 0);
  await view.scope.onCaptureDisplayChange();
  assert.equal(h.requests.length, 1, 'no POST after 503');
});

test('changing capture display posts exact selected LUID plus output index', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.respond(async (url, opts) => {
    if (opts && opts.method === 'POST') {
      const body = JSON.parse(opts.body);
      assert.deepEqual(body, {
        confirmed: true, outputIndex: 2, adapterLuidLow: 111, adapterLuidHigh: 8
      });
      return { ok: true, status: 200, json: async () =>
        ({ saved: true, effectiveNow: false }) };
    }
    return { ok: true, status: 200, json: async () => ({
      ...displayCatalog(), selected: displayCatalog().displays[1]
    }) };
  });
  assert.equal(await h.hooks.refreshDisplays(), true);
  view.scope.captureUi.displayKey = '8:111:2';
  await view.scope.onCaptureDisplayChange();
  assert.equal(view.scope.captureUi.displayKey, '8:111:2');
  assert.match(view.scope.captureDisplayStatus, /restart\/rebuild/i);
  assert.equal(h.requests.filter(x => x.options && x.options.method === 'POST').length, 1);
});

test('FFmpeg receives NVIDIA H264/HEVC and Intel QSV only when Capture reports codec IDs', () => {
  const h = startHarness();
  const view = h.hooks.prime();
  const catalog = containerCatalog();
  catalog.workerTruthAvailable = true;
  catalog.providers[1].capabilityVerified = true; // valid FFmpeg binary probe
  catalog.providers[1].hardwareVerified = false; // not a GPU encode test
  catalog.providers[1].encoders = [
    'h264_nvenc', 'hevc_nvenc', 'h264_qsv', 'hevc_qsv', 'libx264'
  ];
  const parsed = h.hooks.parseCatalog(catalog);
  assert.deepEqual(Array.from(parsed.routes.FFmpeg),
    ['NVENC_H264', 'NVENC_HEVC', 'QuickSync_H264', 'QuickSync_HEVC', 'LibX264']);
  assert.equal(parsed.binaryVerified, true);
  assert.equal(parsed.hardwareVerified, false);
  h.hooks.setCatalog(parsed);
  h.hooks.setState(goodSettings());
  h.hooks.syncCodecs();
  assert.deepEqual(Array.from(view.scope.captureEncoderChoices, x => x.value),
    ['NVENC_H264', 'NVENC_HEVC', 'QuickSync_H264', 'QuickSync_HEVC', 'LibX264']);
  assert.match(view.scope.captureCodecStatus, /Intel QSV requires an available Intel GPU/);
  assert.equal(h.requests.length, 0);
});

test('the Codec helper renders AFTER both selects, never under the select underline', () => {
  const css = fs.readFileSync(path.join(__dirname, '../osc/mods/video-capture-options.css'), 'utf8');
  assert.match(source, /encoderFields\.appendChild\(preset\.wrap\);\s*advanced\.appendChild\(previewPanel\);\s*advanced\.appendChild\(encoderFields\);\s*advancedSection\.appendChild\(advanced\);\s*advancedSection\.appendChild\(codecStatus\);/);
  const noteRule = css.match(/#nv-video-capture-options \.nv-video-engine-codec-note\s*\{([^}]*)\}/);
  assert.ok(noteRule, 'specific CSS rule for helper note');
  assert.match(noteRule[1], /position:\s*static/);
  assert.match(noteRule[1], /margin:\s*14px 0 8px/);
  assert.doesNotMatch(noteRule[1], /margin:\s*-/);
});
