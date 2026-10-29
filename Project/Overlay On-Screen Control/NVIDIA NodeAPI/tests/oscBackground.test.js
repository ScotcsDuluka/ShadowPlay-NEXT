'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const sourcePath = path.join(__dirname, '../osc/Module/Overlay/Background/osc-background.js');
const storageKey = 'DulukaOscBackground';
const defaults = Object.freeze({
  mode: 'classic', opacity: 50, motion: true, particles: 80,
  links: true, react: true, speed: 1
});
const plain = value => JSON.parse(JSON.stringify(value));

function load(options = {}) {
  const values = options.values || Object.create(null);
  const events = Object.create(null);
  const state = { failWrites: !!options.failWrites, failReads: !!options.failReads, writes: 0 };
  const document = options.document || {
    readyState: 'loading',
    addEventListener(name, callback) { events[name] = callback; },
    removeEventListener() {}
  };
  const module = {}, directives = {};
  for (const name of ['config', 'directive', 'controller', 'factory', 'run']) {
    module[name] = () => module;
  }
  module.directive = (name, definition) => { directives[name] = definition; return module; };
  const window = Object.assign({
    document,
    localStorage: {
      getItem(key) {
        if (state.failReads) throw new Error('Storage is unavailable');
        return Object.prototype.hasOwnProperty.call(values, key) ? values[key] : null;
      },
      setItem(key, value) {
        if (state.failWrites) throw new Error('Storage quota exceeded');
        values[key] = String(value);
        state.writes++;
      },
      removeItem(key) {
        if (state.failWrites) throw new Error('Storage is unavailable');
        delete values[key];
      }
    },
    angular: { module: () => module },
    addEventListener() {}, removeEventListener() {},
    setTimeout, clearTimeout, console
  }, options.window);
  vm.runInNewContext(fs.readFileSync(sourcePath, 'utf8'), {
    window, document, console, setTimeout, clearTimeout
  }, { filename: sourcePath });
  const api = window.__dulukaOscBackground;
  assert(api, 'The background settings API must be usable before the DOM is ready');
  return { api, values, state, window, events, directives, document };
}

function renderer(options = {}) {
  const events = {}, frames = new Map(), cancelled = [];
  let nextFrame = 1;
  const painting = { arcs: 0, clears: 0, coordinates: [] };
  const ctx = {
    clearRect() { painting.clears++; },
    beginPath() {}, fill() {}, stroke() {},
    arc(...values) { painting.arcs++; painting.coordinates.push(...values); },
    moveTo(...values) { painting.coordinates.push(...values); },
    lineTo(...values) { painting.coordinates.push(...values); }
  };
  function node(tagName) {
    const classes = new Set(), attributes = {}, children = [], styles = {};
    return {
      tagName, children, parentNode: null, attributes,
      style: Object.assign(styles, { setProperty(key, value) { styles[key] = value; } }),
      classList: {
        toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); },
        contains(name) { return classes.has(name); }
      },
      setAttribute(name, value) { attributes[name] = value; },
      hasAttribute(name) { return Object.prototype.hasOwnProperty.call(attributes, name); },
      appendChild(child) { child.parentNode = this; children.push(child); return child; },
      insertBefore(child, before) {
        child.parentNode = this;
        const index = children.indexOf(before);
        children.splice(index < 0 ? 0 : index, 0, child);
      },
      removeChild(child) { children.splice(children.indexOf(child), 1); child.parentNode = null; },
      getContext() { return ctx; }
    };
  }
  const html = node('html'), body = node('body');
  const document = {
    readyState: 'complete', hidden: false, documentElement: html, body,
    createElement: node,
    getElementById(id) { return body.children.find(child => child.id === id) || null; },
    addEventListener(name, callback) { events[name] = callback; },
    removeEventListener() {}
  };
  const media = { matches: false, addListener(callback) { events.reducedMotion = callback; } };
  const window = {
    innerWidth: 1920, innerHeight: 1200,
    location: { hash: options.hash || '#/base' },
    matchMedia() { return media; },
    addEventListener(name, callback) { events[name] = callback; },
    requestAnimationFrame(callback) { const id = nextFrame++; frames.set(id, callback); return id; },
    cancelAnimationFrame(id) { cancelled.push(id); frames.delete(id); }
  };
  // Renderer behavior tests exercise the animated website backdrop, so storage
  // defaults to website mode; the shipped factory default is classic.
  const loaded = load({ document, window, values: options.values || {
    [storageKey]: JSON.stringify({ ...defaults, mode: 'website' })
  } });
  function tick(now = 100) {
    const pending = Array.from(frames.entries());
    frames.clear();
    for (const [, callback] of pending) callback(now);
  }
  return Object.assign(loaded, {
    html, body, painting, frames, cancelled, media, events, tick,
    root: document.getElementById('nv-osc-background'),
    navigate(hash) { loaded.window.location.hash = hash; events.hashchange(); }
  });
}

test('a fresh installation uses the website background with the classic 50% opacity', () => {
  const { api, state } = load();
  assert.deepEqual(plain(api.get()), defaults);
  assert.equal(state.writes, 0, 'Loading defaults must not write settings during page startup');
});

test('normalization clamps rendering limits and keeps explicit false settings', () => {
  const { api } = load();
  assert.deepEqual(plain(api.normalize({
    mode: 'classic', opacity: -1, motion: false, particles: 999,
    links: false, react: false, speed: 0, unrelated: 'discard'
  })), {
    mode: 'classic', opacity: 0, motion: false, particles: 120,
    links: false, react: false, speed: 0.25
  });
  const upper = api.normalize({ opacity: 101, particles: -9, speed: 5 });
  assert.equal(upper.opacity, 100);
  assert.equal(upper.particles, 0);
  assert.equal(upper.speed, 2);
  assert.equal(api.normalize({ mode: 'unknown' }).mode, 'classic');
});

test('non-finite and malformed settings cannot create invalid opacity, particle counts or animation speed', () => {
  const { api } = load();
  for (const invalid of [NaN, Infinity, -Infinity, undefined, null, '', true, [], {}]) {
    const result = api.normalize({ opacity: invalid, particles: invalid, speed: invalid });
    assert.equal(result.opacity, defaults.opacity, `opacity: ${String(invalid)}`);
    assert.equal(result.particles, defaults.particles, `particles: ${String(invalid)}`);
    assert.equal(result.speed, defaults.speed, `speed: ${String(invalid)}`);
  }
  for (const invalid of [undefined, null, false, 'false', 0, 1, {}, []]) {
    if (invalid === false) continue;
    const result = api.normalize({ motion: invalid, links: invalid, react: invalid });
    assert.equal(result.motion, true);
    assert.equal(result.links, true);
    assert.equal(result.react, true);
  }
  for (const invalid of [undefined, null, false, 'invalid', []]) {
    assert.deepEqual(plain(api.normalize(invalid)), defaults);
  }
});

test('saved settings survive a new page instance and preserve disabled features', () => {
  const f = load();
  const chosen = { mode: 'classic', opacity: 37, motion: false, particles: 12, links: false, react: false, speed: 1.5 };
  assert.equal(f.api.set(chosen), true);
  assert.deepEqual(JSON.parse(f.values[storageKey]), chosen);
  const reopened = load({ values: f.values });
  assert.deepEqual(plain(reopened.api.get()), chosen);
  assert.equal(reopened.state.writes, 0);
});

test('corrupt or inaccessible storage falls back to safe defaults', () => {
  for (const stored of ['{broken', 'null', 'false', '"wrong type"']) {
    const f = load({ values: { [storageKey]: stored } });
    assert.deepEqual(plain(f.api.get()), defaults);
  }
  assert.deepEqual(plain(load({ failReads: true }).api.get()), defaults);
});

test('callers cannot mutate active settings through input objects or returned copies', () => {
  const { api } = load();
  const chosen = { ...defaults, opacity: 42, particles: 20 };
  assert.equal(api.set(chosen), true);
  chosen.opacity = 0;
  const snapshot = api.get();
  snapshot.opacity = 100;
  snapshot.mode = 'invalid';
  assert.equal(api.get().opacity, 42);
  assert.equal(api.get().mode, 'classic');
  assert.equal(api.get().particles, 20);
});

test('a failed save retains the last committed background and does not announce an unapplied change', () => {
  const f = load();
  const committed = { ...defaults, opacity: 44, motion: false };
  assert.equal(f.api.set(committed), true);
  const saved = f.values[storageKey];
  let notifications = 0;
  f.api.subscribe(() => notifications++);
  const before = notifications;
  f.state.failWrites = true;
  assert.equal(f.api.set({ ...defaults, opacity: 90 }), false);
  assert.deepEqual(plain(f.api.get()), committed);
  assert.equal(f.values[storageKey], saved);
  assert.equal(notifications, before);
  assert.equal(typeof f.api.error, 'string');
  assert(f.api.error.length > 0);
  f.state.failWrites = false;
  assert.equal(f.api.set({ ...defaults, opacity: 60 }), true);
  assert.equal(f.api.get().opacity, 60);
  assert.equal(f.api.error, '');
});

test('subscriptions follow committed changes and unsubscribe independently', () => {
  const { api } = load();
  const first = [], second = [];
  const stopFirst = api.subscribe(value => first.push(plain(value)));
  const stopSecond = api.subscribe(value => second.push(plain(value)));
  assert.equal(typeof stopFirst, 'function');
  assert.equal(typeof stopSecond, 'function');
  const firstBefore = first.length, secondBefore = second.length;
  api.set({ ...defaults, opacity: 25 });
  assert.equal(first.length, firstBefore + 1);
  assert.equal(second.length, secondBefore + 1);
  assert.equal(first.at(-1).opacity, 25);
  stopFirst();
  api.set({ ...defaults, opacity: 75 });
  assert.equal(first.length, firstBefore + 1);
  assert.equal(second.length, secondBefore + 2);
  assert.equal(second.at(-1).opacity, 75);
  stopSecond();
});

test('reset restores the approved defaults and persists them for the next launch', () => {
  const f = load();
  assert.equal(f.api.set({ mode: 'classic', opacity: 0, motion: false, particles: 0, links: false, react: false, speed: 2 }), true);
  f.api.reset();
  assert.deepEqual(plain(f.api.get()), defaults);
  assert.deepEqual(plain(load({ values: f.values }).api.get()), defaults);
});

test('the closed OSC route has no visible background and schedules no animation', () => {
  const f = renderer();
  assert.equal(f.root.style.display, 'none');
  assert.equal(f.frames.size, 0);
  assert.equal(f.html.classList.contains('nv-osc-bg-website'), false);
  assert.equal(f.html.classList.contains('nv-osc-bg-classic'), false);
  f.navigate('#/base/');
  assert.equal(f.frames.size, 0);
});

test('game-overlay editor routes do not bring the opaque menu background over the live game', () => {
  const f = renderer({ hash: '#/base/main-menu' });
  assert.equal(f.root.style.display, 'block');
  assert.equal(f.frames.size, 1);

  // Game Filter / Photo Mode / Performance draw above a game frame, not a
  // fullscreen Main-menu backdrop. Both native and nested hash variants
  // must suspend the background immediately when navigation changes.
  for (const route of ['#/mods', '#/base/mods', '#/nvcamera', '#/base/nvcamera',
    '#/octoolmenu', '#/base/octoolmenu']) {
    f.navigate(route);
    assert.equal(f.root.style.display, 'none', route);
    assert.equal(f.frames.size, 0, route + ' must cancel the particle loop');
    assert.equal(f.html.classList.contains('nv-osc-bg-website'), false, route);
    assert.equal(f.html.classList.contains('nv-osc-bg-classic'), false, route);
    f.navigate('#/base/main-menu');
    assert.equal(f.root.style.display, 'block', 'menu background should return');
    assert.equal(f.frames.size, 1, 'animation should restart only for the menu');
  }

  f.html.setAttribute('data-osc-bg-preview', '');
  f.navigate('#/mods');
  assert.equal(f.root.style.display, 'none', 'stale preview must not cover the game editor');
  f.navigate('#/base/preferences');
  assert.equal(f.root.style.display, 'block', 'Settings background stays available');
});

test('opening the menu uses 50% opacity and maintains a single animation loop', () => {
  const f = renderer();
  f.navigate('#/base/main-menu');
  assert.equal(f.root.style.display, 'block');
  assert.equal(f.root.style.opacity, '0.5');
  assert.equal(f.html.classList.contains('nv-osc-bg-website'), true);
  assert.equal(f.frames.size, 1);
  for (let i = 0; i < 4; i++) f.events.hashchange();
  assert.equal(f.frames.size, 1, 'Repeated route notifications must not duplicate the render loop');
  f.tick(100);
  f.tick(132);
  assert.equal(f.frames.size, 1, 'Each frame must replace, rather than accumulate, its successor');
});

test('turning motion off or opacity down to zero cancels animation immediately', () => {
  const f = renderer({ hash: '#/base/main-menu' });
  f.api.set({ ...defaults, mode: 'website', motion: false });
  assert.equal(f.frames.size, 0);
  assert.equal(f.root.classList.contains('nv-osc-bg-paused'), true);
  assert(f.cancelled.length > 0);
  f.api.set({ ...defaults, mode: 'website' });
  assert.equal(f.frames.size, 1);
  f.api.set({ ...defaults, mode: 'website', opacity: 0 });
  assert.equal(f.root.style.opacity, '0');
  assert.equal(f.frames.size, 0);
  assert.equal(f.root.classList.contains('nv-osc-bg-paused'), true);
});

test('classic mode restores the native backdrop class and closing OSC stops rendering', () => {
  const f = renderer({ hash: '#/base/main-menu' });
  f.api.set({ ...defaults, mode: 'classic', opacity: 65 });
  assert.equal(f.root.style.display, 'block', 'Classic must reuse the full-viewport background surface');
  assert.equal(f.root.style.opacity, '0.65', 'Classic opacity must not be applied twice');
  assert.equal(f.frames.size, 0);
  assert.equal(f.html.classList.contains('nv-osc-bg-website'), false);
  assert.equal(f.html.classList.contains('nv-osc-bg-classic'), true);
  assert.equal(f.html.style['--nv-osc-bg-opacity'], '0.65');
  f.api.set({ ...defaults, mode: 'website' });
  assert.equal(f.frames.size, 1);
  f.navigate('#/base');
  assert.equal(f.root.style.display, 'none');
  assert.equal(f.frames.size, 0);
  assert.equal(f.html.classList.contains('nv-osc-bg-website'), false);
  assert.equal(f.html.classList.contains('nv-osc-bg-classic'), false);
});

test('hidden documents and reduced motion suspend animation while retaining a static backdrop', () => {
  const f = renderer({ hash: '#/base/main-menu' });
  f.document.hidden = true;
  f.events.visibilitychange();
  assert.equal(f.frames.size, 0);
  assert.equal(f.root.style.display, 'block');
  f.document.hidden = false;
  f.events.visibilitychange();
  assert.equal(f.frames.size, 1);
  f.media.matches = true;
  f.events.reducedMotion();
  assert.equal(f.frames.size, 0);
  assert.equal(f.root.classList.contains('nv-osc-bg-paused'), true);
});

test('particle rendering obeys count limits and keeps all canvas coordinates finite', () => {
  const f = renderer({ hash: '#/base/main-menu' });
  f.painting.arcs = 0;
  f.api.set({ ...defaults, mode: 'website', particles: 999, links: false });
  assert.equal(f.api.get().particles, 120);
  assert.equal(f.painting.arcs, 120, 'The drawing work must not exceed the configured particle cap');
  f.events.mousemove({ clientX: 0, clientY: 0 });
  f.tick(100);
  f.tick(1000000);
  f.window.innerWidth = 0;
  f.window.innerHeight = 0;
  f.events.resize();
  f.tick(1000032);
  assert(f.painting.coordinates.every(Number.isFinite));
  f.painting.arcs = 0;
  f.api.set({ ...defaults, mode: 'website', particles: -20 });
  f.tick(1000064);
  assert.equal(f.api.get().particles, 0);
  assert.equal(f.painting.arcs, 0, 'A zero-particle setting must actually remove particle work');
});

test('the decorative background cannot intercept pointer input or become an accessibility control', () => {
  const f = renderer();
  const css = fs.readFileSync(path.join(__dirname, '../osc/Module/Overlay/Background/osc-background.css'), 'utf8');
  assert.equal(f.root.attributes['aria-hidden'], 'true');
  assert.match(css, /#nv-osc-background\s*\{[^}]*pointer-events\s*:\s*none/);
  assert.match(css, /#nv-osc-background canvas\s*\{[^}]*pointer-events\s*:\s*none/);
  assert.equal(f.root.attributes.tabindex, undefined);
});

test('the companion settings mount leaves the editor controller intact and cleans up on close or destroy', () => {
  const f = renderer(), definition = f.directives.nvMainMenuLiveEditor;
  assert(definition, 'The background settings must attach as an additive companion directive');
  const pending = new Map(), compiled = [], lifecycle = {};
  let next = 0, watch, unwatchCount = 0, destroyedChildren = 0;
  const timeout = callback => { const id = ++next; pending.set(id, callback); return id; };
  timeout.cancel = id => pending.delete(id);
  const compile = node => child => compiled.push({ node, child });
  const directive = definition.at(-1)(compile, timeout);
  assert.equal(directive.require, 'nvMainMenuLiveEditor');
  assert.equal(directive.controller, undefined, 'The companion must not replace the other chat\'s editor controller');
  const editor = { editing: false, unrelatedState: 'preserved' };
  const scope = {
    flexMenu: editor, $$destroyed: false,
    $watch(expression, callback) {
      assert.equal(expression, 'flexMenu.editing');
      watch = callback;
      return () => unwatchCount++;
    },
    $on(name, callback) { lifecycle[name] = callback; },
    $new() { return { $$destroyed: false, $destroy() { this.$$destroyed = true; destroyedChildren++; } }; }
  };
  const panel = f.document.createElement('section');
  directive.link(scope, [{ querySelector: selector => selector === '.nv-live-editor' ? panel : null }]);
  const flush = () => { for (const [id, callback] of Array.from(pending)) { pending.delete(id); callback(); } };
  watch(true);
  flush();
  assert.equal(scope.flexMenu, editor);
  assert.equal(editor.unrelatedState, 'preserved');
  assert.equal(compiled.length, 1);
  assert.equal(panel.children.length, 1);
  assert.equal(panel.children[0].tagName, 'nv-osc-background-settings');
  watch(false);
  assert.equal(panel.children.length, 0);
  assert.equal(destroyedChildren, 1);
  watch(true);
  watch(false);
  flush();
  assert.equal(compiled.length, 1, 'Closing before the queued mount must prevent a stale settings panel');
  watch(true);
  flush();
  lifecycle.$destroy();
  assert.equal(unwatchCount, 1);
  assert.equal(destroyedChildren, 2);
  assert.equal(panel.children.length, 0);
});

test('a successful Reset background clears a previous save error in the settings UI', () => {
  const f = load(), directive = f.directives.nvOscBackgroundSettings.at(-1)();
  const scope = { $evalAsync(callback) { callback(); }, $on() {} };
  const controller = {};
  directive.controller.at(-1).call(controller, scope);
  f.state.failWrites = true;
  controller.settings.opacity = 80;
  controller.save();
  assert(controller.error.length > 0);
  assert.equal(controller.settings.opacity, defaults.opacity, 'The settings UI must roll back a failed save');
  f.state.failWrites = false;
  controller.reset();
  assert.equal(controller.error, '', 'A successful reset must not keep displaying the earlier storage error');
  assert.deepEqual(plain(controller.settings), defaults);
});

const motionDefaults = Object.freeze({ enabled: false, mode: 'osc', backgroundFade: true,
  topAnimation: true, mainMenuAnimation: true, openDuration: 500, closeDuration: 240 });
function animationController() {
  let settings = { ...motionDefaults }, locale = 'en-US', fail = false;
  const listeners = new Set(), saves = [], lifecycle = {};
  const motion = {
    get: () => ({ ...settings }),
    set(patch) {
      if (fail) return false;
      settings = { ...settings, ...patch }; saves.push({ ...settings });
      for (const listener of listeners) listener({ ...settings });
      return true;
    },
    reset() { return this.set(motionDefaults); },
    subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); },
    get error() { return fail ? 'Motion storage unavailable' : ''; }
  };
  const f = load({ window: { __dulukaOscMotionSettings: motion } });
  const definition = f.directives.nvOscBackgroundSettings.at(-1)();
  const scope = { $$destroyed: false, $evalAsync(callback) { callback(); }, $on(name, callback) { lifecycle[name] = callback; } };
  const controller = {};
  definition.controller.at(-1).call(controller, scope, { get(name) {
    assert.equal(name, '$translate'); return { use: () => locale };
  } });
  return { ...f, controller, scope, motion, saves, listeners, definition,
    language(value) { locale = value; }, fail(value) { fail = value; },
    destroy() { scope.$$destroyed = true; lifecycle.$destroy(); } };
}

test('Animation uses native Material controls without translating over switch internals', () => {
  const f = animationController(), template = f.definition.template;
  assert.ok(template.indexOf('nv-osc-motion-settings') < template.indexOf('nv-osc-background-settings'));
  for (const property of ['enabled', 'mode', 'backgroundFade', 'topAnimation', 'mainMenuAnimation', 'openDuration', 'closeDuration'])
    assert.ok(template.includes('ng-model="background.motionSettings.' + property + '"'), property);
  for (const label of ['Open duration', 'Close duration'])
    assert.ok(template.includes('aria-label="' + label + '" min="50" max="2000" step="10"'));
  assert.match(template, /<button[^>]*hover-focus[^>]*ng-click="background\.resetMotion\(\)"/);
  assert.match(template, /<button[^>]*hover-focus[^>]*ng-click="background\.reset\(\)"/);
  assert.doesNotMatch(template, /<md-switch\b[^>]*\btranslate=/,
    'a translation on the switch root destroys Angular Material toggle children');
  for (const key of ['oscAnimation', 'oscConnectingLines', 'oscReactPointer'])
    assert.ok(template.includes('<span translate="l10n.' + key + '">'), 'preserves existing background toggle labels');
  assert.equal(f.state.writes, 0); assert.equal(f.saves.length, 0, 'opening Settings must not persist defaults');
});

test('Animation changes and reset go only through the Motion API and leave Background settings intact', () => {
  const f = animationController(), ui = f.controller;
  ui.motionSettings.enabled = true; ui.saveMotion(); // exercise the active-animation UI (factory default is motion off)
  assert.equal(ui.motionAvailable, true); assert.equal(ui.oscMotionActive(), true);
  ui.motionSettings.openDuration = 820; ui.motionSettings.backgroundFade = false;
  assert.equal(ui.saveMotion(), true);
  assert.equal(f.motion.get().openDuration, 820); assert.equal(f.motion.get().backgroundFade, false);
  ui.motionSettings.mode = 'default'; ui.saveMotion();
  assert.equal(ui.oscMotionActive(), false, 'OSC component controls are disabled for Default');
  ui.motionSettings.mode = 'osc'; ui.motionSettings.enabled = false; ui.saveMotion();
  assert.equal(ui.oscMotionActive(), false, 'disabled animation disables component and timing controls');
  assert.equal(ui.resetMotion(), true); assert.deepEqual(plain(ui.motionSettings), motionDefaults);
  assert.equal(ui.oscMotionActive(), false, 'factory default leaves animation off');
  assert.deepEqual(plain(f.api.get()), defaults); assert.equal(f.state.writes, 0);
});

test('Animation failed saves restore committed values and successful reset clears the error', () => {
  const f = animationController(), ui = f.controller;
  ui.motionSettings.closeDuration = 400; ui.saveMotion();
  f.fail(true); ui.motionSettings.closeDuration = 1900;
  assert.equal(ui.saveMotion(), false); assert.equal(ui.motionSettings.closeDuration, 400);
  assert.equal(ui.motionError, 'Motion storage unavailable');
  assert.equal(ui.resetMotion(), false); assert.equal(ui.motionSettings.closeDuration, 400);
  f.fail(false); assert.equal(ui.resetMotion(), true);
  assert.equal(ui.motionError, ''); assert.deepEqual(plain(ui.motionSettings), motionDefaults);
});

test('Animation hides inapplicable options while retaining switches and saved choices for re-enabling', () => {
  const f = animationController(), ui = f.controller;
  ui.motionSettings.enabled = true; ui.saveMotion(); // exercise the active-animation UI (factory default is motion off)
  assert.equal(ui.motionTimingActive(), true);
  for (const name of ['backgroundFade', 'topAnimation', 'mainMenuAnimation']) ui.motionSettings[name] = false;
  ui.saveMotion();
  assert.equal(ui.oscMotionActive(), true, 'component switches remain available to turn effects back on');
  assert.equal(ui.motionTimingActive(), false, 'no timing controls when every effect is off');
  ui.motionSettings.topAnimation = true; ui.saveMotion();
  assert.equal(ui.motionTimingActive(), true);
  ui.motionSettings.mode = 'default'; ui.saveMotion();
  assert.equal(ui.oscMotionActive(), false); assert.equal(ui.motionTimingActive(), false);
  ui.motionSettings.mode = 'osc'; ui.saveMotion();
  assert.equal(ui.motionSettings.backgroundFade, false); assert.equal(ui.motionSettings.topAnimation, true);
  ui.motionSettings.enabled = false; ui.saveMotion();
  assert.equal(ui.oscMotionActive(), false); assert.equal(ui.motionTimingActive(), false);
  ui.motionSettings.enabled = true; ui.saveMotion();
  assert.equal(ui.motionTimingActive(), true);
  assert.equal(f.state.writes, 0, 'visibility never changes Background preferences');
});

test('Animation labels follow the native translator language and subscriptions stop with the companion scope', () => {
  const f = animationController(), ui = f.controller;
  assert.equal(ui.motionLabel('animation'), 'Animation');
  f.language('th-TH'); assert.equal(ui.motionLabel('animation'), 'แอนิเมชัน');
  assert.equal(ui.motionLabel('openDuration'), 'เวลาเปิด'); assert.equal(ui.motionLabel('closeDuration'), 'เวลาปิด');
  f.language('en-US'); assert.equal(ui.motionLabel('openDuration'), 'Open duration');
  f.motion.set({ topAnimation: false }); assert.equal(ui.motionSettings.topAnimation, false);
  assert.equal(f.listeners.size, 1); f.destroy(); assert.equal(f.listeners.size, 0);
  f.motion.set({ topAnimation: true }); assert.equal(ui.motionSettings.topAnimation, false, 'destroyed controls cannot receive live updates');
});

test('missing Motion API disables Animation settings without creating another preference store', () => {
  const f = load(), definition = f.directives.nvOscBackgroundSettings.at(-1)(), controller = {};
  definition.controller.at(-1).call(controller, { $evalAsync(fn) { fn(); }, $on() {} });
  assert.equal(controller.motionAvailable, false); assert.equal(controller.oscMotionActive(), false);
  assert.equal(controller.saveMotion(), false); assert.equal(controller.resetMotion(), false);
  assert.equal(controller.motionLabel('unavailable'), 'Animation settings are unavailable.');
  assert.equal(f.state.writes, 0); assert.deepEqual(plain(f.api.get()), defaults);
});

test('classic and website backgrounds use the same fixed full-screen canvas', () => {
  const f = renderer({ hash: '#/base/main-menu' });
  const css = fs.readFileSync(path.join(__dirname, '../osc/Module/Overlay/Background/osc-background.css'), 'utf8');
  const motion = fs.readFileSync(path.join(__dirname, '../osc/Module/Overlay/Motion/osc-motion.css'), 'utf8');
  assert.match(css, /#nv-osc-background\s*\{[^}]*position:fixed[^}]*top:0;right:0;bottom:0;left:0/);
  assert.match(css, /html\.nv-osc-bg-classic\s+#nv-osc-background\s*\{[^}]*background:#000!important/);
  assert.match(css, /html\.nv-osc-bg-classic\s+\.nv-osc\s*\{background:transparent!important\}/);
  assert.match(motion, /nv-motion-active\.nv-motion-backgroundFade:not\(\.nv-osc-bg-website\) \.nv-osc\{background:transparent!important\}/);
  f.api.set({ ...defaults, mode:'classic', opacity:80 });
  assert.equal(f.root.style.display,'block');
  assert.equal(f.root.style.opacity,'0.8');
  assert.equal(f.frames.size,0,'Classic should have no particle animation');
  f.api.set({ ...defaults, mode:'website', opacity:80 });
  assert.equal(f.root.style.display,'block');
  assert.equal(f.frames.size,1,'Website animation should remain unaffected');
  f.navigate('#/base');
  assert.equal(f.root.style.display,'none','Neither surface may remain on the closed OSC route');
});
