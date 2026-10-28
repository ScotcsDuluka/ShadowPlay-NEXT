'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const modPath = path.join(__dirname, '../osc/mods');
const TIMING = {
  open: { duration: 500, bezier: [0, 0.9524242401123044, 0.27999999999999997, 1] },
  closed: { duration: 240, bezier: [0.6681970923837989, 0.04272720336914062, 0.00500000000000001, 1] },
};
const json = value => JSON.parse(JSON.stringify(value));

// Run the actual reversible engine and Angular adapter against existing native
// nodes; the test clock advances RAF only when requested, never in a busy loop.
function fixture(options = {}) {
  let now = 0, nextId = 1, clones = 0, paintCount = 0;
  const frames = new Map(), timers = new Map(), documentEvents = {}, windowEvents = {};
  const decorators = {}, lifecycle = {}, runBlocks = [], asyncQueue = [];
  const clock = {
    requestAnimationFrame(fn) { const id = nextId++; frames.set(id, fn); return id; },
    cancelAnimationFrame(id) { frames.delete(id); },
    setTimeout(fn, delay = 0) { const id = nextId++; timers.set(id, { fn, due: now + delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    frame(step = 16) {
      now += step; const pending = [...frames]; frames.clear(); pending.forEach(([, fn]) => fn(now));
      for (let limit = 0; limit < 100; limit++) {
        const due = [...timers].filter(([, t]) => t.due <= now).sort((a, b) => a[1].due - b[1].due)[0];
        if (!due) return; timers.delete(due[0]); due[1].fn();
      }
      throw Error('Unbounded timer scheduling');
    },
    advance(ms) { while (ms > 0) { const step = Math.min(16, ms); clock.frame(step); ms -= step; } },
    timersOnly(ms) {
      now += ms;
      for (let limit = 0; limit < 100; limit++) {
        const due = [...timers].filter(([, t]) => t.due <= now).sort((a, b) => a[1].due - b[1].due)[0];
        if (!due) return;
        timers.delete(due[0]); due[1].fn();
      }
      throw Error('Unbounded timer-only scheduling');
    },
    settle(limit = 240) { while ((frames.size || timers.size) && limit-- > 0) clock.frame(); assert(limit > 0, 'animation must release all scheduled frames'); },
  };
  const nodes = [];
  function descendants(node) { return node.children.flatMap(child => [child, ...descendants(child)]); }
  function matches(node, selector) {
    return selector.split(',').some(part => {
      part = part.trim(); const attr = part.match(/^\[([^\]]+)\]$/);
      if (attr) return node.hasAttribute(attr[1]);
      if (part === 'md-menu') return node.kind === 'block';
      if (part === 'md-menu-bar') return node.kind === 'bar';
      if (part === 'canvas') return node.name === 'canvas';
      for (const key of ['block', 'group', 'top']) if (part.includes('data-nv-motion-' + key)) return node.hasAttribute('data-nv-motion-' + key);
      return false;
    });
  }
  function element(name, bounds = { left: 0, top: 0, width: 100, height: 80 }) {
    const styles = {}, priorities = {}, attributes = {}, classes = new Set(), children = [];
    const node = {
      name, styles, attributes, children, parentNode: null, parentElement: null, isConnected: true,
      styleWrites: [], attributeWrites: [], rectReads: 0, handlers: { click() {}, keydown() {} },
      getBoundingClientRect() { node.rectReads++; return { ...bounds, x: bounds.left, y: bounds.top, right: bounds.left + bounds.width, bottom: bounds.top + bounds.height }; },
      getAttribute(key) { return Object.hasOwn(attributes, key) ? attributes[key] : null; },
      hasAttribute(key) { return Object.hasOwn(attributes, key); },
      setAttribute(key, value) { node.attributeWrites.push(key); attributes[key] = String(value); }, removeAttribute(key) { delete attributes[key]; },
      appendChild(child) { children.push(child); child.parentNode = child.parentElement = node; return child; },
      removeChild(child) { children.splice(children.indexOf(child), 1); child.parentNode = child.parentElement = null; return child; },
      remove() { if (node.parentNode) node.parentNode.removeChild(node); },
      contains(child) { return node === child || children.some(c => c.contains(child)); },
      cloneNode() { clones++; throw Error('Native controls must never be cloned'); },
      querySelectorAll(selector) { return descendants(node).filter(child => matches(child, selector)); },
      querySelector(selector) { return node.querySelectorAll(selector)[0] || null; },
      closest(selector) { return matches(node, selector) ? node : (node.parentNode && node.parentNode.closest(selector)); },
      classList: {
        add(...names) { names.forEach(name => classes.add(name)); }, remove(...names) { names.forEach(name => classes.delete(name)); },
        contains(name) { return classes.has(name); }, toggle(name, force) { if (force === undefined ? !classes.has(name) : force) classes.add(name); else classes.delete(name); },
      },
      getContext() { return { fillStyle: '', fillRect() { paintCount++; }, clearRect() {} }; },
    };
    node.style = new Proxy({
      setProperty(key, value, priority = '') { node.styleWrites.push(key); styles[key] = String(value); priorities[key] = priority; },
      getPropertyValue(key) { return styles[key] || ''; }, getPropertyPriority(key) { return priorities[key] || ''; },
      removeProperty(key) { const old = styles[key]; delete styles[key]; delete priorities[key]; return old; },
    }, { set(target, key, value) { node.styleWrites.push(key); styles[key] = String(value); target[key] = value; return true; } });
    Object.defineProperty(node, 'className', { get: () => [...classes].join(' '), set: value => { classes.clear(); String(value).split(/\s+/).forEach(c => classes.add(c)); } });
    nodes.push(node); return node;
  }
  const html = element('html'), body = element('body'), overlay = element('overlay');
  const topbar = element('topbar', { left: 0, top: 0, width: 1680, height: 70 });
  const content = element('settings-content', { left: 70, top: 140, width: 1295, height: 840 });
  const group = element('native-toolbar', { left: 175, top: 140, width: 1330, height: 210 });
  const bar = element('native-menu-bar', { left: 175, top: 140, width: 1330, height: 210 }); bar.kind = 'bar';
  bar.style.setProperty('transform', 'translateX(-50%)'); bar.styleWrites.length = 0;
  const tiles = [
    element('left-top', { left: 175, top: 140, width: 210, height: 70 }),
    element('left-bottom', { left: 175, top: 280, width: 210, height: 70 }),
    element('center', { left: 420, top: 140, width: 840, height: 210 }),
    element('right', { left: 1295, top: 140, width: 210, height: 210 }),
    element('hidden', { left: 9000, top: 0, width: 0, height: 0 }),
  ];
  tiles.forEach(tile => { tile.kind = 'block'; bar.appendChild(tile); }); group.appendChild(bar);
  const document = {
    documentElement: html, body, hidden: !!options.hidden, readyState: 'complete',
    querySelectorAll(selector) {
      if (options.missing) return [];
      if (selector.includes('nv-main-menu') && selector.includes('md-toolbar.list')) return options.settings ? [] : [group];
      if (selector.includes('nv-osc-top-bar')) return [topbar];
      if (selector === '.nv-osc .osc-content') return options.settings ? [content] : [];
      if (selector.includes('data-nv-motion-')) return nodes.filter(node => matches(node, selector));
      if (selector === 'md-menu') return tiles;
      return [];
    },
    querySelector(selector) { if (options.missing) return null; if (selector === '.nv-osc') return overlay; if(options.unified&&selector==='nv-main-menu:not([data-customize-preview]) .main-menu-container')return group; return this.querySelectorAll(selector)[0] || null; },
    addEventListener(type, fn) { (documentEvents[type] ||= []).push(fn); }, createElement(name) { return element(name); },
  };
  const media = { matches: !!options.reduced, addListener(fn) { media.change = fn; }, addEventListener(type, fn) { media.change = fn; } };
  const rootScope = { $evalAsync(fn) { if (options.deferAsync) asyncQueue.push(fn); else fn(); }, $on(type, fn) { (lifecycle[type] ||= []).push(fn); return () => {}; } };
  const timeout = (fn, delay) => clock.setTimeout(fn, delay); timeout.cancel = clock.clearTimeout;
  const dependencies = { $rootScope: rootScope, $timeout: timeout, $state: { current: { name: 'base' } } };
  function invoke(definition, extra = {}) { return definition.at(-1)(...definition.slice(0, -1).map(name => ({ ...dependencies, ...extra })[name])); }
  const module = {
    config(definition) { invoke(definition, { $provide: { decorator(name, definition) { decorators[name] = definition; } } }); return module; },
    run(definition) { runBlocks.push(definition); return module; },
  };
  const window = {
    ...clock, document, innerWidth: 1680, innerHeight: 1050, performance: { now: () => now }, location: { hash: '#/base' },
    matchMedia: () => media, angular: { module: () => module }, __dulukaOscMotionTiming: json(TIMING),
    __dulukaOscBackground: { get: () => ({ opacity: options.bgOpacity === undefined ? 50 : options.bgOpacity }) },
    getComputedStyle(node) { return { display: 'block', visibility: 'visible', opacity: '1', transform: node.styles.transform || 'none', getPropertyValue: key => node.style.getPropertyValue(key) }; },
    addEventListener(type, fn) { (windowEvents[type] ||= []).push(fn); },
  };
  const stored = new Map(options.motionSettings ? [['DulukaOscMotionSettings', JSON.stringify(options.motionSettings)]] : []);
  window.localStorage = { getItem: key => stored.get(key) || null,
    setItem(key, value) { if(options.failStorage)throw Error('Storage full');stored.set(key,value); } };
  const context = { window, document, console, performance: window.performance, setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout };
  for (const file of ['osc-motion-engine.js', 'osc-motion-timing.js', 'osc-motion.js']) vm.runInNewContext(fs.readFileSync(path.join(modPath, file), 'utf8'), context);
  runBlocks.forEach(definition => invoke(definition));
  const api = window.__dulukaOscMotion; assert(api, 'native adapter API must initialize');
  function emit(collection, type, ...args) { (collection[type] || []).slice().forEach(fn => fn(...args)); }
  return {
    route: dependencies.$state, api, window, document, html, body, overlay, topbar, group, bar, tiles, content, nodes, media, frames, timers, clock, rootScope, asyncQueue,stored,
    emitDocument: (type, ...args) => emit(documentEvents, type, ...args), emitWindow: (type, ...args) => emit(windowEvents, type, ...args),
    emitRoute: (type, ...args) => emit(lifecycle, type, ...args), decorate: delegate => invoke(decorators.oscDisplayService, { $delegate: delegate }),
    flushAsync() { while (asyncQueue.length) asyncQueue.shift()(); }, clones: () => clones, paintCount: () => paintCount,
  };
}
function open(f) { f.api.prepareOpen(); f.api.playOpen(); f.clock.settle(); }
function assertStopped(f) { assert.equal(f.frames.size, 0); assert.equal(f.timers.size, 0); assert.equal(f.body.children.length, 0); }
function assertNativeClean(f) {
  for (const node of [f.group, f.topbar, ...f.tiles, f.content]) {
    assert.equal(Object.keys(node.attributes).some(name => /^data-nv-motion-/.test(name)), false, node.name);
    assert.equal(Object.keys(node.styles).some(name => /^--nv-motion-/.test(name)), false, node.name);
  }
}

test('loads the selected 500 ms / 240 ms free Bézier curves', () => {
  const f = fixture(), timing = json(f.api.timing);
  assert.equal(timing.open.duration, 500); assert.equal(timing.closed.duration, 240);
  assert.deepEqual(timing.open.bezier, TIMING.open.bezier); assert.deepEqual(timing.closed.bezier, TIMING.closed.bezier);
  f.api.prepareOpen(); f.api.playOpen(); f.clock.frame(); f.clock.frame(); f.clock.advance(100);
  assert.deepEqual(json(f.api.getState().timing.open.bezier), TIMING.open.bezier);
  assert.equal(f.api.getState().requestedTarget, 'open'); assert.equal(f.api.getState().phase, 'opening');
  f.clock.settle(); assertStopped(f); assertNativeClean(f);
});
test('whole native toolbar translates together, while visible blocks scale independently without stagger', () => {
  const f = fixture(); f.api.prepareOpen(); f.api.playOpen(); f.clock.frame(); f.clock.frame(); f.clock.advance(80);
  assert(f.group.hasAttribute('data-nv-motion-group')); f.tiles.slice(0, -1).forEach(node => assert(node.hasAttribute('data-nv-motion-block'), node.name));
  assert.equal(f.tiles.at(-1).hasAttribute('data-nv-motion-block'), false); assert(f.topbar.hasAttribute('data-nv-motion-top'));
  assert(!f.tiles.some(node => node.styleWrites.includes('--nv-motion-delay'))); f.clock.settle(); assertStopped(f); assertNativeClean(f);
});
test('native node identity, layout coordinates, menu-bar transform and actions stay untouched', () => {
  const f = fixture(), originals = [f.group, f.bar, f.topbar, ...f.tiles].map(node => ({ node, rect: node.getBoundingClientRect(), handlers: node.handlers, parent: node.parentNode }));
  open(f); f.api.close(() => {}); f.clock.settle();
  for (const { node, rect, handlers, parent } of originals) {
    assert.deepEqual(node.getBoundingClientRect(), rect); assert.equal(node.handlers, handlers); assert.equal(node.parentNode, parent);
    assert(node.styleWrites.every(key => /^--nv-motion-/.test(key)), `${node.name}: ${node.styleWrites}`);
  }
  assert.equal(f.bar.style.getPropertyValue('transform'), 'translateX(-50%)'); assert.equal(f.clones(), 0); assertStopped(f);
});
test('edge geometry is cached rather than read again every animation frame', () => {
  const f = fixture(); f.api.prepareOpen(); f.api.playOpen(); f.clock.frame(); f.clock.frame(); f.clock.frame();
  const reads = f.tiles.reduce((sum, node) => sum + node.rectReads, 0); assert(reads > 0); f.clock.advance(160);
  assert.equal(f.tiles.reduce((sum, node) => sum + node.rectReads, 0), reads);
  const state = f.api.getState(); assert(state.pose.x > -1 && state.pose.x < 0); assert(state.pose.scale > .24 && state.pose.scale < 1);
  // Visible union ends at x=1505; the zero-size hidden node at x=9000 is excluded.
  assert(Math.abs(parseFloat(f.group.styles['--nv-motion-x']) - state.pose.x * 1507) < .00001);
  f.clock.settle(); f.api.close(() => {}); f.clock.advance(120);
  assert(Math.abs(parseFloat(f.group.styles['--nv-motion-x'])-f.api.getState().pose.x*1507)<.00001);
  f.clock.settle();
  assert.equal(f.group.style.getPropertyValue('--nv-motion-x'),'','a completed close cannot leave offscreen transforms over the UI');
  assertStopped(f);assertNativeClean(f);
});
test('animation restores existing owned attributes and custom variables instead of deleting prior values', () => {
  const f = fixture(), block = f.tiles[0];
  block.setAttribute('data-nv-motion-block', 'prior-owner'); block.style.setProperty('--nv-motion-scale', '.9', 'important');
  open(f); assert.equal(block.getAttribute('data-nv-motion-block'), 'prior-owner');
  assert.equal(block.style.getPropertyValue('--nv-motion-scale'), '.9'); assert.equal(block.style.getPropertyPriority('--nv-motion-scale'), 'important');
  f.api.close(() => {}); f.clock.advance(48); f.api.cancel();
  assert.equal(block.getAttribute('data-nv-motion-block'), 'prior-owner'); assert.equal(block.style.getPropertyValue('--nv-motion-scale'), '.9'); assertStopped(f);
});
test('production CSS keeps scaled blocks on their original lower baseline without replacing menu-bar layout', () => {
  const css = fs.readFileSync(path.join(modPath, 'osc-motion.css'), 'utf8');
  assert.match(css, /\[data-nv-motion-block\][^}]*transform-origin:\s*left bottom/i);
  assert.doesNotMatch(css, /(?:left|right|top|bottom|width|height)\s*:/i, 'CSS must not hard-code native menu geometry');
  assert.doesNotMatch(css, /md-menu-bar[^}]*transform\s*:/i, 'menu-bar placement belongs to the live layout editor');
});
test('BG opacity is configured alpha times progress rather than repeatedly multiplied', () => {
  const f = fixture({ bgOpacity: 50 }); f.api.prepareOpen(); f.api.playOpen(); f.clock.frame(); f.clock.frame(); f.clock.advance(96);
  const alpha = Number(f.html.style.getPropertyValue('--nv-motion-bg-alpha')); assert(alpha > 0 && alpha < .5);
  assert(Math.abs(alpha - .5 * f.api.getState().pose.bgProgress) < .00001); f.clock.settle(); f.api.close(() => {}); f.clock.advance(80);
  assert(Math.abs(Number(f.html.style.getPropertyValue('--nv-motion-bg-alpha')) - .5 * f.api.getState().pose.bgProgress) < .00001); f.clock.settle(); assertStopped(f);
});
test('native close commits once after the selected exit graph completes', () => {
  const f = fixture(); open(f); let closes = 0; f.api.close(() => { closes++; }); assert.equal(closes, 0); f.clock.advance(120); assert.equal(closes, 0);
  f.clock.settle(); assert.equal(closes, 1); assertStopped(f); f.clock.advance(1000); assert.equal(closes, 1);
});
test('duplicate closes coalesce instead of dispatching multiple native close calls', () => {
  const f = fixture(); open(f); let first = 0, duplicate = 0; f.api.close(() => { first++; }); f.api.close(() => { duplicate++; }); f.clock.settle();
  assert.equal(first, 1); assert.equal(duplicate, 0); assertStopped(f);
});
test('rapid alternating presses chain through the engine: every curve plays its full ms before the queued intent plays', () => {
  // User spec 2026-10-27: "เปิด > จบ ปิด > จบ > เปิด > จบ วนได้เรื่อยๆ" — the
  // engine defers mid-curve commands and auto-reverses at the endpoint, so
  // alternating presses chain full curves. The latest intent wins per curve.
  const f = fixture(); open(f); let discarded = 0; f.api.close(() => { discarded++; }); f.clock.advance(40);
  const before = json(f.api.getState()); f.api.open(); assert.deepEqual(json(f.api.getState().pose), before.pose);
  assert.equal(f.api.getState().target, 'closed'); assert.equal(f.api.getState().requestedTarget, 'open'); f.clock.settle();
  assert.equal(discarded, 0); assert.equal(f.api.getState().pose.x, 0); assertStopped(f); assertNativeClean(f);
});
test('a long burst of opens and closes leaves only the final intent and no steady RAF polling', () => {
  const f = fixture(); let closes = 0; f.api.open();
  for (let i = 0; i < 40; i++) { f.clock.advance(7); if (i % 2) f.api.open(); else f.api.close(() => { closes++ }); }
  f.api.open(); f.clock.settle(); assert.equal(closes, 0); assert.equal(f.api.getState().pose.opacity, 1); assertStopped(f);
  f.api.close(() => { closes++ }); f.clock.settle(); assert.equal(closes, 1); assertStopped(f);
});
test('cancel releases deferred linking frames and drops obsolete close callbacks', () => {
  const f = fixture(); let closes = 0; f.api.prepareOpen(); f.api.playOpen(); f.api.cancel(); f.clock.settle(); assertStopped(f); assertNativeClean(f);
  open(f); f.api.close(() => { closes++; }); f.clock.advance(48); f.api.cancel(); f.clock.settle(); assert.equal(closes, 0); assertStopped(f); assertNativeClean(f);
});
test('Settings fallback animates the existing page without rebuilding native controls', () => {
  const f = fixture({ settings: true }); f.api.open(); f.clock.frame(); f.clock.frame(); f.clock.frame();
  assert(f.content.hasAttribute('data-nv-motion-group') || f.content.hasAttribute('data-nv-motion-block')); f.clock.settle(); assert.equal(f.clones(), 0); assertStopped(f); assertNativeClean(f);
});
for (const [name, options] of [['reduced motion', { reduced: true }], ['hidden document', { hidden: true }], ['missing overlay', { missing: true }]]) {
  test(`${name} does not defer native closure`, () => { const f = fixture(options); let closes = 0; f.api.open(); f.clock.settle(); f.api.close(() => { closes++; }); assert.equal(closes, 1); assertStopped(f); });
}
test('visibility or reduced-motion changes during exit finish requested closure once', () => {
  for (const change of ['hidden', 'reduced']) {
    const f = fixture(); open(f); let closes = 0; f.api.close(() => { closes++; }); f.clock.advance(48);
    if (change === 'hidden') { f.document.hidden = true; f.emitDocument('visibilitychange'); } else { f.media.matches = true; f.media.change({ matches: true }); }
    f.clock.settle(); assert.equal(closes, 1, change); assertStopped(f);
  }
});
test('pagehide discards obsolete native actions and all animation resources', () => {
  const f = fixture(); open(f); let closes = 0; f.api.close(() => { closes++; }); f.emitWindow('pagehide'); f.clock.settle(); assert.equal(closes, 0); assertStopped(f); assertNativeClean(f);
});
test('Angular display-service decoration preserves original identity, actions, context and arguments', () => {
  const f = fixture(), calls = [], owner = {}, notification = () => {};
  const delegate = { closeOSC(...args) { calls.push({ method: 'close', owner: this, args }); }, openOSC(...args) { calls.push({ method: 'open', owner: this, args }); return 'native'; }, openOSCForNotification: notification };
  const actual = f.decorate(delegate); assert.equal(actual, delegate); assert.equal(actual.openOSCForNotification, notification);
  open(f); actual.closeOSC.call(owner, 'reason', 5); assert.equal(calls.length, 0); f.clock.settle();
  assert.equal(calls.length, 1); assert.equal(calls[0].owner, owner); assert.deepEqual(calls[0].args, ['reason', 5]);
  assert.equal(actual.openOSC.call(owner, 'main.gallery'), 'native'); assert.equal(calls[1].owner, owner); assert.deepEqual(calls[1].args, ['main.gallery']);
  assert.equal(f.decorate(actual), actual); f.api.cancel(); assertStopped(f);
});
test('a reopened overlay suppresses stale close work already queued for Angular dispatch', () => {
  const f = fixture({ deferAsync: true }); let closes = 0; const delegate = f.decorate({ closeOSC() { closes++; }, openOSC() {} });
  open(f); delegate.closeOSC(); f.clock.settle(); assert(f.asyncQueue.length > 0); assert.equal(closes, 0);
  delegate.openOSC(); f.flushAsync(); f.clock.settle(); assert.equal(closes, 0); assert.equal(f.api.getState().requestedTarget, 'open'); assertStopped(f);
});
test('pending-close reopen does not toggle native open unless explicitly given a route', () => {
  const f = fixture(); let opens = 0, closes = 0; const delegate = f.decorate({ closeOSC() { closes++; }, openOSC() { opens++; } });
  open(f); f.route.current.name="main.main-menu"; delegate.closeOSC(); f.clock.advance(48); delegate.openOSC(); f.clock.settle(); assert.equal(opens, 0); assert.equal(closes, 0); assertStopped(f);
  delegate.closeOSC(); f.clock.advance(48); delegate.openOSC('main.gallery'); f.clock.settle(); assert.equal(opens, 1); assert.equal(closes, 0); assertStopped(f);
});
test('OPEN from base calls original NVIDIA route even if prior Motion close has not settled', () => {
  const f = fixture();
  let originalOpenCalls = 0, originalCloseCalls = 0;
  const service = f.decorate({
    closeOSC() { originalCloseCalls++; },
    openOSC() { originalOpenCalls++; return 'nvidia-route-opened'; }
  });
  open(f);
  service.closeOSC(); f.clock.advance(48);
  assert.equal(f.api.getState().requestedTarget, 'closed');
  assert.equal(f.route.current.name, 'base');
  const result = service.openOSC();
  assert.equal(originalOpenCalls, 1, 'Angular must receive original NVIDIA openOSC()');
  assert.equal(result, 'nvidia-route-opened');
  f.clock.settle();
  assert.equal(originalCloseCalls, 0, 'stale close callback must be cancelled');
});
test('base-to-overlay entry waits for linking and in-overlay Gallery navigation does not replay', () => {
  const f = fixture(), base = { name: 'base' }, main = { name: 'base.main-menu' }, gallery = { name: 'base.gallery.files' };
  f.emitRoute('$stateChangeStart', {}, main, {}, base); assert.equal(f.api.phase, 'preparing');
  f.emitRoute('$stateChangeSuccess', {}, main, {}, base); f.emitRoute('$viewContentLoaded'); f.clock.settle(); assert.equal(f.api.getState().pose.opacity, 1); assertStopped(f);
  const writes = f.group.styleWrites.length; f.emitRoute('$stateChangeStart', {}, gallery, {}, main); f.emitRoute('$stateChangeSuccess', {}, gallery, {}, main); f.emitRoute('$viewContentLoaded');
  assert.equal(f.frames.size, 0); assert.equal(f.group.styleWrites.length, writes);
});
test('failed or base transitions cancel deferred entrance and release nodes', () => {
  for (const failed of [true, false]) {
    const f = fixture(), base = { name: 'base' }, main = { name: 'base.main-menu' };
    f.emitRoute('$stateChangeStart', {}, main, {}, base); f.emitRoute('$stateChangeSuccess', {}, main, {}, base);
    if (failed) f.emitRoute('$stateChangeError'); else f.emitRoute('$stateChangeSuccess', {}, base, {}, main);
    f.clock.settle(); assertStopped(f); assertNativeClean(f);
  }
});

test('the actual OSC page loads the selected engine and timing before its native adapter',()=>{
  const html=fs.readFileSync(path.join(__dirname,'../osc/index.html'),'utf8');
  const assets=['/mods/osc-motion-engine.js','/mods/osc-motion-timing.js','/mods/osc-motion.js'];
  const positions=assets.map(asset=>html.indexOf('src="'+asset));
  assert(positions.every(i=>i>=0),'all motion scripts must be linked');
  assert(positions[0]<positions[1]&&positions[1]<positions[2],'motion script order must be engine -> timing -> adapter');
  const saved=JSON.parse(fs.readFileSync(path.join(__dirname,'../../../../build_test/NVIDIA Background_Test/selected-motion-timing.json'),'utf8'));
  const code=fs.readFileSync(path.join(modPath,'osc-motion-timing.js'),'utf8');
  const vmWindow={};vm.runInNewContext(code,{window:vmWindow});
  assert.deepEqual(json(vmWindow.__dulukaOscMotionTiming),saved);
});

test('OSC Motion keeps native blocks fully opaque while fading only the background',()=>{
  const f=fixture({bgOpacity:45});
  f.api.prepareOpen();f.api.playOpen();
  f.clock.frame();f.clock.frame();f.clock.advance(110);
  assert.equal(f.api.getState().phase,'opening');
  assert(f.tiles.some(tile=>tile.hasAttribute('data-nv-motion-block')));
  f.tiles.forEach(tile=>assert.equal(tile.style.getPropertyValue('--nv-motion-opacity'),'',tile.name));
  assert.equal(f.group.style.getPropertyValue('--nv-motion-group-opacity'),'');
  assert.equal(f.topbar.style.getPropertyValue('--nv-motion-opacity'),'');
  const css=fs.readFileSync(path.join(modPath,'osc-motion.css'),'utf8');
  assert.match(css,/\[data-nv-motion-block\][^}]*opacity:1!important/i);
  assert.match(css,/\[data-nv-motion-group\][^}]*opacity:1!important/i);
  assert.match(css,/\[data-nv-motion-top\][^}]*opacity:1!important/i);
  assert.match(css,/\#nv-osc-background\{opacity:var\(--nv-motion-bg-alpha/i);
  f.clock.settle();assertStopped(f);
});

test('Customize transitions are gated behind editing mode, not startup or OSC motion',()=>{
  const css=fs.readFileSync(path.join(modPath,'main-menu-live.css'),'utf8');
  const js=fs.readFileSync(path.join(modPath,'main-menu-live.js'),'utf8');
  assert.match(css,/\.main-menu-container md-menu-bar\{height:240px!important;transition:none\}/);
  assert.match(css,/\.nv-flex-face\{[^}]*animation:none\}/);
  assert.match(css,/html\.nv-live-layout-editing:not\(\.nv-motion-preparing\):not\(\.nv-motion-active\)[^\{]*md-menu-bar/);
  assert.match(js,/self\.start=function\(\)\{self\.editing=true;editingMotion\(true\)/);
  assert.match(js,/self\.done=function\(\)\{self\.editing=false;editingMotion\(false\)/);
});

test('V4 positions its native tiles before OSC Motion captures geometry, then freezes them throughout opening',()=>{
  const f=fixture({unified:true});let placements=0,preparedBeforeCapture=false;
  f.window.__dulukaUnifiedMainMenu={
    enabled:()=>true,
    prepareForMotion(root){
      placements++;
      assert.equal(root,f.group);
      preparedBeforeCapture=!root.hasAttribute('data-nv-motion-group');
      f.bar.style.setProperty('transform','translateX(160px) scale(.8)','important');
      return true;
    }
  };
  f.api.prepareOpen();f.api.playOpen();f.clock.frame();f.clock.frame();
  assert.equal(placements,1,'V4 preflight must run once before first capture');
  assert.equal(preparedBeforeCapture,true,'Motion must capture V4 positions, not legacy V2 positions');
  assert.equal(f.group.hasAttribute('data-nv-motion-group'),true);
  assert.equal(f.bar.style.getPropertyValue('transform'),'translateX(160px) scale(.8)');
  f.clock.advance(175);
  assert.equal(placements,1,'No geometry writes during opening frames');
  f.clock.settle();
  assert.equal(placements,1);
  assert.equal(f.bar.style.getPropertyValue('transform'),'translateX(160px) scale(.8)');
  assert.equal(f.api.getState().target,'open');assertNativeClean(f);assertStopped(f);
});
test('V2 animation remains unchanged without V4 feature flag',()=>{
  const f=fixture({unified:true});
  let count=0;
  f.window.__dulukaUnifiedMainMenu={enabled:()=>false,prepareForMotion(){count++}};
  open(f);assert.equal(count,0);
  f.api.cancel();f.clock.settle();assertNativeClean(f);
});
test('rapid reopen defers the opening animation until the V5 layout owner can reflow, then animates its geometry',()=>{
  // Measured live 2026-10-27: a null preflight used to be ignored, so the
  // entrance ran on stale native geometry and V5 applied mid-motion (~530 ms
  // jump). The preflight must gate the capture like an uncapturable view.
  const f=fixture({unified:true});let calls=0,ready=false;
  f.window.__nvNextLayoutV5={
    enabled:()=>true,
    prepareForMotion(root){calls++;assert.equal(root,f.group);return ready?{width:1360,scale:1}:null;}
  };
  f.api.prepareOpen();f.api.playOpen();
  f.clock.frame();f.clock.frame();
  assert.equal(f.api.getState().phase,'preparing');
  assert.equal(f.group.hasAttribute('data-nv-motion-group'),false,'must not capture while the owner cannot reflow');
  f.clock.frame();f.clock.frame();
  assert.ok(calls>=3,'preflight must be retried instead of animating stale geometry');
  ready=true;f.clock.frame();
  assert.equal(f.group.hasAttribute('data-nv-motion-group'),true,'capture runs once the owner reports geometry');
  assert.equal(f.api.getState().phase,'opening');
  f.clock.settle();assertStopped(f);assertNativeClean(f);
});
test('a layout owner that never becomes ready skips motion instead of animating replaced geometry',()=>{
  const f=fixture({unified:true});let calls=0;
  f.window.__nvNextLayoutV5={enabled:()=>true,prepareForMotion(){calls++;return null;}};
  f.api.prepareOpen();f.api.playOpen();f.clock.settle();
  assert.ok(calls>=1);
  assert.equal(f.group.hasAttribute('data-nv-motion-group'),false,'no capture of geometry the owner never applied');
  assert.equal(f.api.getState().phase,'idle');assertNativeClean(f);assertStopped(f);
});
test('stalled V5 preparation releases the queued close even if animation frames stop',()=>{
  const f=fixture({unified:true});let closes=0;
  f.window.__nvNextLayoutV5={enabled:()=>true,prepareForMotion(){return null;}};
  f.api.prepareOpen();f.api.playOpen();f.clock.frame();f.clock.frame();
  assert.equal(f.api.phase,'preparing');
  f.api.close(()=>closes++);
  f.clock.timersOnly(1749);assert.equal(closes,0);
  f.clock.timersOnly(1);assert.equal(closes,1);
  assert.equal(f.api.phase,'idle');assertNativeClean(f);assertStopped(f);
});
test('a mid-close reopen never cuts the running close; it completes then auto-reverses',()=>{
  // Live symptom 2026-10-27 ("ปิดไม่จบ"): cancelling a running close whenever
  // the V5 preflight was not ready snapped the menu back mid-exit. The curve
  // must always finish; the engine then reverses to the latest intent.
  const f=fixture({unified:true});let calls=0,ready=true;
  f.window.__nvNextLayoutV5={enabled:()=>true,prepareForMotion(root){calls++;return ready?{width:1360,scale:1}:null;}};
  f.api.prepareOpen();f.api.playOpen();
  f.clock.frame();f.clock.frame();
  assert.equal(f.api.getState().phase,'opening');
  ready=false;f.api.close(()=>{});f.clock.frame();f.clock.frame();
  assert.equal(f.api.getState().phase,'opening','the running curve must finish first');
  f.api.open();
  assert.equal(f.api.getState().requestedTarget,'open','the latest press replaces the queued intent');
  f.clock.settle();
  assert.equal(f.api.getState().target,'open');assertNativeClean(f);assertStopped(f);
});
test('a route change during a close cancels the transition so the next open starts fresh',()=>{
  // Route changes cancel through the motion $stateChangeSuccess handler — the
  // only sanctioned way a destroyed view stops being animated.
  const f=fixture({unified:true});
  f.window.__nvNextLayoutV5={enabled:()=>true,prepareForMotion(){return {width:1360,scale:1};}};
  f.api.prepareOpen();f.api.playOpen();
  f.clock.frame();f.clock.frame();
  assert.equal(f.api.getState().phase,'opening');
  f.api.close(()=>{});f.clock.frame();f.clock.frame();
  assert.equal(f.api.getState().requestedTarget,'closed');
  f.route.current.name='base';
  f.emitRoute('$stateChangeSuccess',{},f.route.current);
  f.clock.settle();
  assert.equal(f.api.getState().phase,'idle');assertNativeClean(f);assertStopped(f);
});

test('Default delegates original NVIDIA open/close without OSC transforms or synthetic animation delays',()=>{
  // unified:true supplies the real native menu node the V5 owner would find;
  // the live page always has it once the route mounts.
  const f=fixture({motionSettings:{mode:'default'},unified:true}),calls=[];
  let preflights=0;
  f.window.__nvNextLayoutV5={enabled:()=>true,prepareForMotion(){preflights++;}};
  const native=f.decorate({openOSC(route){calls.push(['open',route]);return 'native-open';},closeOSC(reason){calls.push(['close',reason]);}});
  assert.equal(native.openOSC('main.main-menu'),'native-open');
  f.api.prepareOpen();assert.equal(f.api.phase,'idle');assert.equal(f.html.classList.contains('nv-motion-preparing'),false);
  f.api.playOpen();f.clock.settle();
  assert.equal(preflights,1,'native positioning still precedes the settled view');
  assert.equal(f.api.getState().target,'open');assertNativeClean(f);
  native.closeOSC('original');
  assert.deepEqual(calls,[['open','main.main-menu'],['close','original']]);
  assert.equal(f.api.phase,'idle');assertStopped(f);
  assert.equal(f.group.styleWrites.length,0);assert.equal(f.topbar.styleWrites.length,0);
  assert.equal(f.html.classList.contains('nv-motion-default'),true);
});

test('Animation off and all OSC components off keep nodes visible and commit close instantly',()=>{
  for(const motionSettings of [{enabled:false},{backgroundFade:false,topAnimation:false,mainMenuAnimation:false}]){
    const f=fixture({motionSettings});f.api.prepareOpen();
    assert.equal(f.html.classList.contains('nv-motion-preparing'),false);
    f.api.playOpen();f.clock.settle();assertNativeClean(f);
    let closed=0;f.api.close(()=>closed++);
    assert.equal(closed,1);assertStopped(f);
    assert.equal(f.api.getState().target,'closed');
  }
});

test('OSC background, top and main-menu switches independently omit only their own animation',()=>{
  for(const name of ['backgroundFade','topAnimation','mainMenuAnimation']){
    const f=fixture({motionSettings:{[name]:false}});f.api.prepareOpen();
    assert.equal(f.html.classList.contains('nv-motion-'+name),false);
    if(name==='backgroundFade')assert.equal(f.html.style.getPropertyValue('--nv-motion-bg-alpha'),'');
    f.api.playOpen();f.clock.frame();f.clock.frame();f.clock.advance(96);
    assert.equal(f.group.hasAttribute('data-nv-motion-group'),name!=='mainMenuAnimation');
    assert.equal(f.topbar.hasAttribute('data-nv-motion-top'),name!=='topAnimation');
    assert.equal(!!f.html.style.getPropertyValue('--nv-motion-bg-alpha'),name!=='backgroundFade');
    if(name==='mainMenuAnimation')assert(f.tiles.every(tile=>!tile.hasAttribute('data-nv-motion-block')));
    f.clock.settle();assertNativeClean(f);assertStopped(f);
  }
  const css=fs.readFileSync(path.join(modPath,'osc-motion.css'),'utf8');
  assert.match(css,/nv-motion-preparing\.nv-motion-mainMenuAnimation/);
  assert.match(css,/nv-motion-preparing\.nv-motion-topAnimation/);
  assert.match(css,/nv-motion-preparing\.nv-motion-backgroundFade/);
});

test('configured full-range durations are honored exactly and Reset restores approved curves',()=>{
  const f=fixture({motionSettings:{openDuration:50,closeDuration:2000}});
  f.api.prepareOpen();f.api.playOpen();f.clock.frame();f.clock.frame();
  f.clock.advance(49);assert.equal(f.api.phase,'opening');
  f.clock.advance(1);assert.equal(f.api.phase,'idle');assertStopped(f);
  let closes=0;f.api.close(()=>closes++);f.clock.advance(1999);
  assert.equal(f.api.phase,'closing');assert.equal(closes,0);
  f.clock.advance(1);assert.equal(closes,1);assertStopped(f);
  assert.equal(f.window.__dulukaOscMotionSettings.reset(),true);
  assert.equal(f.api.timing.open.duration,500);assert.equal(f.api.timing.closed.duration,240);
  assert.deepEqual(json(f.api.timing.open.bezier),TIMING.open.bezier);
  assert.deepEqual(json(f.api.timing.closed.bezier),TIMING.closed.bezier);
});

test('shared engine constructor accepts the same50..2000ms range as persisted timing',()=>{
  const f=fixture();
  for(const duration of [50,2000]){
    const controller=f.window.createMotionController({duration,
      requestFrame:f.clock.requestAnimationFrame,cancelFrame:f.clock.cancelAnimationFrame,
      now:()=>f.window.performance.now()});
    assert.equal(controller.getTiming().open.duration,duration);
    controller.open();f.clock.advance(duration-1);assert.equal(controller.getState().phase,'opening');
    f.clock.advance(1);assert.equal(controller.getState().phase,'idle');
    controller.cancel();assertStopped(f);
  }
});

test('configuration changes settle an active transition and do not leave stale native close callbacks',()=>{
  const f=fixture();open(f);let closes=0;f.api.close(()=>closes++);f.clock.advance(48);
  assert.equal(f.api.phase,'closing');
  f.window.__dulukaOscMotionSettings.set({enabled:false});
  assert.equal(closes,1);assert.equal(f.api.phase,'idle');f.clock.settle();assertStopped(f);assertNativeClean(f);
  f.window.__dulukaOscMotionSettings.set({enabled:true,mode:'osc'});open(f);
  f.api.close(()=>closes++);f.clock.advance(48);f.api.open();
  f.window.__dulukaOscMotionSettings.set({mode:'default'});f.clock.settle();
  assert.equal(closes,1,'reopening discards the obsolete close even across engine change');
  assert.equal(f.api.getState().target,'open');assertStopped(f);assertNativeClean(f);
});

test('input guard runs only during an actual OSC transition, never Default, disabled or preparation',()=>{
  for(const motionSettings of [{},{mode:'default'},{enabled:false}]){
    const f=fixture({motionSettings});
    function activate(){const ev={target:{closest:()=>f.tiles[0]},preventDefault(){this.prevented=true;},stopImmediatePropagation(){this.stopped=true;}};f.emitDocument('click',ev);return !!ev.prevented;}
    f.api.prepareOpen();assert.equal(activate(),false);
    f.api.playOpen();f.clock.frame();f.clock.frame();f.clock.advance(48);
    assert.equal(activate(),motionSettings.mode!=='default'&&motionSettings.enabled!==false);
    f.clock.settle();assert.equal(activate(),false);
  }
});

test('OSC restores a previously owned background alpha value and priority on release',()=>{
  const f=fixture();f.html.style.setProperty('--nv-motion-bg-alpha','.37','important');
  open(f);assert.equal(f.html.style.getPropertyValue('--nv-motion-bg-alpha'),'.37');
  assert.equal(f.html.style.getPropertyPriority('--nv-motion-bg-alpha'),'important');
  f.api.close(()=>{});f.clock.advance(48);f.api.cancel();
  assert.equal(f.html.style.getPropertyValue('--nv-motion-bg-alpha'),'.37');
  assert.equal(f.html.style.getPropertyPriority('--nv-motion-bg-alpha'),'important');
  assertStopped(f);assertNativeClean(f);
});

test('stalled OSR animation frames cannot leave opening input guards or queued native close stuck',()=>{
  const f=fixture();
  f.api.prepareOpen();f.api.playOpen();f.clock.frame();f.clock.frame();
  assert.equal(f.api.phase,'opening');assert.ok(f.frames.size);
  f.clock.timersOnly(749);assert.equal(f.api.phase,'opening');
  f.clock.timersOnly(1);
  assert.equal(f.api.phase,'idle');assert.equal(f.api.getState().target,'open');
  assertStopped(f);assertNativeClean(f);
  let closed=0;f.api.close(()=>closed++);assert.equal(f.api.phase,'closing');
  f.clock.timersOnly(490);
  assert.equal(closed,1);assert.equal(f.api.phase,'idle');
  assertStopped(f);assertNativeClean(f);
  assert.equal(f.html.classList.contains('nv-motion-active'),false);
  const event={target:{closest:()=>f.tiles[0]},preventDefault(){this.prevented=true;},stopImmediatePropagation(){}};
  f.emitDocument('click',event);assert.equal(event.prevented,undefined);
  f.api.open();f.clock.settle();assert.equal(f.api.getState().target,'open');
  assertStopped(f);assertNativeClean(f);
});

test('native close failure still removes Motion transforms and allows the next open',()=>{
  const f=fixture();open(f);
  f.api.close(()=>{throw Error('native close failed');});
  assert.throws(()=>f.clock.advance(240),/native close failed/);
  assertStopped(f);assertNativeClean(f);
  assert.equal(f.html.classList.contains('nv-motion-active'),false);
  f.api.open();f.clock.settle();
  assert.equal(f.api.phase,'idle');assert.equal(f.api.getState().target,'open');
});

test('missing preparation frames cannot hide the overlay or retain an Alt-Z close callback forever',()=>{
  for(const missing of [false,true]){
    const f=fixture({missing});let closed=0;
    f.api.prepareOpen();f.api.playOpen();
    if(missing){f.clock.frame();f.clock.frame();}
    f.api.close(()=>closed++);
    assert.equal(f.api.phase,'preparing');
    f.clock.timersOnly(750);
    assert.equal(closed,1);assert.equal(f.api.phase,'idle');
    assert.equal(f.html.classList.contains('nv-motion-preparing'),false);
    assertStopped(f);assertNativeClean(f);
  }
  const f=fixture();f.api.prepareOpen();f.api.playOpen();
  f.clock.timersOnly(750);
  assert.equal(f.api.phase,'idle');assert.equal(f.api.getState().target,'open');
  assertStopped(f);assertNativeClean(f);
});
