'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const M = require('../osc/Module/MainMenu/OSCFreestyleV5/nv-layout-v5-core.js');
const modFiles = {
  'main-menu-live.js': 'Module/MainMenu/NVIDIA/main-menu-live.js',
  'gpu-profiles.js': 'Module/Settings/GPUProfiles/gpu-profiles.js',
  'nv-next-v5-native.js': 'Module/MainMenu/OSCFreestyleV5/nv-next-v5-native.js'
};
const source = name => fs.readFileSync(path.join(__dirname, '../osc', modFiles[name]), 'utf8');
const names = {
  Screenshot: 'screenshot-tile', Mods: 'mods-tile', ManualRecord: 'manual-record-tile',
  Stream: 'stream-tile', InstantReplay: 'instant-replay-tile', Ansel: 'ansel-tile',
  Broadcast: 'broadcast-tile', Performance: 'performance-tile', Mic: 'mic-tile',
  Camera: 'camera-tile', Gallery: 'gallery-tile', Settings: 'nv-settings-menu-tile', GameProfiles: 'nv-game-profiles-tile'
};
const plain = value => JSON.parse(JSON.stringify(value));

function nativeNode() {
  const properties = new Map(), priorities = new Map(), classes = new Set(), attributes = new Map();
  const node = { writes: 0 };
  node.style = {
    setProperty(key, value, priority = '') {
      properties.set(key, String(value)); priorities.set(key, priority); node.writes++;
    },
    removeProperty(key) {
      const previous = properties.get(key) || '';
      properties.delete(key); priorities.delete(key); node.writes++;
      return previous;
    },
    getPropertyValue: key => properties.get(key) || '',
    getPropertyPriority: key => priorities.get(key) || ''
  };
  Object.defineProperty(node.style, 'display', {
    get: () => node.style.getPropertyValue('display'),
    set: value => node.style.setProperty('display', value)
  });
  node.classList = {
    add: (...keys) => keys.forEach(key => classes.add(key)),
    remove: (...keys) => keys.forEach(key => classes.delete(key)),
    contains: key => classes.has(key),
    toggle(key, enabled) {
      const on = enabled === undefined ? !classes.has(key) : enabled;
      if (on) classes.add(key); else classes.delete(key);
      return on;
    }
  };
  node.getAttribute = key => attributes.has(key) ? attributes.get(key) : null;
  node.setAttribute = (key, value) => attributes.set(key, String(value));
  node.removeAttribute = key => attributes.delete(key);
  node.querySelector = () => null;
  node.snapshot = () => ({
    styles: Array.from(properties, ([key, value]) => [key, value, priorities.get(key)]).sort(),
    classes: Array.from(classes).sort(), attributes: Array.from(attributes).sort()
  });
  return node;
}

function gpuFixture() {
  // Load the real V2 publisher from persisted Settings-only user data. This
  // reproduces ng-if unlinking all eleven other tiles before V5 takes over.
  const values = new Map([
    ['DulukaMainMenuLayoutV2', JSON.stringify({ version: 2, left: [], center: [] })],
    ['DulukaMainMenuSideRowsV1', JSON.stringify({ left: [], right: [['Settings']] })]
  ]);
  const originalStorage = Array.from(values), styles = {}, tiles = {}, nodes = {};
  const bar = nativeNode(), frames = new Map(), cancelledFrames = [];
  const root = {
    getBoundingClientRect: () => ({ left: 440, width: 1040, height: 350 }),
    querySelector(selector) {
      if (selector === 'md-menu-bar') return bar;
      const key = M.CONTROLS.find(key => selector === 'md-menu.' + names[key]);
      return key ? nodes[key] : null;
    }
  };
  M.CONTROLS.forEach(key => {
    nodes[key] = nativeNode();
    const tileKey = key === 'Settings' ? 'Preferences' : key;
    tiles[tileKey] = { name: names[key], visible: false, enabled: true, state: {} };
  });
  const scope = { mainMenu: { tiles }, digests: 0, $applyAsync() { this.digests++; } };
  const document = {
    documentElement: {}, querySelector: () => root,
    getElementById: key => styles[key] || null,
    createElement: () => ({ textContent: '', parentNode: null }),
    head: { appendChild(node) { styles[node.id] = node; node.parentNode = this; } }
  };
  const angularModule = {
    config() { return this; }, directive() { return this; }, controller() { return this; }, run() { return this; }
  };
  let nextFrame = 0, v5Owner = true, v4Owner = false, v5Syncs = 0, v4Syncs = 0;
  const window = {
    innerWidth: 1920,
    localStorage: {
      getItem: key => values.get(key) || null,
      setItem: (key, value) => values.set(key, String(value)),
      removeItem: key => values.delete(key)
    },
    angular: {
      module: () => angularModule, copy: plain, noop() {},
      element: element => element === root ? { scope: () => scope } : {
        injector: () => ({ get: () => ({ go() {} }) })
      }
    },
    requestAnimationFrame(callback) { const id = ++nextFrame; frames.set(id, callback); return id; },
    cancelAnimationFrame(id) { cancelledFrames.push(id); frames.delete(id); },
    __nvNextLayoutV5: { enabled: () => v5Owner, model: M, sync() { v5Syncs++; } },
    __dulukaUnifiedMainMenu: {
      enabled: () => v4Owner, nativeGeometry() { v4Syncs++; },
      visible: () => ({ Settings: true }),
      model: { CONTROLS: M.CONTROLS.map(key => ({ key })) }
    }
  };
  vm.runInNewContext(source('main-menu-live.js'), { window, document, JSON, console });
  const context = {
    window, document, mainMenuTileStyleId: 'ownership-test-style', mainMenuTileMarker: 'ownership-test',
    performanceSupportAvailable: null, liveLayoutUnsubscribe: null, openGameProfilesFromMainMenu() {}
  };
  const gpu = source('gpu-profiles.js');
  const start = gpu.indexOf('  function ensureMainMenuTileStyle()');
  const end = gpu.indexOf('  function registerPerformanceSupportListener()', start);
  assert.ok(start >= 0 && end > start, 'native menu integration functions must be found');
  vm.createContext(context);
  vm.runInContext(gpu.slice(start, end), context, { filename: 'gpu-profiles-native-menu.js' });
  return {
    window, scope, tiles, nodes, bar, values, originalStorage, frames, cancelledFrames,
    synchronize: () => vm.runInContext('updateMainMenuPerformance()', context),
    owner(v5, v4 = false) { v5Owner = v5; v4Owner = v4; },
    counts: () => ({ v5: v5Syncs, v4: v4Syncs }),
    nativeWrites: () => bar.writes + Object.values(nodes).reduce((sum, node) => sum + node.writes, 0)
  };
}

test('V5 revives every Angular native tile despite persisted V2 hiding, and exclusively owns geometry', () => {
  const f = gpuFixture();
  for (const key of M.CONTROLS.filter(key => key !== 'Settings' && key !== 'GameProfiles'))
    assert.equal(f.window.__dulukaMainMenuVisibility[key], false, key + ' starts hidden by saved V2');
  f.synchronize();
  assert.deepEqual(f.counts(), { v5: 1, v4: 0 });
  for (const key of M.CONTROLS)
    assert.equal(f.tiles[key === 'Settings' ? 'Preferences' : key].visible, true, key + ' must remain Angular-linked');
  assert.equal(f.nativeWrites(), 0, 'gpu integration must not write V2 geometry while V5 owns it');
  assert.ok(f.scope.digests > 0, 'Angular receives the visibility change');
  assert.deepEqual(Array.from(f.values), f.originalStorage, 'handover does not migrate or overwrite saved V2 data');
  f.synchronize();
  assert.deepEqual(f.counts(), { v5: 2, v4: 0 });
  assert.equal(f.nativeWrites(), 0);
});

test('disabling V5 returns native geometry and persisted visibility to the legacy owner', () => {
  const f = gpuFixture();
  f.synchronize();
  f.owner(false);
  f.synchronize();
  assert.deepEqual(f.counts(), { v5: 1, v4: 0 });
  assert.equal(f.bar.style.getPropertyValue('width'), '0px', 'legacy Settings-only geometry is restored');
  assert.ok(f.nativeWrites() > 0, 'disabled V5 falls through to executable V2 geometry');
  for (const key of M.CONTROLS.filter(key => key !== 'Settings' && key !== 'GameProfiles'))
    assert.equal(f.tiles[key].visible, false, key + ' follows its original V2 saved visibility');
  assert.equal(f.tiles.Preferences.visible, true);
  assert.equal(f.tiles.GameProfiles.visible, false, 'the catalog-only control is hidden after releasing V5');
  assert.deepEqual(Array.from(f.values), f.originalStorage);
});

test('taking V5 ownership cancels an already scheduled legacy layout pass', () => {
  const f = gpuFixture();
  f.owner(false, true);
  f.synchronize();
  assert.deepEqual(f.counts(), { v5: 0, v4: 1 });
  assert.equal(f.frames.size, 1);
  const queuedFrame = Array.from(f.frames.keys())[0];
  f.owner(true, true);
  f.synchronize();
  assert.deepEqual(f.counts(), { v5: 1, v4: 1 });
  assert.deepEqual(f.cancelledFrames, [queuedFrame]);
  assert.equal(f.frames.size, 0, 'a stale V4 frame cannot overwrite V5 positions');
  assert.equal(f.nativeWrites(), 0);
});

function adapterFixture() {
  const storage = new Map(), bar = nativeNode(), tiles = {}, infos = {};
  bar.style.setProperty('width', '960px');
  bar.style.setProperty('position', 'absolute', 'important');
  bar.style.setProperty('transform', 'translateX(-20px)');
  bar.classList.add('nv-unified-bar', 'native-bar');
  bar.parentElement = { getBoundingClientRect: () => ({ left: 480, width: 1040, height: 240 }) };
  bar.offsetLeft = 0;
  M.CONTROLS.forEach((key, index) => {
    const tile = tiles[key] = nativeNode(), info = infos[key] = nativeNode();
    tile.style.setProperty('left', (index + 7) + 'px');
    tile.style.setProperty('top', '18px', 'important');
    tile.style.setProperty('display', index % 2 ? 'flex' : 'none', index % 2 ? '' : 'important');
    tile.style.setProperty('color', 'rgb(12, 34, 56)', 'important');
    tile.classList.add('native-custom', 'nv-side-full-tile', 'nv-flex-tile');
    if (index % 2) tile.classList.add('nv-unified-big');
    if (index % 2) tile.setAttribute('data-nv-next-control', 'before-' + key);
    else tile.setAttribute('aria-hidden', 'true');
    tile.setAttribute('data-native-preserved', key);
    info.style.setProperty('display', 'inline-flex', 'important');
    tile.querySelector = selector => selector === '.nv-side-native-info' ? info : null;
  });
  const root = {
    getBoundingClientRect: () => ({ left: 440, top: 120, width: 1040, height: 350 }),
    querySelector(selector) {
      if (selector === 'md-menu-bar') return bar;
      const key = M.CONTROLS.find(key => selector === 'md-menu.' + names[key]);
      return key ? tiles[key] : null;
    }
  };
  const document = {
    documentElement: nativeNode(),
    querySelector: selector => selector === 'nv-main-menu:not([data-customize-preview]) .main-menu-container' ? root : null
  };
  const window = {
    NvShadowPlayLayoutV5: M,
    NvUnifiedLayoutV4: { CONTROLS: M.CONTROLS.map(key => ({ key, name: names[key] })) },
    innerWidth: 1920, innerHeight: 1200,
    localStorage: {
      getItem: key => storage.get(key) || null,
      setItem: (key, value) => storage.set(key, String(value)),
      removeItem: key => storage.delete(key)
    }
  };
  const snapshot = () => ({
    bar: bar.snapshot(),
    tiles: M.CONTROLS.map(key => [key, tiles[key].snapshot(), infos[key].snapshot()])
  });
  vm.runInNewContext(source('nv-next-v5-native.js'), { window, document }, { filename: 'nv-next-v5-native.js' });
  return { api: window.__nvNextLayoutV5, document, tiles, bar, storage, snapshot };
}

test('Cancel releases a temporary native lease and exactly restores classes, attributes and CSS priorities', () => {
  const f = adapterFixture(), before = f.snapshot();
  f.api.editOwnership(true);
  f.api.beginEdit();
  f.api.edit(s => M.autoFill(s, false));
  assert.equal(f.api.sync(true).cells.length, 12);
  assert.equal(f.tiles.Performance.classList.contains('nv-side-full-tile'), false);
  assert.equal(f.tiles.Performance.style.getPropertyValue('left'), '1256px');
  assert.notDeepEqual(f.snapshot(), before);
  f.api.edit(s => M.takeControl(s, 'Ansel'));
  f.api.sync(true);
  assert.equal(f.tiles.Ansel.getAttribute('aria-hidden'), 'true');
  f.api.cancel();
  f.api.editOwnership(false);
  assert.equal(f.api.enabled(), false);
  assert.deepEqual(f.snapshot(), before, 'restore must use the first pre-edit snapshot, not a later V5 frame');
  assert.equal(f.document.documentElement.classList.contains('nv-next-v5-on'), false);
  assert.equal(f.storage.size, 0, 'Cancel never saves flag or layout');
});

test('disabling a committed owner removes V5 additions while retaining preexisting native classes and priorities', () => {
  const f = adapterFixture(), before = f.snapshot();
  f.api.enable(true);
  assert.ok(f.api.sync(true));
  assert.equal(f.bar.style.getPropertyPriority('width'), 'important');
  assert.equal(f.tiles.Mic.classList.contains('nv-unified-big'), false);
  f.api.enable(false);
  assert.deepEqual(f.snapshot(), before);
  assert.equal(f.storage.size, 0);
});

test('thirteen linked controls retain twelve default cells and Game Profiles receives geometry only after assignment', () => {
  const f = adapterFixture(), before = f.snapshot(), tile = f.tiles.GameProfiles;
  assert.equal(Object.keys(f.tiles).length, 13);
  f.api.editOwnership(true); f.api.beginEdit();
  const initial = f.api.sync(true);
  assert.equal(initial.cells.length, 12); assert.equal(initial.boxes.length, 6);
  assert.ok(M.available(f.api.get()).includes('GameProfiles'));
  assert.equal(tile.style.getPropertyValue('display'), 'none'); assert.equal(tile.getAttribute('aria-hidden'), 'true');
  f.api.edit(s => M.changeBlock(s, 'g2', 0, 'small', 'triple'));
  f.api.edit(s => M.placeControl(s, 'GameProfiles', 'g2', 0, 1));
  const assigned = f.api.sync(true);
  assert.equal(assigned.cells.length, 13); assert.equal(assigned.cells.filter(cell => cell.control === 'GameProfiles').length, 1);
  assert.equal(f.tiles.GameProfiles, tile, 'the real native node is positioned without replacement');
  assert.equal(tile.style.getPropertyValue('display'), 'block'); assert.equal(tile.getAttribute('data-nv-next-control'), 'GameProfiles');
  assert.equal(tile.getAttribute('aria-hidden'), null); assert.equal(M.findControl(f.api.get(), 'GameProfiles').groupId, 'g2');
  assert.equal(f.storage.size, 0, 'assignment stays transient until Done');
  f.api.cancel(); f.api.editOwnership(false);
  assert.deepEqual(f.snapshot(), before, 'Cancel restores native attributes/classes/styles including the catalog tile');
  assert.equal(f.storage.size, 0);
});
