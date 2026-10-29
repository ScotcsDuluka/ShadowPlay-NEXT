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
  path.join(__dirname, '../osc/Module/Settings/Capture/video-capture-options.js'), 'utf8');
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
    displayKey: captureDisplayKey,
    installNativeQualityPresets: installNativeQualityPresets
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
    win,
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

test('codec and preset controls live in the native Advanced Capture route', () => {
  const route = fs.readFileSync(
    path.join(__dirname, '../osc/Module/Settings/Capture/advanced-capture-route.js'), 'utf8');
  assert.match(source, /openAdvancedCapture = function/);
  assert.match(source, /main\.osc-capture-advanced/);
  assert.doesNotMatch(source, /panel\.appendChild\(advancedSection\)/);
  assert.match(route, /captureAdvanced\.encoder/);
  assert.match(route, /captureAdvanced\.preset/);
  assert.match(route, /controllerAs:\s*'captureAdvanced'/);
});

test('Video Settings uses compact static sections, not a nested scrollbar', () => {
  const css = fs.readFileSync(
    path.join(__dirname, '../osc/Module/Settings/Capture/video-capture-options.css'), 'utf8');
  function block(selector) {
    const start = css.indexOf(selector + ' {');
    assert.ok(start >= 0, 'missing ' + selector);
    return css.slice(start, css.indexOf('}', start));
  }
  const section = block('#nv-video-capture-options .nv-video-engine-section');
  const heading = block('#nv-video-capture-options .nv-video-engine-section-heading');
  const footer = block('#nv-video-capture-options .nv-video-engine-footer');
  const advancedFields = block('#nv-video-capture-options .nv-video-engine-advanced-fields');
  assert.match(section, /margin:\s*0 0 6px/);
  assert.match(heading, /height:\s*34px/);
  assert.match(footer, /margin:\s*5px 0 0/);
  assert.match(advancedFields, /display:\s*grid/);
  assert.match(advancedFields, /grid-template-columns:\s*minmax\(0, 1\.6fr\) minmax\(0, 0\.7fr\)/);
  assert.match(advancedFields, /gap:\s*8px/);
  assert.match(css, /\.nv-video-engine-section > \.nv-video-engine-field\s*\{[^}]*gap:\s*3px/);
  assert.doesNotMatch(css, /overflow-y:\s*(auto|scroll)/);
  assert.doesNotMatch(css, /max-height:\s*[0-9].*overflow/);
});

function nvencSyntheticCatalog(status) {
  const body = containerCatalog();
  body.policyValid = true;
  body.providers[1].encoders = [
    'h264_nvenc', 'hevc_nvenc', 'av1_nvenc', 'libx264'
  ];
  body.workerRuntime = { schemaVersion: 2, ffmpegBinary: {
    binaryRunnable: true,
    nvencSyntheticValidation: {
      status: status || 'complete', gpuCount: 2,
      results: [0, 1].flatMap(gpuIndex => [
        { gpuIndex, encoder: 'h264_nvenc', syntheticEncodePassed: true,
          realRecordingVerified: false },
        { gpuIndex, encoder: 'hevc_nvenc', syntheticEncodePassed: true,
          realRecordingVerified: false },
        { gpuIndex, encoder: 'av1_nvenc', syntheticEncodePassed: false,
          realRecordingVerified: false }
      ])
    }
  }};
  return body;
}

test('live Capture synthetic proof disables AV1 on both GPUs but keeps NVIDIA md-select choices', async () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.hooks.setCatalog(h.hooks.parseCatalog(nvencSyntheticCatalog()));
  h.hooks.setState({ ...goodSettings(), encoder: 'NVENC_AV1' });
  view.scope.captureSettingsReady = true;
  h.hooks.syncCodecs();
  assert.deepEqual(Array.from(h.hooks.allowed('FFmpeg')),
    ['NVENC_H264', 'NVENC_HEVC', 'LibX264']);
  const av1 = view.scope.captureEncoderChoices.find(x => x.value === 'NVENC_AV1');
  assert.equal(av1.disabled, true);
  assert.match(av1.label, /GPU test failed/);
  assert.equal(h.hooks.state().encoder, 'NVENC_AV1',
    'saved unsupported codec must remain unchanged and visible');
  assert.equal(view.scope.captureUi.encoder, 'NVENC_AV1');
  assert.match(view.scope.captureCodecStatus, /FAILED on all 2 tested GPUs/);
  assert.equal(await h.hooks.save(), false);
  assert.equal(h.requests.length, 0, 'read-only refresh and invalid save never POST');
  view.scope.captureUi.encoder = 'NVENC_H264';
  view.scope.onEncoderChange();
  await Promise.resolve();
  assert.equal(h.hooks.state().encoder, 'NVENC_H264');
  assert.equal(h.requests.length, 1);
});

test('synthetic pass means encode tested, not recording-ready or selected GPU ready', () => {
  const h = startHarness();
  const view = h.hooks.prime();
  h.hooks.setCatalog(h.hooks.parseCatalog(nvencSyntheticCatalog()));
  h.hooks.setState(goodSettings());
  h.hooks.syncCodecs();
  assert.match(view.scope.captureCodecStatus, /passed on 2\/2 GPUs/);
  assert.match(view.scope.captureCodecStatus, /real recording are unverified/);
  assert.equal(view.scope.captureEncoderChoices.find(x => x.value === 'NVENC_HEVC').disabled, false);
});

test('not_run and missing NVENC verification do not claim unsupported hardware', () => {
  for (const body of [nvencSyntheticCatalog('not_run'),
    { ...nvencSyntheticCatalog(), workerRuntime: undefined }]) {
    const h = startHarness();
    const view = h.hooks.prime();
    h.hooks.setCatalog(h.hooks.parseCatalog(body));
    h.hooks.setState({ ...goodSettings(), encoder: 'NVENC_AV1' });
    h.hooks.syncCodecs();
    assert.equal(view.scope.captureEncoderChoices.find(x => x.value === 'NVENC_AV1').disabled, false);
    assert.match(view.scope.captureCodecStatus, /GPU support unverified/);
    assert.ok(h.hooks.allowed('FFmpeg').includes('NVENC_AV1'));
  }
});

test('incomplete or duplicate GPU evidence cannot falsely disable a codec', () => {
  const cases = [
    body => { body.workerRuntime.ffmpegBinary.nvencSyntheticValidation.results.pop(); },
    body => { body.workerRuntime.ffmpegBinary.nvencSyntheticValidation.results =
      body.workerRuntime.ffmpegBinary.nvencSyntheticValidation.results
        .filter(x => x.encoder !== 'av1_nvenc').concat([
          { gpuIndex: 0, encoder: 'av1_nvenc', syntheticEncodePassed: false },
          { gpuIndex: 0, encoder: 'av1_nvenc', syntheticEncodePassed: false }
        ]); },
    body => { body.workerRuntime.ffmpegBinary.nvencSyntheticValidation.results =
      body.workerRuntime.ffmpegBinary.nvencSyntheticValidation.results.map(x =>
        x.encoder === 'av1_nvenc' ? { ...x, syntheticEncodePassed: 'false' } : x); }
  ];
  for (const alter of cases) {
    const body = nvencSyntheticCatalog();
    alter(body);
    const h = startHarness();
    h.hooks.setCatalog(h.hooks.parseCatalog(body));
    assert.ok(h.hooks.allowed('FFmpeg').includes('NVENC_AV1'));
  }
});

test('FFmpeg verification must never disable Duluka Native H264 route', () => {
  const h = startHarness();
  const body = nvencSyntheticCatalog();
  body.workerRuntime.ffmpegBinary.nvencSyntheticValidation.results =
    body.workerRuntime.ffmpegBinary.nvencSyntheticValidation.results.map(x =>
      x.encoder === 'h264_nvenc' ? { ...x, syntheticEncodePassed: false } : x);
  h.hooks.setCatalog(h.hooks.parseCatalog(body));
  assert.deepEqual(Array.from(h.hooks.allowed('Duluka')), ['NVENC_H264']);
  assert.equal(h.hooks.allowed('FFmpeg').includes('NVENC_H264'), false);
});

test('Recommended and Maximum extend the SAME NVIDIA four-preset row without new backend quality IDs', () => {
  const h = startHarness();
  h.hooks.prime();
  const base = Object.fromEntries(['Low', 'Medium', 'High', 'Custom'].map(id => [id, {id, supported:true}]));
  const calls = [];
  const native = {
    feature:'Manual', Qualities:base, settings:{quality:base.Low},
    selectQuality(item){calls.push(item.id); this.settings.quality=item;},
    isActiveQuality(item){return this.settings.quality === item;}
  };
  h.win.angular.element = () => ({
    isolateScope(){return {customizeMenu:native};},
    controller(){return native;}
  });
  const host={querySelector(){return {};}};
  assert.equal(h.hooks.installNativeQualityPresets(host),true);
  assert.deepEqual(Object.keys(native.Qualities),
    ['Low','Medium','High','Custom','NvRecommended','NvMaximum']);
  assert.equal(native.Qualities.NvRecommended.icon,'icon-highlights');
  assert.equal(native.Qualities.NvMaximum.icon,'icon-high');
  native.selectQuality(native.Qualities.NvRecommended);
  assert.deepEqual(calls,['Medium']);
  assert.equal(native.isActiveQuality(native.Qualities.NvRecommended),true);
  assert.equal(native.isActiveQuality(native.Qualities.Medium),false);
  native.selectQuality(native.Qualities.NvMaximum);
  assert.deepEqual(calls,['Medium','High']);
  assert.equal(native.isActiveQuality(native.Qualities.NvMaximum),true);
  assert.equal(native.isActiveQuality(native.Qualities.High),false);
  native.selectQuality(native.Qualities.Custom);
  assert.equal(native.isActiveQuality(native.Qualities.Custom),true);
  assert.equal(native.isActiveQuality(native.Qualities.NvMaximum),false);
  assert.equal(h.hooks.installNativeQualityPresets(host),true);
  assert.equal(Object.keys(native.Qualities).length,6);
  assert.deepEqual(calls,['Medium','High','Custom']);
});

test('late NVIDIA isolate-scope digest paints the two extra presets without waiting for Capture injector', () => {
  const h = startHarness();
  // Deliberately DO NOT prime the Capture panel: injector is still null.
  const qualities = Object.fromEntries(['Low','Medium','High','Custom']
    .map(id => [id,{id,supported:true}]));
  const native = {
    feature:'Manual', Qualities:qualities, settings:{quality:qualities.Low},
    selectQuality(choice){this.settings.quality=choice;},
    isActiveQuality(choice){return this.settings.quality===choice;}
  };
  let paints = 0;
  let visible = 4;
  const list = { querySelectorAll() { return Array(visible).fill({}); } };
  const nativeElement = {
    querySelector(selector){return selector==='.quality-list-box' ? list : null;}
  };
  const scope = {
    customizeMenu:native,
    $evalAsync(){paints += 1; visible=6;}
  };
  h.win.angular.element = () => ({
    isolateScope(){return scope;},
    controller(){return native;}
  });
  const host = {querySelector(){return nativeElement;}};
  assert.equal(h.hooks.installNativeQualityPresets(host),true);
  assert.equal(paints,1,'native ng-repeat must be digested on the first install');
  assert.equal(visible,6,'DOM must reflect six native buttons');
  assert.equal(h.hooks.installNativeQualityPresets(host),true);
  assert.equal(paints,1,'a completed native row needs no redundant digest');
  assert.equal(Object.keys(qualities).length,6);
});
