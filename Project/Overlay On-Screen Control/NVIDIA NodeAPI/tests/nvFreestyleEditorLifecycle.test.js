'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const M = require('../osc/mods/nv-layout-v5-core.js');
const source = fs.readFileSync(path.join(__dirname, '../osc/mods/nv-freestyle-editor.js'), 'utf8');

// Only the DOM surface used by the actual editor is represented here. Angular's
// existing shell is deliberately retained, and a synthetic click is an error.
class Element {
  constructor(tag, className = '') {
    this.tagName = tag; this.className = className; this.children = [];
    this.parentNode = null; this.attributes = {}; this.style = {}; this.listeners = {};
    this._text = '';
    this.classList = {
      contains: name => this.className.split(/\s+/).includes(name),
      add: name => { if (!this.classList.contains(name)) this.className += ' ' + name; },
      remove: name => { this.className = this.className.split(/\s+/).filter(x => x !== name).join(' '); },
      toggle: (name, force) => {
        const show = force === undefined ? !this.classList.contains(name) : force;
        this.classList[show ? 'add' : 'remove'](name); return show;
      }
    };
  }
  set textContent(text) { this._text = String(text); for (const child of this.children) child.parentNode = null; this.children = []; }
  get textContent() { return this._text + this.children.map(x => x.textContent).join(''); }
  get firstChild() { return this.children[0] || null; }
  appendChild(child) {
    if (child.tagName === '#fragment') { for (const el of child.children.slice()) this.appendChild(el); return child; }
    child.remove(); this.children.push(child); child.parentNode = this; return child;
  }
  insertBefore(child, before) {
    if (!before) return this.appendChild(child);
    child.remove(); const index = this.children.indexOf(before);
    if (index < 0) throw Error('insertBefore target missing');
    this.children.splice(index, 0, child); child.parentNode = this; return child;
  }
  remove() {
    if (!this.parentNode) return;
    const siblings = this.parentNode.children; siblings.splice(siblings.indexOf(this), 1); this.parentNode = null;
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name] === undefined ? null : this.attributes[name]; }
  addEventListener(type, listener) { (this.listeners[type] ||= []).push(listener); }
  click() { throw Error('The Angular shell must be closed directly, never by a synthetic Done click'); }
  matchesSimple(selector) {
    const tag = selector.match(/^[\w-]+/);
    if (tag && this.tagName !== tag[0]) return false;
    for (const [, name] of selector.matchAll(/\.([\w-]+)/g)) if (!this.classList.contains(name)) return false;
    for (const [, name, value] of selector.matchAll(/\[([\w-]+)(?:=["']?([^\]"']+)["']?)?\]/g)) {
      if (this.getAttribute(name) === null || (value !== undefined && this.getAttribute(name) !== value)) return false;
    }
    return true;
  }
  matches(selector) {
    const parts = selector.replace(/\s*>\s*/g, ' ').trim().split(/\s+/);
    if (!this.matchesSimple(parts.pop())) return false;
    let ancestor = this.parentNode;
    while (parts.length) {
      const wanted = parts.pop();
      while (ancestor && !ancestor.matchesSimple(wanted)) ancestor = ancestor.parentNode;
      if (!ancestor) return false;
      ancestor = ancestor.parentNode;
    }
    return true;
  }
  closest(selector) { let el = this; while (el) { if (el.matches(selector)) return el; el = el.parentNode; } return null; }
  querySelectorAll(selector) {
    const found = [];
    const visit = parent => { for (const el of parent.children) { if (el.matches(selector)) found.push(el); visit(el); } };
    visit(this); return found;
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  getBoundingClientRect() {
    const css = Object.fromEntries(String(this.style.cssText || '').split(';').filter(Boolean).map(x => x.split(':')));
    const x = parseFloat(this.style.left || css.left) || 0, y = parseFloat(this.style.top || css.top) || 100;
    const width = parseFloat(this.style.width || css.width) || 240, height = parseFloat(this.style.height || css.height) || 24;
    return { x, y, left: x, top: y, width, height, right: x + width, bottom: y + height };
  }
}

function fixture({ enabled = false } = {}) {
  const calls = [], docListeners = {}, winListeners = {}, timers = new Map(); let nextTimer = 0;
  const f = { shellVisible: true, hit: null, motionTarget: 'open', nativeActions: 0, shellCloses: 0 };
  const html = new Element('html', 'nv-live-layout-editing'), body = new Element('body'); html.appendChild(body);
  const host = new Element('section', 'nv-live-editor nv-freestyle-settings'), head = new Element('header');
  const done = new Element('button', 'osc-button-green'); head.appendChild(done); host.appendChild(head);
  const controls = new Element('div', 'nv-freestyle-controls'), library = new Element('div', 'nv-live-library');
  const palette = new Element('div', 'nv-live-palette'); library.appendChild(palette); controls.appendChild(library); host.appendChild(controls);
  const legacy = new Element('details', 'nv-freestyle-legacy'); host.appendChild(legacy); body.appendChild(host);
  for (const key of M.CONTROLS) { const button = new Element('button'); button.setAttribute('data-menu-key', key); palette.appendChild(button); }
  const main = new Element('nv-main-menu'), root = new Element('div', 'main-menu-container'), bar = new Element('md-menu-bar');
  bar.style.cssText = 'left:100px;top:100px;width:1440px;height:240px';
  const menu = new Element('md-menu'), nativeButton = new Element('button', 'menu-button');
  menu.appendChild(nativeButton); bar.appendChild(menu); root.appendChild(bar); main.appendChild(root); body.appendChild(main);
  let saved = M.defaultLayout(), draft = null, preview = null, flag = enabled, lease = false;
  const current = () => draft || saved;
  const A = {
    enabled: () => flag,
    get: current, renderState: () => preview || current(),
    sync: () => M.geometry(preview || current(), 'edit', 1440),
    editOwnership(value) { lease = value; calls.push(['ownership', value]); },
    beginEdit() { calls.push(['begin']); draft = M.clone(saved); },
    edit(fn) { calls.push(['edit']); draft = fn(current()); },
    done() { calls.push(['done']); saved = M.clone(current()); draft = null; preview = null; },
    cancel() { calls.push(['cancel']); draft = null; preview = null; },
    enable(value) { calls.push(['enable', value]); flag = value; },
    preview(value) { calls.push(['preview', !!value]); preview = value; },
    undo() { calls.push(['undo']); }
  };
  const scope = { $evalAsync(fn) { fn(); }, flexMenu: { done() {
    calls.push(['shell.done']); f.shellCloses++; f.shellVisible = false; html.classList.remove('nv-live-layout-editing');
  } } };
  const document = {
    readyState: 'loading', documentElement: html, body,
    createElement: tag => new Element(tag), createDocumentFragment: () => new Element('#fragment'),
    querySelector(selector) {
      if (selector === '.nv-live-editor.nv-freestyle-settings') return f.shellVisible ? host : null;
      if (selector.startsWith('nv-main-menu:not')) return root;
      return body.querySelector(selector);
    },
    elementFromPoint: () => f.hit,
    addEventListener(type, listener) { (docListeners[type] ||= []).push(listener); }
  };
  const window = {
    __nvNextLayoutV5: A, NvShadowPlayLayoutV5: M, innerWidth: 1920, innerHeight: 1200,
    __dulukaOscMotion: { phase: 'idle', getState: () => ({ target: f.motionTarget }) },
    angular: { element(el) { assert.equal(el, host); return { scope: () => scope }; } },
    setInterval() {}, setTimeout(fn, delay) { const id = ++nextTimer; timers.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    addEventListener(type, listener) { (winListeners[type] ||= []).push(listener); }
  };
  vm.runInNewContext(source, { window, document }, { timeout: 2000 });
  Object.assign(f, { A, calls, window, document, host, nativeButton, done, timers, editor: window.__nvFreestyleEditor,
    flag: () => flag, lease: () => lease, preview: () => preview,
    scan: () => window.__nvFreestyleEditor.scan(),
    emit(type, target, more = {}) {
      const event = { target, button: 0, clientX: 120, clientY: 100, key: '', ...more,
        preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; },
        stopImmediatePropagation() { this.immediate = true; } };
      for (const fn of docListeners[type] || []) { fn(event); if (event.immediate) break; }
      if (target === nativeButton && !event.stopped && (type === 'click' || (type === 'keydown' && ['Enter', ' '].includes(event.key)))) f.nativeActions++;
      return event;
    },
    blur() { for (const fn of winListeners.blur || []) fn(); },
    fireTimer(delay) { const entry = [...timers].find(([, timer]) => timer.delay === delay); assert.ok(entry, 'expected delayed insertion preview'); timers.delete(entry[0]); entry[1].fn(); }
  });
  return f;
}

function startDrag(f) {
  const handle = f.document.querySelector('[data-f-drag-group="g1"]');
  const destination = f.document.querySelector('[data-f-group="g2"]');
  const rect = destination.getBoundingClientRect();
  f.hit = destination;
  f.emit('mousedown', handle, { buttons: 1, clientX: 120, clientY: 100 });
  f.emit('mousemove', destination, { buttons: 1, clientX: rect.left + 1, clientY: rect.top + 10 });
  assert.ok(f.document.querySelector('.nv-f-drag-ghost'), 'real editor created drag ghost');
}

test('native click, Enter and Space are captured during Angular shell startup before V5 scan', () => {
  const f = fixture();
  assert.equal(f.editor.isEditing(), false);
  for (const [type, more] of [['click', {}], ['keydown', { key: 'Enter' }], ['keydown', { key: ' ' }]]) {
    const event = f.emit(type, f.nativeButton, more);
    assert.ok(event.prevented && event.stopped && event.immediate, type + ' blocked before editor start');
  }
  assert.equal(f.nativeActions, 0);
  assert.deepEqual(f.calls, [], 'guard does not start or persist an editor');
  f.document.documentElement.classList.remove('nv-live-layout-editing');
  f.emit('click', f.nativeButton);
  assert.equal(f.nativeActions, 1, 'native behavior resumes outside editing shell');
});

test('Cancel closes original Angular shell directly and releases only the transient lease', () => {
  const f = fixture(); f.scan();
  assert.equal(f.editor.isEditing(), true); assert.equal(f.lease(), true); assert.equal(f.flag(), false);
  f.editor.apply(s => M.autoFill(s, false));
  assert.equal(f.A.get().autoFill, false);
  f.editor.cancel(true);
  assert.equal(f.A.get().autoFill, true, 'pre-edit model restored');
  assert.equal(f.shellCloses, 1); assert.equal(f.lease(), false); assert.equal(f.flag(), false);
  assert.equal(f.editor.isEditing(), false); assert.equal(f.document.querySelector('.nv-f-overlay'), null);
  assert.equal(f.calls.some(x => x[0] === 'done' || x[0] === 'enable'), false, 'Cancel never persists');
  f.scan(); assert.equal(f.editor.isEditing(), false, 'closed Angular shell cannot reopen on scan');
});

test('Escape restores draft and closes Angular shell without a synthetic native Done click', () => {
  const f = fixture(); f.scan(); f.editor.apply(s => M.autoFill(s, false));
  const event = f.emit('keydown', f.host, { key: 'Escape' });
  assert.ok(event.prevented && event.stopped); assert.equal(f.shellCloses, 1);
  assert.equal(f.A.get().autoFill, true); assert.equal(f.flag(), false); assert.equal(f.lease(), false);
  assert.equal(f.calls.some(x => x[0] === 'done' || x[0] === 'enable'), false);
});

test('Done saves current draft once, persists V5 ownership and closes the Angular shell', () => {
  const f = fixture(); f.scan(); f.editor.apply(s => M.autoFill(s, false));
  const event = f.emit('click', f.done);
  assert.ok(event.prevented && event.stopped); assert.equal(f.A.get().autoFill, false);
  assert.equal(f.flag(), true); assert.equal(f.lease(), false); assert.equal(f.shellCloses, 1);
  assert.equal(f.editor.isEditing(), false); assert.equal(f.document.querySelector('.nv-f-overlay'), null);
  assert.deepEqual(f.calls.filter(x => ['done', 'enable', 'shell.done'].includes(x[0])), [['done'], ['enable', true], ['shell.done']]);
  f.scan(); assert.equal(f.calls.filter(x => x[0] === 'begin').length, 1);
});

test('blur clears actual drag ghost, delayed insertion preview and pending target', () => {
  const f = fixture(); f.scan(); startDrag(f); f.fireTimer(360);
  assert.ok(f.preview(), 'delayed insertion preview is active');
  f.blur();
  assert.equal(f.document.querySelector('.nv-f-drag-ghost'), null); assert.equal(f.preview(), null);
  assert.equal(f.editor.dragState().kind, null); assert.equal(f.editor.dragState().pending, null);
  assert.equal(f.timers.size, 0); assert.equal(f.editor.isEditing(), true, 'blur ends drag without saving or closing editor');
  assert.equal(f.flag(), false);
});

test('Host hover with buttons zero cannot replace a held drag target or delayed insertion preview', () => {
  const f = fixture(); f.scan(); startDrag(f); f.fireTimer(360);
  const held = f.editor.dragState();
  assert.equal(held.target.id, 'g2'); assert.equal(held.target.edge, 'before');
  assert.equal(held.pending.groupId, 'g2'); assert.equal(held.pending.ready, true);
  const before = JSON.stringify(held), previewBefore = JSON.stringify(f.preview());
  const unrelated = f.document.querySelector('[data-f-group="g4"]');
  const rect = unrelated.getBoundingClientRect(); f.hit = unrelated;
  // The native Host sends these real-cursor hovers between injected held moves.
  for (let i = 0; i < 3; i++) {
    f.emit('mousemove', unrelated, { buttons: 0, clientX: rect.left + rect.width / 2, clientY: rect.top + 20 });
  }
  assert.equal(JSON.stringify(f.editor.dragState()), before, 'hover leaves held target and pending insertion intact');
  assert.equal(JSON.stringify(f.preview()), previewBefore, 'hover does not cancel or replace rendered preview');
  assert.ok(f.document.querySelector('.nv-f-drag-ghost'), 'held drag remains active');
  f.blur();
  assert.equal(f.editor.dragState().kind, null); assert.equal(f.editor.dragState().pending, null);
  assert.equal(f.preview(), null); assert.equal(f.document.querySelector('.nv-f-drag-ghost'), null);
});

test('route exit cancels active drag and draft, removes guides, and releases temporary owner', () => {
  const f = fixture(); f.scan(); startDrag(f);
  f.shellVisible = false; f.scan();
  assert.equal(f.editor.isEditing(), false); assert.equal(f.lease(), false); assert.equal(f.flag(), false);
  assert.equal(f.document.querySelector('.nv-f-drag-ghost'), null); assert.equal(f.document.querySelector('.nv-f-overlay'), null);
  assert.equal(f.timers.size, 0); assert.equal(f.calls.filter(x => x[0] === 'cancel').length, 1);
  assert.equal(f.shellCloses, 0, 'route removal does not synthesize a shell command');
});

test('Motion closed target prevents stale Angular editor from acquiring or restarting a lease', () => {
  const f = fixture(); f.motionTarget = 'closed'; f.scan(); f.scan();
  assert.equal(f.editor.isEditing(), false); assert.deepEqual(f.calls, []);
  f.motionTarget = 'open'; f.scan(); assert.equal(f.editor.isEditing(), true);
  f.motionTarget = 'closed'; f.scan(); f.scan();
  assert.equal(f.editor.isEditing(), false); assert.equal(f.lease(), false); assert.equal(f.shellCloses, 1);
  assert.equal(f.calls.filter(x => x[0] === 'begin').length, 1, 'closed Motion never restarts stale shell');
  assert.equal(f.calls.some(x => x[0] === 'done' || x[0] === 'enable'), false);
});
