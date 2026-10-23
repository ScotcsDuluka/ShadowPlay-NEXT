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
    this.parentNode = null; this.attributes = {}; this.listeners = {};
    const values = {}, priorities = {};
    this.style = {
      setProperty(name, value, priority = '') { values[name] = String(value); priorities[name] = priority; this[name] = String(value); },
      getPropertyValue(name) { return values[name] === undefined ? this[name] || '' : values[name]; },
      getPropertyPriority(name) { return priorities[name] || ''; },
      removeProperty(name) { const old = this.getPropertyValue(name); delete values[name]; delete priorities[name]; delete this[name]; return old; }
    };
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

function fixture({ enabled = false, lingerOnClose = false } = {}) {
  const calls = [], docListeners = {}, winListeners = {}, timers = new Map(); let nextTimer = 0;
  const f = { shellVisible: true, hit: null, motionTarget: 'open', nativeActions: 0, shellCloses: 0, shellCloseObservations: [] };
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
  const scope = { $evalAsync(fn) { fn(); }, flexMenu: { editing: true, done() {
    f.shellCloseObservations.push({ visibility: host.style.getPropertyValue('visibility'),
      visibilityPriority: host.style.getPropertyPriority('visibility'), pointer: host.style.getPropertyValue('pointer-events'),
      pointerPriority: host.style.getPropertyPriority('pointer-events'), wasAngularEditing: scope.flexMenu.editing });
    calls.push(['shell.done']); f.shellCloses++; scope.flexMenu.editing = false;
    f.shellVisible = lingerOnClose; html.classList.remove('nv-live-layout-editing');
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
  Object.assign(f, { A, calls, window, document, host, scope, nativeButton, done, timers, editor: window.__nvFreestyleEditor,
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

test('real Host zero-button-mask mouse sequence starts, updates and completes Group swap while native actions remain guarded', () => {
  const f = fixture(); f.scan();
  const handle = f.document.querySelector('[data-f-drag-group="g1"]');
  const destination = f.document.querySelector('[data-f-group="g2"]'), rect = destination.getBoundingClientRect();
  f.hit = destination;
  f.emit('mousedown', handle, { buttons: 0, clientX: 120, clientY: 100 });
  f.emit('mousemove', destination, { buttons: 0, clientX: rect.left + rect.width / 2, clientY: rect.top + 40 });
  assert.ok(f.document.querySelector('.nv-f-drag-ghost'), 'Host move with buttons zero starts real drag');
  assert.equal(f.editor.dragState().target.id, 'g2');
  f.emit('mouseup', destination, { buttons: 0, clientX: rect.left + rect.width / 2, clientY: rect.top + 40 });
  assert.equal(f.A.get().panels.left[0].id, 'g2'); assert.equal(f.A.get().panels.center[0].id, 'g1');
  assert.equal(f.editor.dragState().kind, null); assert.equal(f.document.querySelector('.nv-f-drag-ghost'), null);
  for (const [type, more] of [['click', {}], ['keydown', { key: 'Enter' }], ['keydown', { key: ' ' }]]) {
    const event = f.emit(type, f.nativeButton, more); assert.ok(event.prevented && event.stopped, 'native command remains guarded');
  }
  assert.equal(f.nativeActions, 0); assert.equal(f.flag(), false, 'drag stays an unsaved edit');
});

test('real Host zero-button-mask drag accepts delayed Group insertion and clears its preview on drop', () => {
  const f = fixture(); f.scan();
  const handle = f.document.querySelector('[data-f-drag-group="g2"]');
  const destination = f.document.querySelector('[data-f-group="g4"]'), rect = destination.getBoundingClientRect();
  f.hit = destination;
  f.emit('mousedown', handle, { buttons: 0, clientX: 350, clientY: 100 });
  f.emit('mousemove', destination, { buttons: 0, clientX: rect.left + 1, clientY: rect.top + 40 });
  assert.equal(f.editor.dragState().pending.groupId, 'g4');
  f.fireTimer(360); assert.ok(f.preview()); assert.equal(f.editor.dragState().pending.ready, true);
  f.emit('mouseup', destination, { buttons: 0, clientX: rect.left + 1, clientY: rect.top + 40 });
  assert.deepEqual(f.A.get().panels.center.map(g => g.id), ['g3', 'g2', 'g4', 'g5']);
  assert.equal(f.preview(), null); assert.equal(f.editor.dragState().pending, null);
  assert.equal(f.document.querySelector('.nv-f-drag-ghost'), null); assert.equal(f.nativeActions, 0);
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

test('ng-leave DOM never acquires a lease and cancels an editor already entering Angular leave', () => {
  const f = fixture(); f.host.classList.add('ng-leave'); f.editor.begin(); f.scan();
  assert.equal(f.editor.isEditing(), false); assert.deepEqual(f.calls, []);
  f.host.classList.remove('ng-leave'); f.scan(); assert.equal(f.editor.isEditing(), true);
  startDrag(f); f.host.classList.add('ng-leave'); f.scan(); f.editor.begin(); f.scan();
  assert.equal(f.editor.isEditing(), false); assert.equal(f.lease(), false);
  assert.equal(f.document.querySelector('.nv-f-drag-ghost'), null); assert.equal(f.timers.size, 0);
  assert.equal(f.calls.filter(x => x[0] === 'begin').length, 1); assert.equal(f.shellCloses, 0);
});

test('Angular editing false or destroyed scope blocks both direct begin and scan while closing DOM remains', () => {
  for (const closedState of ['editingFalse', 'destroyed']) {
    const f = fixture();
    if (closedState === 'editingFalse') f.scope.flexMenu.editing = false; else f.scope.$$destroyed = true;
    f.editor.begin(); f.scan();
    assert.equal(f.editor.isEditing(), false, closedState); assert.deepEqual(f.calls, [], closedState);
    f.scope.flexMenu.editing = true; f.scope.$$destroyed = false; f.scan(); startDrag(f);
    if (closedState === 'editingFalse') f.scope.flexMenu.editing = false; else f.scope.$$destroyed = true;
    f.scan(); f.editor.begin(); f.scan();
    assert.equal(f.editor.isEditing(), false); assert.equal(f.lease(), false); assert.equal(f.shellCloses, 0);
    assert.equal(f.calls.filter(x => x[0] === 'begin').length, 1);
    assert.equal(f.document.querySelector('.nv-f-drag-ghost'), null); assert.equal(f.timers.size, 0);
  }
});

for (const action of ['Cancel', 'Done']) test(action + ' hides lingering native Settings before shell editing flag and V5 edit class disappear', () => {
  const f = fixture({ lingerOnClose: true }); f.scan();
  const removedClass = [], originalRemove = f.host.classList.remove;
  f.host.classList.remove = name => {
    if (name === 'nv-freestyle-v5-edit') removedClass.push({ visibility: f.host.style.getPropertyValue('visibility'),
      pointer: f.host.style.getPropertyValue('pointer-events'), visibilityPriority: f.host.style.getPropertyPriority('visibility'),
      pointerPriority: f.host.style.getPropertyPriority('pointer-events') });
    originalRemove(name);
  };
  if (action === 'Cancel') f.editor.cancel(true); else f.editor.done();
  assert.equal(f.scope.flexMenu.editing, false); assert.equal(f.shellCloses, 1);
  assert.equal(f.document.querySelector('.nv-live-editor.nv-freestyle-settings'), f.host, 'Angular leave DOM deliberately lingers');
  assert.deepEqual(f.shellCloseObservations, [{ visibility: 'hidden', visibilityPriority: 'important', pointer: 'none', pointerPriority: 'important', wasAngularEditing: true }], 'hidden before Angular shell removes its editing flag');
  assert.deepEqual(removedClass, [{ visibility: 'hidden', pointer: 'none', visibilityPriority: 'important', pointerPriority: 'important' }], 'hidden before V5 edit class removal');
  f.editor.begin(); f.scan(); f.scan();
  assert.equal(f.editor.isEditing(), false); assert.equal(f.calls.filter(x => x[0] === 'begin').length, 1, 'closing DOM cannot reacquire owner');
  assert.equal(f.lease(), false); assert.equal(f.flag(), action === 'Done');
});

test('Escape inside native NVIDIA md-select closes only that popup, not Freestyle', () => {
  const f = fixture(); f.scan();
  const nativePopup = new Element('div', 'md-select-menu-container md-active');
  f.document.body.appendChild(nativePopup);
  const popEscape = f.emit('keydown', f.host, { key: 'Escape' });
  assert.equal(popEscape.prevented, undefined, 'Angular Material receives Escape');
  assert.equal(f.editor.isEditing(), true, 'Freestyle stays in Edit while native popup is open');
  assert.equal(f.shellCloses, 0);
  assert.equal(f.flag(), false, 'No premature Done/persistence');
  nativePopup.remove();
  const editorEscape = f.emit('keydown', f.host, { key: 'Escape' });
  assert.equal(editorEscape.prevented, true, 'Escape cancels Edit only after popup closes');
  assert.equal(f.editor.isEditing(), false);
  assert.equal(f.shellCloses, 1);
});

test('Material Escape race: md-option event stays owned after Angular removed active class', () => {
  const f = fixture(); f.scan();
  const popup = new Element('div', 'md-select-menu-container');
  const option = new Element('md-option');
  popup.appendChild(option); f.document.body.appendChild(popup);
  // Simulate the earlier Material capture listener already closing the popup.
  const event = f.emit('keydown', option, { key: 'Escape' });
  assert.equal(event.prevented, undefined, 'Freestyle does not swallow Material Escape');
  assert.equal(f.editor.isEditing(), true);
  assert.equal(f.shellCloses, 0);
  popup.remove();
  f.emit('keydown', f.host, { key: 'Escape' });
  assert.equal(f.editor.isEditing(), false, 'Second Escape cancels Customize');
});
