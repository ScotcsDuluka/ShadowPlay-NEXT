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
    querySelector(selector) { if (options.missing) return null; if (selector === '.nv-osc') return overlay; return this.querySelectorAll(selector)[0] || null; },
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
  const context = { window, document, console, performance: window.performance, setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout };
  for (const file of ['osc-motion-engine.js', 'osc-motion.js']) vm.runInNewContext(fs.readFileSync(path.join(modPath, file), 'utf8'), context);
  runBlocks.forEach(definition => invoke(definition));
  const api = window.__dulukaOscMotion; assert(api, 'native adapter API must initialize');
  function emit(collection, type, ...args) { (collection[type] || []).slice().forEach(fn => fn(...args)); }
  return {
    api, window, document, html, body, overlay, topbar, group, bar, tiles, content, nodes, media, frames, timers, clock, rootScope, asyncQueue,
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
  f.clock.settle(); f.api.close(() => {}); f.clock.settle();
  assert.equal(parseFloat(f.group.styles['--nv-motion-x']), 1507); assertStopped(f);
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
test('rapid commands finish the running curve and settle the latest target without a stale native close', () => {
  const f = fixture(); open(f); let discarded = 0; f.api.close(() => { discarded++; }); f.clock.advance(40);
  const before = json(f.api.getState()); f.api.open(); assert.deepEqual(json(f.api.getState().pose), before.pose);
  assert.equal(f.api.getState().target, 'closed'); assert.equal(f.api.getState().requestedTarget, 'open'); f.clock.settle();
  assert.equal(discarded, 0); assert.equal(f.api.getState().pose.x, 0); assertStopped(f); assertNativeClean(f);
});
test('a long burst of opens and closes leaves only the final intent and no steady RAF polling', () => {
  const f = fixture(); let closes = 0; f.api.open();
  for (let i = 0; i < 40; i++) { f.clock.advance(7); if (i % 2) f.api.open(); else f.api.close(() => { closes++; }); }
  f.api.open(); f.clock.settle(); assert.equal(closes, 0); assert.equal(f.api.getState().pose.opacity, 1); assertStopped(f);
  f.api.close(() => { closes++; }); f.clock.settle(); assert.equal(closes, 1); assertStopped(f);
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
  open(f); delegate.closeOSC(); f.clock.advance(48); delegate.openOSC(); f.clock.settle(); assert.equal(opens, 0); assert.equal(closes, 0); assertStopped(f);
  delegate.closeOSC(); f.clock.advance(48); delegate.openOSC('main.gallery'); f.clock.settle(); assert.equal(opens, 1); assert.equal(closes, 0); assertStopped(f);
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
