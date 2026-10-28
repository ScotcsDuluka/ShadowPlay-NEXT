'use strict';
// Executes actual bundled NVIDIA constructors/functions with synthetic APIs.
// Does not load mods or start a product process, listener, socket or native host.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const underscore = require('underscore');
const bundle = fs.readFileSync(path.join(__dirname, '../osc/app.js'), 'utf8');
let modules;
vm.runInNewContext(bundle, {webpackJsonp: (_ids, values) => { modules = values; }});
const noop = () => {};
const q = {when: value => Promise.resolve(value), all: values => Promise.all(values),
  reject: error => Promise.reject(error)};
const flush = () => new Promise(resolve => setImmediate(resolve));
const constant = () => new Proxy({}, {get: (_object, name) => String(name)});

function moduleText(marker) {
  const factory = modules.find(value => value && value.toString().includes(marker));
  assert.ok(factory, marker);
  return factory;
}
function serviceDefinition(marker, id, exportName) {
  let definition;
  moduleText(marker)({exports: {}}, {}, n => n === id ? {
    [exportName]: {service(_name, def) { definition = def; }}
  } : n === 3 ? underscore : n === 110 ? value => typeof value : n === 8 ? JSON.stringify : {});
  assert.ok(definition, marker);
  return definition;
}
const appDef = serviceDefinition('t.appService=', 1, 'ngMainModule');
const displayDef = serviceDefinition('t.oscDisplayService=', 2, 'ngMainCommonModule');
const hotkeyDef = serviceDefinition('t.hotkeyService=', 2, 'ngMainCommonModule');
const broadcastDef = serviceDefinition('t.broadcastService=', 2, 'ngMainCommonModule');
const shadowText = moduleText('t.shadowPlayService=').toString();
const shadowInit = shadowText.slice(shadowText.indexOf('we.init=') + 'we.init='.length,
  shadowText.lastIndexOf('}]);t.shadowPlayService='));
const shadowSubscriptions = shadowText.slice(shadowText.indexOf('function ve(){'),
  shadowText.indexOf('var ye,we=this'));
const mainText = moduleText('t.MainMenuController=').toString();
const mainBroadcast = mainText.slice(mainText.indexOf('J.setBroadcastMenuState=') +
  'J.setBroadcastMenuState='.length, mainText.indexOf(',J.setHotkeyShortcuts='));
let preferencesDef;
moduleText('t.PreferencesMenuController=')({exports: {}}, {}, n => n === 1 ? {
  ngMainModule: {controller(_name, definition) { preferencesDef = definition; }}
} : n === 3 ? underscore : {});
assert.ok(preferencesDef);
const multiTrackMethod = shadowText.slice(shadowText.indexOf('we.isMultiTrackAudioAvailable=') +
  'we.isMultiTrackAudioAvailable='.length, shadowText.indexOf(',we.getAudioState='));
const desktopSettingMethod = shadowText.slice(shadowText.indexOf('we.isDesktopCaptureSettingShown=') +
  'we.isDesktopCaptureSettingShown='.length, shadowText.indexOf(',we.isBroadcastRecordSupported='));

function makeService(definition, dependencies) {
  const service = {};
  definition.at(-1).apply(service, definition.slice(0, -1).map(name => dependencies[name] || constant()));
  return service;
}
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return {promise, resolve, reject};
}
function fixture(options = {}) {
  const calls = Object.create(null), registrations = [], subscriptions = [], messages = [];
  const listeners = Object.create(null), rootEvents = Object.create(null);
  const count = name => { calls[name] = (calls[name] || 0) + 1; };
  const logger = {info: noop, error: (...parts) => messages.push(parts.join(' '))};
  const events = {
    on(name, fn) { subscriptions.push({name, fn}); (listeners[name] || (listeners[name] = [])).push(fn); },
    trigger(name, value) { for (const fn of listeners[name] || []) fn(value); }
  };
  const rootScope = {$on(name, fn) { (rootEvents[name] || (rootEvents[name] = [])).push(fn); }};
  const routeRequests = [];
  function completeRoute(name) {
    for (const fn of rootEvents.$stateChangeStart || []) fn({}, {name}, {}, state.current);
    state.current = {name};
    for (const fn of rootEvents.$viewContentLoaded || []) fn();
  }
  const state = {current: {name: options.initialRoute || 'base'}, go(name) {
    if (state.current.name === name) return q.when(state.current);
    if (options.routeFailure && name !== 'base') {
      const rejected = q.reject(options.routeFailure);
      rejected.catch(noop);
      return rejected;
    }
    if (options.deferRoutes) {
      const task = deferred();
      routeRequests.push({name, task});
      return task.promise;
    }
    completeRoute(name);
    return q.when(true);
  }};
  const socket = {connect() { count('socket.connect'); }, register(route, event) { registrations.push({route, event}); }};
  const telemetry = new Proxy({init() { count('telemetryService.init'); return q.when(true); }},
    {get: (target, name) => target[name] || noop});
  const dependencies = {$q: q, $log: {getInstance: () => logger}, $state: state,
    $rootScope: rootScope, $window: {}, $document: [{addEventListener: noop, removeEventListener: noop}],
    eventAggregator: events, socketService: socket, telemetryService: telemetry,
    OSC_CONFIG: {}, HOTKEY_EVENTS: constant(), OSC_KEYBOARD: constant(),
    BROADCAST_STATES: constant(),
    cefService: {query: () => q.when(JSON.stringify({fullscreen: false})), allowOSCPainting: noop,
      openOSC: () => q.when('window'), closeOSC: noop},
    highlightsEndpoints: {notifyOverlayState: () => q.when(true)}};
  const captureFailure = {status: 503, data: {error: 'synthetic_unavailable', privateDetail: 'NEVER LOG THIS'}};
  let captureAvailable = !!options.captureAvailable;
  const shadow = {mainMenuData: {},
    isSPRunning: () => q.when(true), getMainViewDataInit: () => q.when({}),
    getAudioSettingsInit: () => q.when(true), dynamicHotkeyToggle: noop,
    setOscReady() { count('setOscReady'); return q.when(true); },
    uploadInstantReplay: noop, saveInstantReplay: noop, toggleWebcam: noop,
    checkAndCaptureScreenshot() { count('screenshotActionDispatch'); }};
  const endpoints = {
    getCaptureState() {
      count('getCaptureState');
      // Fulfilled fixture preserves this repository's raw response shape.
      // It is a transport control, not proof of capture readiness compatibility.
      return captureAvailable ? q.when({data: {captureMode: 'None', recordingState: 'NotRecording'}}) : q.reject(captureFailure);
    },
    getDesktopCaptureSupportReason: () => q.when({data: {support: true}}),
    getBroadcast2KSupported: () => q.when({data: {support: true}}),
    get8K60Support: () => q.when({data: {support: false}}),
    getCaptureProcessInfo: () => () => options.focus || q.when({data: {}})
  };
  const subscriptionContext = {Se: logger, d: socket, p: events, I: constant(), A: constant(),
    M: constant(), _: {}, we: shadow};
  for (const name of ['de', 'ce', 'fe', 'me', 'ge', 'pe', 'le', 'j', 'J', '$', 'ee', 'te', 'ne', 'se'])
    subscriptionContext[name] = noop;
  const ve = vm.runInNewContext('(' + shadowSubscriptions + ')', subscriptionContext);
  const actualShadowInit = vm.runInNewContext('(' + shadowInit + ')', {
    we: shadow, r: endpoints, Se: logger, ve, H: noop, xe: () => q.when(true)
  });
  shadow.init = function () { count('shadowPlayService.init'); return actualShadowInit(); };
  dependencies.shadowPlayService = shadow;
  dependencies.shadowPlayEndpoints = endpoints;
  const display = makeService(displayDef, dependencies);
  dependencies.oscDisplayService = display;
  const hotkeys = makeService(hotkeyDef, dependencies);
  dependencies.hotkeyService = hotkeys;
  const connect = {serviceTypes: {STREAMING: 1}, services: {}, getServices() { return this.services; }};
  dependencies.connectService = connect;
  const broadcast = makeService(broadcastDef, dependencies);
  dependencies.broadcastService = broadcast;
  for (const [name, service] of [['oscDisplayService', display], ['hotkeyService', hotkeys], ['broadcastService', broadcast]]) {
    const original = service.init;
    service.init = function () { count(name + '.init'); return original.apply(service, arguments); };
  }
  for (const name of appDef.slice(0, -1)) if (!dependencies[name]) dependencies[name] = {
    init() {
      count(name + '.init');
      if (name === 'ugcService') connect.services = {Twitch: {id: 1, portalIdentifier: 'Twitch', name: 'Twitch'}};
      return options[name] || q.when(true);
    }
  };
  dependencies.piplConfigService = {initialize() { count('pipl.initialize'); }, isConnectEnabled: () => q.when(false)};
  const app = makeService(appDef, dependencies);
  return {app, shadow, broadcast, display, hotkeys, state, rootEvents, registrations, dependencies, routeRequests,
    subscriptions, messages, calls, captureFailure,
    completeRoute(index) {
      const request = routeRequests[index];
      completeRoute(request.name); request.task.resolve(true);
    },
    receiveHotkey(value) { events.trigger('hotkeyEvent', value); },
    setCaptureAvailable(value) { captureAvailable = value; }};
}

test('truthful Capture503 starts original display/hotkey shell without mods, account init or ready POST', async () => {
  const page = fixture();
  const degraded = await page.app.initializeUI();
  assert.equal(degraded.captureUnavailable, true);
  assert.equal(degraded.status, 503);
  assert.equal(page.app.uiInitialization.phase, 'degraded');
  assert.equal(page.app.uiInitialization.shellReady, true);
  assert.equal(page.app.uiInitialization.failedService, 'shadowPlayService');
  assert.equal(page.app.uiInitialization.status, 503);
  assert.equal(page.calls.setOscReady || 0, 0);
  assert.equal(page.calls['ugcService.init'] || 0, 0);
  assert.equal(page.calls['broadcastService.init'] || 0, 0);
  assert.equal(page.calls['oscDisplayService.init'], 1);
  assert.equal(page.calls['hotkeyService.init'], 1);
  assert.equal(page.rootEvents.$viewContentLoaded.length, 1);
  assert.equal(page.broadcast.getBroadcastPortalFromName('AlwaysAsk').id, -1);
  assert.equal(page.broadcast.broadcastPortalPreferences.length, 1);
  const menu = {isConnectEnabled: true, tiles: {Broadcast: {}}};
  const update = vm.runInNewContext('(' + mainBroadcast + ')', {
    J: menu, f: page.broadcast, l: q, be: () => q.when('AlwaysAsk')
  });
  await update();
  assert.equal(menu.tiles.Broadcast.visible, false);
  assert.ok(page.messages.some(message => message.includes('service=shadowPlayService status=503')));
  assert.ok(!page.messages.join(' ').includes('NEVER LOG THIS'));
});

test('Capture503 shell closes and reopens using actual original display callbacks', async () => {
  const page = fixture();
  await page.app.initializeUI();
  for (let cycle = 0; cycle < 3; cycle++) {
    page.display.openOSC(); await flush();
    assert.equal(page.state.current.name, 'main.main-menu');
    page.display.closeOSC(); await flush();
    assert.equal(page.state.current.name, 'base');
  }
});

test('fallback waits for existing keyboard/settings promises and does not run sibling init again', async () => {
  const keyboard = deferred(), settings = deferred();
  const page = fixture({keyboardService: keyboard.promise, settingsService: settings.promise});
  const attempt = page.app.initializeUI();
  assert.strictEqual(page.app.initializeUI(), attempt);
  await flush();
  assert.equal(page.calls['oscDisplayService.init'] || 0, 0);
  keyboard.resolve(true); await flush();
  assert.equal(page.calls['hotkeyService.init'] || 0, 0);
  settings.resolve(true);
  const degraded = await attempt;
  assert.equal(degraded.captureUnavailable, true);
  assert.equal(page.calls['keyboardService.init'], 1);
  assert.equal(page.calls['settingsService.init'], 1);
  assert.equal(page.calls['oscDisplayService.init'], 1);
});

test('concurrent sibling failure prevents fallback and remains memoized on retry', async () => {
  const settings = deferred();
  const page = fixture({settingsService: settings.promise});
  const attempt = page.app.initializeUI(); await flush();
  settings.reject({status: 500});
  await assert.rejects(attempt, error => error === page.captureFailure);
  assert.equal(page.calls['oscDisplayService.init'] || 0, 0);
  assert.equal(page.calls['hotkeyService.init'] || 0, 0);
  assert.equal(page.app.uiInitialization.shellReady, false);
  await assert.rejects(page.app.initializeUI());
  assert.equal(page.calls['settingsService.init'], 1);
  assert.equal(page.calls['oscDisplayService.init'] || 0, 0);
  assert.equal(page.calls.setOscReady || 0, 0);
});

test('same-instance fulfilled recovery preserves one socket and one original listener registration', async () => {
  const page = fixture();
  await page.app.initializeUI();
  const beforeSubscriptions = page.subscriptions.filter(item => item.name === 'MANUAL').length;
  assert.equal(beforeSubscriptions, 1);
  page.setCaptureAvailable(true);
  const recovered = page.app.initializeUI();
  assert.strictEqual(page.app.initializeUI(), recovered);
  await recovered;
  assert.equal(page.app.uiInitialization.phase, 'ready');
  assert.equal(page.app.uiInitialization.failedService, null);
  assert.equal(page.app.uiInitialization.status, null);
  assert.equal(page.calls['socket.connect'], 1);
  assert.equal(page.calls['pipl.initialize'], 1);
  assert.equal(page.calls['shadowPlayService.init'], 2);
  assert.equal(page.calls['keyboardService.init'], 1);
  assert.equal(page.calls['settingsService.init'], 1);
  assert.equal(page.calls['oscDisplayService.init'], 1);
  assert.equal(page.calls['hotkeyService.init'], 1);
  assert.equal(page.rootEvents.$viewContentLoaded.length, 1);
  assert.equal(page.subscriptions.filter(item => item.name === 'MANUAL').length, beforeSubscriptions);
  assert.equal(page.registrations.filter(item => item.route === '/ShadowPlay/v.1.0/Hotkey').length, 1);
  assert.equal(page.broadcast.getBroadcastPortalFromName('Twitch').id, 1);
  assert.equal(page.calls.setOscReady, 1);
  assert.strictEqual(page.app.initializeUI(), recovered);
  assert.equal(page.app.uiInitialization.phase, 'ready');
  // Preserve the current backend response contract instead of inventing state.
  assert.equal(page.shadow.captureState, undefined);
});

test('fulfilled synthetic transport control runs each original initializer once', async () => {
  const page = fixture({captureAvailable: true});
  const first = page.app.initializeUI();
  assert.strictEqual(page.app.initializeUI(), first);
  await first;
  assert.strictEqual(page.app.initializeUI(), first);
  for (const [name, calls] of Object.entries(page.calls)) assert.equal(calls, 1, name);
  assert.equal(page.app.uiInitialization.phase, 'ready');
  assert.equal(page.broadcast.getBroadcastPortalFromName('AlwaysAsk').id, 0);
  assert.equal(page.broadcast.getBroadcastPortalFromName('Twitch').id, 1);
});

test('original generic hotkey mapping dispatches once under503 and after fulfilled retry', async () => {
  const page = fixture();
  await page.app.initializeUI();
  page.receiveHotkey({hotkeyId: 'Screenshot'});
  assert.equal(page.calls.screenshotActionDispatch, 1);
  page.setCaptureAvailable(true);
  await page.app.initializeUI();
  page.receiveHotkey({hotkeyId: 'Screenshot'});
  assert.equal(page.calls.screenshotActionDispatch, 2,
    'one mapped event must not invoke duplicate original action subscriptions');
  // This verifies event routing only. Screenshot action/API success is not mocked
  // as available and is not claimed by this isolated dispatch test.
});

function settingsController(page, options = {}) {
  const dependencies = Object.assign({}, page.dependencies);
  const requests = [];
  const logger = {info: noop, error: noop};
  const shadow = page.shadow;
  const endpoints = {
    getAudioSettings() { requests.push('AudioSettings'); return options.audio || q.when({data: {}}); },
    getDesktopCaptureSupportReason() {
      requests.push('DesktopCapture/Support/Reason');
      return options.desktop || q.when({data: {support: true}});
    }
  };
  shadow.isMTAAvailCached = false;
  shadow.isMTAAvail = false;
  shadow.isMultiTrackAudioAvailable = vm.runInNewContext('(' + multiTrackMethod + ')', {
    we: shadow, e: q, r: endpoints, Se: logger
  });
  shadow.isDesktopCaptureSettingShown = vm.runInNewContext('(' + desktopSettingMethod + ')', {
    r: endpoints, Se: logger
  });
  dependencies.$scope = {$on: noop};
  dependencies.$stateParams = {};
  dependencies.$log = {getInstance: () => logger};
  dependencies.oscService = {onlineState: {online: true}};
  dependencies.coplayService = {shouldShowCoplaySetting: () => false};
  dependencies.nvCameraService = {isModsOn: () => false, hasVariableAvailability: () => false};
  dependencies.octoolService = {getIsFeatureAvailable: () => false};
  dependencies.piplConfigService = {isConnectEnabled: () => q.when(true)};
  // Only route construction is exercised here; no editor is mounted or service
  // action invoked. The original controller requires off only during destruction.
  dependencies.eventAggregator = {on: noop, off: noop};
  const controller = makeService(preferencesDef, dependencies);
  controller.initPreferencesMenu();
  return {controller, requests};
}

test('actual original Settings index builds with skipped503 feature services and API reads fulfilled', async () => {
  const page = fixture();
  await page.app.initializeUI();
  const settings = settingsController(page);
  await flush();
  assert.equal(settings.controller.tiles.length, 14);
  assert.ok(settings.controller.tiles.find(tile => tile.name === 'keyboard-shortcuts-tile').visible);
  assert.deepEqual(settings.requests, ['AudioSettings', 'DesktopCapture/Support/Reason']);
  assert.equal(page.calls['ugcService.init'] || 0, 0);
  assert.equal(page.calls['broadcastService.init'] || 0, 0);
  assert.equal(page.calls['coplayService.init'] || 0, 0);
  assert.equal(page.calls['octoolService.init'] || 0, 0);
  settings.controller.back();
  assert.equal(page.state.current.name, 'main.main-menu');
});

test('actual original Settings API rejection hides unsupported optional controls and still builds index', async () => {
  const page = fixture();
  await page.app.initializeUI();
  // Defer rejection until the original controller attaches its own handlers.
  const audio = deferred(), desktop = deferred();
  const settings = settingsController(page, {audio: audio.promise, desktop: desktop.promise});
  audio.reject({status: 503});
  await flush();
  desktop.reject({status: 503});
  await flush();
  assert.equal(settings.controller.tiles.length, 14);
  assert.equal(settings.controller.tiles.find(tile => tile.name === 'audio-tile').visible, false);
  assert.equal(settings.controller.tiles.find(tile => tile.name === 'privacy-control-tile').visible, false);
});

test('actual original Settings waits for AudioSettings but cached second entry avoids the request', async () => {
  const page = fixture();
  await page.app.initializeUI();
  const audio = deferred();
  const settings = settingsController(page, {audio: audio.promise});
  await flush();
  assert.equal(settings.controller.tiles.length, 0, 'An unresolved AudioSettings can leave the original index empty');
  audio.resolve({data: {}});
  await flush();
  assert.equal(settings.controller.tiles.length, 14);
  settings.controller.initPreferencesMenu();
  await flush();
  assert.equal(settings.requests.filter(request => request === 'AudioSettings').length, 1);
  assert.equal(settings.controller.tiles.length, 14);
});

test('initialized display clears same-state open lock without relying on view events', async () => {
  const page = fixture({initialRoute: 'main.main-menu'});
  await page.app.initializeUI();
  page.display.openOSC(); await flush();
  page.display.closeOSC(); await flush();
  assert.equal(page.state.current.name, 'base');
});

test('rejected open navigation settles its lock so the original close can run', async () => {
  const error = {type: 'synthetic_route_failure'};
  const page = fixture({routeFailure: error});
  await page.app.initializeUI();
  await assert.rejects(Promise.resolve(page.display.openOSC()), actual => actual === error);
  page.display.closeOSC();
  assert.equal(page.display.getNavigationState().open, false);
  assert.equal(page.display.getNavigationState().transitionPending, false);
});

test('close invalidates a delayed focus read without replaying its stale open', async () => {
  const focus = deferred();
  const page = fixture({focus: focus.promise});
  await page.app.initializeUI();
  const opening = page.display.openOSC();
  page.display.closeOSC();
  focus.resolve({data: {}});
  await opening; await flush();
  assert.equal(page.state.current.name, 'base');
  assert.equal(page.display.getNavigationState().openPending, false);
});

test('stale navigation completion cannot release a newer open generation', async () => {
  const page = fixture({deferRoutes: true});
  await page.app.initializeUI();
  const oldOpen = page.display.openOSC(); await flush();
  page.display.closeOSC();
  const newOpen = page.display.openOSC(); await flush();
  const current = page.display.getNavigationState();
  assert.equal(current.openPending, true);
  const superseded = {type: 'synthetic_superseded_transition'};
  page.routeRequests[0].task.reject(superseded);
  await assert.rejects(oldOpen, error => error === superseded);
  assert.equal(page.display.getNavigationState().generation, current.generation);
  assert.equal(page.display.getNavigationState().openPending, true);
  assert.equal(page.display.getNavigationState().transitionPending, true);
  // The close requested while the route was still base is a same-state no-op;
  // the remaining queued transition belongs to the newer open.
  page.completeRoute(1); await newOpen;
  assert.equal(page.display.getNavigationState().openPending, false);
  assert.equal(page.display.getNavigationState().transitionPending, false);
  assert.equal(page.state.current.name, 'main.main-menu');
});

test('Capture503 resolves degraded UI entry and hotkeys without calling Capture ready', async () => {
  const page = fixture();
  const degraded = await page.app.initializeUI();
  assert.equal(degraded.degraded, true);
  assert.equal(degraded.captureUnavailable, true);
  assert.equal(page.app.uiInitialization.phase, 'degraded');
  assert.equal(page.app.uiInitialization.shellReady, true);
  assert.equal(page.app.uiInitialization.status, 503);
  assert.equal(page.calls.setOscReady || 0, 0);
  assert.equal(page.calls['hotkeyService.init'], 1);
  page.display.openOSC(); await flush();
  assert.equal(page.state.current.name, 'main.main-menu');
  page.receiveHotkey({hotkeyId: 'Screenshot'});
  assert.equal(page.calls.screenshotActionDispatch, 1);
  page.display.closeOSC(); await flush();
  assert.equal(page.state.current.name, 'base');
  const again = await page.app.initializeUI();
  assert.equal(again.captureUnavailable, true);
  assert.equal(page.calls['hotkeyService.init'], 1);
  page.setCaptureAvailable(true);
  await page.app.initializeUI();
  assert.equal(page.app.uiInitialization.phase, 'ready');
  assert.equal(page.calls.setOscReady, 1);
  assert.equal(page.calls['hotkeyService.init'], 1);
  assert.equal(page.registrations.filter(x => x.route === '/ShadowPlay/v.1.0/Hotkey').length, 1);
});
