'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const sourcePath = path.join(__dirname, '../osc/mods/osc-background.js');
const storageKey = 'DulukaOscBackground';
const defaults = Object.freeze({
  mode: 'website', opacity: 50, motion: true, particles: 80,
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
  const loaded = load({ document, window });
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
  assert.equal(api.normalize({ mode: 'unknown' }).mode, 'website');
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
  assert.equal(api.get().mode, 'website');
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
  f.api.set({ ...defaults, motion: false });
  assert.equal(f.frames.size, 0);
  assert.equal(f.root.classList.contains('nv-osc-bg-paused'), true);
  assert(f.cancelled.length > 0);
  f.api.set(defaults);
  assert.equal(f.frames.size, 1);
  f.api.set({ ...defaults, opacity: 0 });
  assert.equal(f.root.style.opacity, '0');
  assert.equal(f.frames.size, 0);
  assert.equal(f.root.classList.contains('nv-osc-bg-paused'), true);
});

test('classic mode restores the native backdrop class and closing OSC stops rendering', () => {
  const f = renderer({ hash: '#/base/main-menu' });
  f.api.set({ ...defaults, mode: 'classic', opacity: 65 });
  assert.equal(f.root.style.display, 'none');
  assert.equal(f.frames.size, 0);
  assert.equal(f.html.classList.contains('nv-osc-bg-website'), false);
  assert.equal(f.html.classList.contains('nv-osc-bg-classic'), true);
  assert.equal(f.html.style['--nv-osc-bg-opacity'], '0.65');
  f.api.set(defaults);
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
  f.api.set({ ...defaults, particles: 999, links: false });
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
  f.api.set({ ...defaults, particles: -20 });
  f.tick(1000064);
  assert.equal(f.api.get().particles, 0);
  assert.equal(f.painting.arcs, 0, 'A zero-particle setting must actually remove particle work');
});

test('the decorative background cannot intercept pointer input or become an accessibility control', () => {
  const f = renderer();
  const css = fs.readFileSync(path.join(__dirname, '../osc/mods/osc-background.css'), 'utf8');
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
