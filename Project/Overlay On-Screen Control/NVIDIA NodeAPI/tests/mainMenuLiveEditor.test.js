'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const sourcePath = path.join(__dirname, '../osc/mods/main-menu-live.js');
const storageKey = 'DulukaMainMenuLayoutV2';
const defaults = {
  version: 2,
  left: ['Screenshot', 'Ansel', 'Mods'],
  center: ['InstantReplay', 'ManualRecord', 'Stream', 'Broadcast']
};
const plain = value => JSON.parse(JSON.stringify(value));

function fixture(saved) {
  const values = Object.assign({}, saved);
  const registrations = { controllers: {}, directives: {}, decorators: {}, configs: [], runs: [] };
  const storage = {
    fail: false,
    getItem(key) { return Object.hasOwn(values, key) ? values[key] : null; },
    setItem(key, value) {
      if (this.fail) throw new Error('Storage is full');
      values[key] = String(value);
    },
    removeItem(key) { delete values[key]; }
  };
  const module = {
    config(args) { registrations.configs.push(args); return this; },
    decorator(name, args) { registrations.decorators[name] = args; return this; },
    controller(name, args) { registrations.controllers[name] = args; return this; },
    directive(name, args) { registrations.directives[name] = args; return this; },
    run(args) { registrations.runs.push(args); return this; }
  };
  const window = {
    localStorage: storage,
    angular: {
      module: () => module,
      copy: plain,
      noop() {},
      forEach(value, fn) { Object.keys(value || {}).forEach(key => fn(value[key], key)); }
    },
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent() {}
  };
  vm.runInNewContext(fs.readFileSync(sourcePath, 'utf8'), { window, JSON, console, document: {} }, { filename: sourcePath });
  assert(window.__dulukaMenuLayout, 'The live editor must expose its shared layout model');
  return { api: window.__dulukaMenuLayout, window, storage, values, registrations };
}

function assertUniqueEditable(layout) {
  const keys = layout.left.concat(layout.center);
  assert.equal(new Set(keys).size, keys.length, 'A control cannot occupy two blocks');
  assert(layout.left.length <= 3);
  assert(layout.center.length <= 4);
}

function assertLockedRight(window) {
  assert.deepEqual(plain(window.__dulukaMainMenuLayout.right), ['Performance', 'Mic', 'Camera']);
  assert.equal(window.__dulukaMainMenuLayout.gallery, 'Gallery');
  for (const key of ['Performance', 'Mic', 'Camera', 'Gallery', 'Preferences']) {
    assert.equal(window.__dulukaMainMenuVisibility[key], true, key + ' must stay visible');
  }
}

function editorController(f, cefQuery) {
  f.window.cefQuery = cefQuery;
  const directive = f.registrations.directives.nvMainMenuLiveEditor.at(-1)(f.window, [{}]);
  const events = {};
  const scope = { $evalAsync: fn => fn(), $on: (name, fn) => { events[name] = fn; } };
  const controller = {};
  directive.controller.at(-1).call(controller, scope);
  return { controller, scope, events };
}

test('the editable catalog has seven real controls and the right panel stays locked', () => {
  const f = fixture();
  assert.deepEqual(plain(f.api.get()), defaults);
  assert.deepEqual(plain(f.api.defaults), defaults);
  assert.deepEqual(Array.from(f.api.items, item => item.key).sort(), defaults.left.concat(defaults.center).sort());
  for (const item of f.api.items) {
    assert.equal(typeof item.label, 'string');
    assert(item.label.length);
    assert.equal(typeof item.icon, 'string');
    assert(item.icon.length);
  }
  assertLockedRight(f.window);
});

test('normalization compacts blanks, removes invalid controls and deduplicates across panels', () => {
  const f = fixture();
  const result = f.api.normalize({
    version: 2,
    left: ['', 'Mods', 'Mods', 'Gallery', 'Screenshot', 'Ansel', 'Stream'],
    center: ['Screenshot', null, 'InstantReplay', 'Camera', 'ManualRecord', 'Broadcast', 'Unknown'],
    right: ['Screenshot'],
    gallery: ''
  });
  assert.deepEqual(plain(result), {
    version: 2,
    left: ['Mods', 'Screenshot', 'Ansel'],
    center: ['InstantReplay', 'ManualRecord', 'Broadcast']
  });
  assertUniqueEditable(result);
  assertLockedRight(f.window);
});

test('legacy slot layouts migrate without retaining empty holes or hiding locked controls', () => {
  const f = fixture({
    DulukaMainMenuLayout: JSON.stringify({ left: ['Screenshot', '', 'Mods'], center: ['Broadcast', '', 'Stream', ''], right: ['', '', ''], gallery: '' }),
    DulukaMainMenuVisibility: JSON.stringify({ Mic: false, Camera: false, Gallery: false, Preferences: false })
  });
  assert.deepEqual(plain(f.api.get()), { version: 2, left: ['Screenshot', 'Mods'], center: ['Broadcast', 'Stream'] });
  assert.equal(f.window.__dulukaMainMenuVisibility.Ansel, false);
  assert.equal(f.window.__dulukaMainMenuVisibility.ManualRecord, false);
  assertLockedRight(f.window);
});

test('legacy visibility-only settings migrate while locked right controls are restored', () => {
  const f = fixture({ DulukaMainMenuVisibility: JSON.stringify({ Screenshot: false, Stream: false, Mic: false, Gallery: false }) });
  assert.deepEqual(plain(f.api.get()), { version: 2, left: ['Ansel', 'Mods'], center: ['InstantReplay', 'ManualRecord', 'Broadcast'] });
  assertLockedRight(f.window);
});

test('a V2 layout takes precedence and malformed saved JSON falls back to defaults', () => {
  const current = { version: 2, left: ['Stream'], center: ['Mods'] };
  const f = fixture({ [storageKey]: JSON.stringify(current), DulukaMainMenuLayout: JSON.stringify(defaults) });
  assert.deepEqual(plain(f.api.get()), current);
  assert.deepEqual(plain(fixture({ [storageKey]: '{', DulukaMainMenuLayout: '{', DulukaMainMenuVisibility: '{' }).api.get()), defaults);
});

test('left panel uses fixed Side rows with no phantom height', () => {
  const f = fixture();
  for (let count = 1; count <= 3; count++) {
    const cells = Array.from(f.api.cells({ version: 2, left: defaults.left.slice(0, count), center: [] }));
    assert.equal(cells.length, count);
    cells.forEach((cell, index) => {
      assert.equal(cell.key, defaults.left[index]);
      assert.equal(cell.group, 'left');
      assert.equal(cell.index, index);
      assert.equal(cell.x, -280);
      assert.equal(cell.y, index * 80);
      assert.equal(cell.width, 240);
      assert.equal(cell.height, 80);
      assert.equal(cell.variant, 'row');
    });
    assert.equal(cells.reduce((area, cell) => area + cell.width * cell.height, 0), count * 240 * 80);
  }
});

test('center controls keep fixed 240x240 blocks without stretching as count decreases', () => {
  const f = fixture();
  for (const count of [1, 2, 3, 4]) {
    const cells = Array.from(f.api.cells({ version: 2, left: [], center: defaults.center.slice(0, count) }));
    assert.equal(cells.length, count);
    cells.forEach((cell, index) => {
      assert.equal(cell.group, 'center');
      assert.equal(cell.key, defaults.center[index]);
      assert.equal(cell.index, index);
      assert.equal(cell.variant, 'big');
      assert.equal(cell.x, index * 240);
      assert.equal(cell.y, 0);
      assert.equal(cell.width, 240);
      assert.equal(cell.height, 240);
      if (index) assert.equal(cells[index - 1].x + cells[index - 1].width, cell.x);
    });
    assert.equal(cells.reduce((area, cell) => area + cell.width * cell.height, 0), count * 240 * 240);
  }
  assert.deepEqual(plain(f.api.cells({ version: 2, left: [], center: [] })), []);
});

test('moving a control across full panels swaps the destination back into the source slot', () => {
  const f = fixture();
  assert.equal(f.api.move('Ansel', 'center', 1), true);
  assert.deepEqual(plain(f.api.get()), {
    version: 2,
    left: ['Screenshot', 'ManualRecord', 'Mods'],
    center: ['InstantReplay', 'Ansel', 'Stream', 'Broadcast']
  });
  assertUniqueEditable(f.api.get());
  assertLockedRight(f.window);
});

test('cross-panel moves insert into free space, compact the source and allow any editable control', () => {
  const f = fixture();
  assert.equal(f.api.remove('ManualRecord'), true);
  assert.equal(f.api.move('Mods', 'center', 1), true);
  assert.deepEqual(plain(f.api.get()), {
    version: 2,
    left: ['Screenshot', 'Ansel'],
    center: ['InstantReplay', 'Mods', 'Stream', 'Broadcast']
  });
  assert.equal(f.api.move('Stream', 'left', 0), true);
  assert.deepEqual(plain(f.api.get()), { version: 2, left: ['Stream', 'Screenshot', 'Ansel'], center: ['InstantReplay', 'Mods', 'Broadcast'] });
  assertUniqueEditable(f.api.get());
});

test('reordering within a panel preserves every assigned control', () => {
  const f = fixture();
  assert.equal(f.api.move('Broadcast', 'center', 0), true);
  assert.deepEqual(Array.from(f.api.get().center), ['Broadcast', 'InstantReplay', 'ManualRecord', 'Stream']);
  assert.equal(f.api.move('Screenshot', 'left', 2), true);
  assert.deepEqual(Array.from(f.api.get().left), ['Ansel', 'Mods', 'Screenshot']);
  assertUniqueEditable(f.api.get());
});

test('a removed control can return to either panel but cannot displace a full panel without a source', () => {
  const f = fixture();
  assert.equal(f.api.remove('Mods'), true);
  const before = plain(f.api.get());
  assert.equal(f.api.move('Mods', 'center', 1), false);
  assert.deepEqual(plain(f.api.get()), before);
  assert.equal(f.api.remove('Broadcast'), true);
  assert.equal(f.api.move('Mods', 'center', 1), true);
  assert.deepEqual(Array.from(f.api.get().center), ['InstantReplay', 'Mods', 'ManualRecord', 'Stream']);
});

test('invalid edits and attempts to change locked controls do not mutate the live layout', () => {
  const f = fixture();
  const before = plain(f.api.get());
  const invalid = [
    { version: 2, left: ['Mods', 'Mods'], center: [] },
    { version: 2, left: ['Mods'], center: ['Mods'] },
    { version: 2, left: ['Gallery'], center: [] },
    { version: 2, left: ['Unknown'], center: [] },
    { version: 2, left: defaults.left.concat('Stream'), center: [] },
    { version: 2, left: [], center: defaults.center.concat('Screenshot') },
    { version: 2, left: [], center: [], right: [] },
    { version: 2, left: [], center: [], right: null },
    { version: 2, left: [], center: [], right: false },
    { version: 2, left: [], center: [], gallery: null },
    { version: 2, left: [], center: [], gallery: false },
    { version: 2, left: [], center: [], gallery: '' }
  ];
  for (const layout of invalid) {
    assert.equal(f.api.set(layout), false, JSON.stringify(layout));
    assert.deepEqual(plain(f.api.get()), before);
  }
  for (const key of ['Mic', 'Camera', 'Gallery', 'Performance']) {
    assert.equal(f.api.move(key, 'left', 0), false);
  }
  for (const key of ['Preferences', 'Settings']) {
    assert.equal(f.api.remove(key), false);
    assert.equal(f.api.move(key, 'left', 0), false);
  }
  assert.equal(f.api.move('Mods', 'right', 0), false);
  assert.equal(f.api.move('Mods', 'unknown', 0), false);
  assert.deepEqual(plain(f.api.get()), before);
  assertLockedRight(f.window);
});

test('canonical V2 saves survive reload and reset restores every NVIDIA default', () => {
  const f = fixture();
  const layout = { version: 2, left: ['Broadcast'], center: ['Screenshot', 'Ansel', 'Stream'] };
  assert.equal(f.api.set(layout), true);
  assert.deepEqual(JSON.parse(f.values[storageKey]), layout);
  assert.equal(f.window.__dulukaMainMenuVisibility.Broadcast, true);
  assert.equal(f.window.__dulukaMainMenuVisibility.Mods, false);
  const reopened = fixture(f.values);
  assert.deepEqual(plain(reopened.api.get()), layout);
  assert.equal(reopened.api.reset(), true);
  assert.deepEqual(plain(reopened.api.get()), defaults);
  assert.deepEqual(JSON.parse(reopened.values[storageKey]), defaults);
  assertLockedRight(reopened.window);
});

test('both editable panels may be empty without hiding or modifying the fixed right panel', () => {
  const f = fixture();
  assert.equal(f.api.set({ version: 2, left: [], center: [], right: ['Performance', 'Mic', 'Camera'], gallery: 'Gallery' }), true);
  assert.deepEqual(plain(f.api.get()), { version: 2, left: [], center: [] });
  assert.deepEqual(JSON.parse(f.values[storageKey]), { version: 2, left: [], center: [] });
  for (const key of defaults.left.concat(defaults.center)) assert.equal(f.window.__dulukaMainMenuVisibility[key], false);
  assertLockedRight(f.window);
  assert.deepEqual(plain(fixture(f.values).api.get()), { version: 2, left: [], center: [] });
});

test('a storage failure leaves the live layout, visibility, persistence and subscribers unchanged', () => {
  const f = fixture();
  const before = plain(f.api.get());
  const visibility = plain(f.window.__dulukaMainMenuVisibility);
  const saved = f.values[storageKey];
  let updates = 0;
  f.api.subscribe(() => { updates++; });
  const beforeUpdates = updates;
  f.storage.fail = true;
  assert.equal(f.api.set({ version: 2, left: ['Mods'], center: [] }), false);
  assert.deepEqual(plain(f.api.get()), before);
  assert.deepEqual(plain(f.window.__dulukaMainMenuVisibility), visibility);
  assert.equal(f.values[storageKey], saved);
  assert.equal(updates, beforeUpdates);
  assert.equal(typeof f.api.error, 'string');
  assert(f.api.error.length, 'The editor must show that the change was not saved');
});

test('subscribers receive only committed changes and can unsubscribe', () => {
  const f = fixture();
  const observed = [];
  const unsubscribe = f.api.subscribe(layout => observed.push(plain(layout)));
  const initialCount = observed.length;
  assert.equal(f.api.remove('Mods'), true);
  assert.equal(observed.length, initialCount + 1);
  assert.deepEqual(observed.at(-1), plain(f.api.get()));
  const committedCount = observed.length;
  assert.equal(f.api.move('Gallery', 'left', 0), false);
  assert.equal(observed.length, committedCount);
  unsubscribe();
  assert.equal(f.api.reset(), true);
  assert.equal(observed.length, committedCount);
});

test('callers cannot change saved state by mutating a returned snapshot', () => {
  const f = fixture();
  const layout = f.api.get();
  layout.left[0] = 'Broadcast';
  layout.center.length = 0;
  assert.deepEqual(plain(f.api.get()), defaults);
  assertLockedRight(f.window);
});

test('the live editor retains the real desktop display selector and saves its selected device', () => {
  const f = fixture(), requests = [];
  const { controller } = editorController(f, query => {
    const request = JSON.parse(query.request);
    requests.push(request);
    query.onSuccess(JSON.stringify(request.command === 'QUERY_OSC_DESKTOP_MONITORS' ? {
      selected: '', displays: [{ device: '\\\\.\\DISPLAY25', name: 'Monitor', width: 1280, height: 800, primary: false }]
    } : { saved: true }));
  });
  controller.loadDesktopMonitors();
  assert.equal(controller.desktopDisplays[0].label, 'Display 25 — Monitor (1280 × 800)');
  controller.desktopMonitor = '\\\\.\\DISPLAY25';
  controller.saveDesktopMonitor();
  assert.equal(requests.at(-1).command, 'QUERY_OSC_SET_DESKTOP_MONITOR');
  assert.equal(requests.at(-1).device, '\\\\.\\DISPLAY25');
  assert.equal(controller.desktopBusy, false);
  assert.equal(controller.desktopDisconnected, false);
  assert.equal(controller.error, '');
});

test('failed desktop display changes restore the saved device and release the live selector', () => {
  const f = fixture();
  const { controller } = editorController(f, query => {
    const request = JSON.parse(query.request);
    query.onSuccess(JSON.stringify(request.command === 'QUERY_OSC_DESKTOP_MONITORS' ? { selected: 'DISPLAY1', displays: [] } : { saved: false }));
  });
  controller.loadDesktopMonitors();
  assert.equal(controller.desktopDisconnected, true);
  controller.desktopMonitor = 'DISPLAY26';
  controller.saveDesktopMonitor();
  assert.equal(controller.desktopMonitor, 'DISPLAY1');
  assert.equal(controller.desktopBusy, false);
  assert.match(controller.error, /Could not change display/);
});

test('destroyed live editor scopes ignore late monitor replies and release layout subscriptions', () => {
  const f = fixture();
  let pending;
  const { controller, scope, events } = editorController(f, query => { pending = query; });
  controller.loadDesktopMonitors();
  assert.equal(controller.desktopBusy, true);
  scope.$$destroyed = true;
  events.$destroy();
  pending.onSuccess(JSON.stringify({ selected: 'DISPLAY1', displays: [] }));
  assert.equal(controller.desktopMonitor, '');
  const oldLayout = plain(controller.layout);
  f.api.remove('Mods');
  assert.deepEqual(plain(controller.layout), oldLayout);
});

test('Settings live editor keeps saved panels after Done, teardown and a cold initialization', () => {
  const first = fixture();
  const navigations = [];
  first.window.angular.element = () => ({
    injector: () => ({ get: () => ({ go: route => navigations.push(route) }) })
  });
  first.window.setTimeout = () => 1;

  first.window.__dulukaStartMainMenuEditor();
  assert.deepEqual(navigations, ['main.main-menu']);
  const respond = query => {
    const request = JSON.parse(query.request);
    assert.equal(request.command, 'QUERY_OSC_DESKTOP_MONITORS');
    query.onSuccess(JSON.stringify({ selected: '', displays: [] }));
  };
  const opened = editorController(first, respond);
  assert.equal(opened.controller.editing, true, 'the Settings entry should open live editing');
  assert.equal(first.api.move('Mods', 'center', 0), true);
  assert.equal(first.api.remove('Broadcast'), true);
  const saved = plain(first.api.get());
  assert.deepEqual(JSON.parse(first.values[storageKey]), saved);
  opened.controller.done();
  assert.equal(opened.controller.editing, false);
  opened.events.$destroy();

  // A fresh VM recreates the app module and its in-memory layout.
  // Only the persisted localStorage snapshot carries over.
  const restarted = fixture(first.values);
  assert.deepEqual(plain(restarted.api.get()), saved);
  assertLockedRight(restarted.window);
  restarted.window.angular.element = () => ({
    injector: () => ({ get: () => ({ go: route => navigations.push(route) }) })
  });
  restarted.window.setTimeout = () => 1;
  restarted.window.__dulukaStartMainMenuEditor();
  const reopened = editorController(restarted, respond);
  assert.equal(reopened.controller.editing, true);
  assert.deepEqual(plain(reopened.controller.layout), saved);
  assert.deepEqual(navigations, ['main.main-menu', 'main.main-menu']);
  reopened.events.$destroy();
});

test('Side rows support 1 named control or groups of 2–3 icons while preserving Settings', () => {
  const f=fixture();
  assert.deepEqual(plain(f.api.getSides().right),[['Performance','Mic','Camera'],['Gallery'],['Settings']]);
  assert.equal(f.api.sideRemove('Mic'),true);
  assert.equal(f.api.sideMove('Settings','right',0,true),true);
  assert.deepEqual(plain(f.api.getSides().right[0]),['Performance','Camera','Settings']);
  assert.equal(f.api.sideMove('Settings','right',0,true),true);
  assert.equal(f.api.sideMove('Camera','right',1,true),true);
  assert.deepEqual(plain(f.api.getSides().right),[['Performance','Settings'],['Gallery','Camera']]);
  assert.equal(f.api.sideRemove('Settings'),false);
  assert.equal(f.api.remove('Settings'),false);
  assert.equal(f.api.move('Settings','center',0),false);
  assert.equal(f.api.sideMove('Settings','left',0,true),false);
  assert.equal(f.api.sideMove('Mods','right',0,true),true);
  assert.deepEqual(plain(f.api.getSides().right[0]),['Performance','Settings','Mods']);
  assert.equal(f.api.get().left.includes('Mods'),false);
  const cold=fixture(f.values);
  assert.deepEqual(plain(cold.api.getSides()),plain(f.api.getSides()));
  assert.equal(cold.window.__dulukaMainMenuVisibility.Preferences,true);
});

test('empty left and center panels retain just the mandatory Settings Side block', () => {
  const f=fixture();
  for(const key of ['Performance','Mic','Camera','Gallery'])assert.equal(f.api.sideRemove(key),true);
  assert.equal(f.api.set({version:2,left:[],center:[]}),true);
  assert.deepEqual(plain(f.api.getSides()),{left:[],right:[['Settings']]});
  assert.equal(f.window.__dulukaMainMenuVisibility.Preferences,true);
  for(const key of ['Performance','Mic','Camera','Gallery'])assert.equal(f.window.__dulukaMainMenuVisibility[key],false);
  const cold=fixture(f.values);
  assert.deepEqual(plain(cold.api.getSides()),{left:[],right:[['Settings']]});
  assert.equal(cold.api.reset(),true);
  assert.deepEqual(plain(cold.api.getSides().right),[['Performance','Mic','Camera'],['Gallery'],['Settings']]);
});

test('the native editor includes all three panels and a searchable scrollable control catalog',()=>{
  const html=fs.readFileSync(sourcePath,'utf8');
  assert(html.includes("sidePanelMarkup('right')"));
  assert(html.includes("sidePanelMarkup('left')"));
  assert(html.includes('nv-live-search'));
  assert(html.includes('filter:{label:flexMenu.search}'));
  assert(html.includes("key!==\\'Settings\\'")||html.includes("key!=='Settings'"));
});

test('Side Slot icons can be reordered within the same row without duplicating Settings',()=>{
  const f=fixture();
  assert.equal(f.api.sideMove('Mic','right',0,true,0),true);
  assert.deepEqual(plain(f.api.getSides().right[0]),['Mic','Performance','Camera']);
  assert.equal(f.api.sideMove('Settings','right',1,true,0),true);
  assert.deepEqual(plain(f.api.getSides().right[1]),['Settings','Gallery']);
  assert.equal(f.api.sideMove('Gallery','right',1,true,0),true);
  assert.deepEqual(plain(f.api.getSides().right[1]),['Gallery','Settings']);
  assert.equal(plain(f.api.getSides().right).flat().filter(k=>k==='Settings').length,1);
});

test('extra right Side rows move the Customize toolbar below the taller native panel',()=>{
  const f=fixture(),{controller}=editorController(f,()=>{});
  assert.equal(controller.extraRows(),0);
  assert.equal(f.api.sideMove('Mods','right',3,false),true);
  assert.equal(f.api.getSides().right.length,4);
  assert.equal(controller.extraRows(),80);
  assert.equal(f.api.sideMove('Ansel','right',4,false),true);
  assert.equal(controller.extraRows(),160);
  assert.equal(f.api.reset(),true);
  assert.equal(controller.extraRows(),0);
});

test('actual Side Slot model reports two and three icon-only rectangles',()=>{
  const f=fixture();
  assert.equal(f.api.sideMove('Ansel','left',0,true),true);
  let c=Array.from(f.api.cells(f.api.get())).filter(x=>x.group==='left');
  assert.deepEqual(c.map(x=>[x.key,x.x,x.y,x.width,x.height,x.variant]),[
    ['Screenshot',-280,0,120,80,'small'],['Ansel',-160,0,120,80,'small'],['Mods',-280,80,240,80,'row']
  ]);
  assert.equal(f.api.sideMove('Mods','left',0,true),true);
  c=Array.from(f.api.cells(f.api.get())).filter(x=>x.group==='left');
  assert.deepEqual(c.map(x=>[x.x,x.width,x.height,x.variant]),[[-280,80,80,'small'],[-200,80,80,'small'],[-120,80,80,'small']]);
});
