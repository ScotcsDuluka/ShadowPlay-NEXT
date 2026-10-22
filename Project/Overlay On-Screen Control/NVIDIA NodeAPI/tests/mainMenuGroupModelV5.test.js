'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const G = require('../osc/mods/main-menu-group-model');
const V4 = require('../osc/mods/main-menu-unified-model');

const initial = () => G.fromV4(V4.initial());
const groups = s => G.PANELS.flatMap(p => s.panels[p].map(g => ({ panel:p, group:g })));

test('V5 keeps every native V4 Control exactly once and never loses Settings', () => {
  const old = V4.initial(), copy = JSON.stringify(old);
  const s = G.fromV4(old);
  assert.equal(JSON.stringify(old), copy);
  assert.equal(G.validate(s), true);
  assert.equal(new Set(G.slots(s)).size, G.slots(s).length);
  assert.deepEqual(new Set(G.slots(s)), new Set(V4.CONTROLS.map(c => c.key)));
  assert(G.find(s, 'Settings'));
});

test('Side detail presets use 180/60px, matching the agreed native 240px Group', () => {
  assert.deepEqual(G.PRESETS.side.detailLeft, [3, 1]);
  assert.deepEqual(G.PRESETS.side.detailRight, [1, 3]);
  let s = G.createGroup(initial(), 'center', 'side', 'detailLeft');
  let n = s.panels.center.length - 1;
  s = G.setSlot(s, 'Screenshot', 'center', n, 0, 0);
  s = G.setSlot(s, 'Ansel', 'center', n, 0, 1);
  const cells = G.geometry(s, 1920, 1080).cells.filter(c => c.group === s.panels.center[n].id);
  assert.deepEqual(cells.map(c => c.width), [180, 60]);
});

test('moving a Group inside its own Panel without index appends to the end', () => {
  let s = initial();
  s = G.createGroup(s, 'left', 'small', 'triple');
  s = G.createGroup(s, 'left', 'small', 'wideRight');
  const before = JSON.stringify(s), first = s.panels.left[0], rest = s.panels.left.slice(1);
  const moved = G.moveGroup(s, 'left', 0, 'left');
  assert.deepEqual(moved.panels.left, [...rest, first]);
  assert.equal(JSON.stringify(s), before);
});

test('explicit in-Panel insertion index is interpreted before removing the source', () => {
  let s = initial();
  s = G.createGroup(s, 'left', 'small', 'triple');
  s = G.createGroup(s, 'left', 'small', 'wideRight');
  const before = s.panels.left.map(g => g.id);
  const moved = G.moveGroup(s, 'left', 0, 'left', 2).panels.left.map(g => g.id);
  assert.deepEqual(moved, [before[1], before[0], ...before.slice(2)]);
});

test('moving Group between Panels keeps Controls and Group IDs unique', () => {
  const s = initial(), first = s.panels.left[0], old = JSON.stringify(s);
  const moved = G.moveGroup(s, 'left', 0, 'right');
  assert.equal(moved.panels.right.at(-1).id, first.id);
  assert.deepEqual(G.slots(moved).sort(), G.slots(s).sort());
  assert.equal(new Set(groups(moved).map(x => x.group.id)).size, groups(moved).length);
  assert.equal(JSON.stringify(s), old);
});

test('Settings cannot be deleted or cleared even after Group changes', () => {
  const s = initial(), location = G.find(s, 'Settings');
  assert.throws(() => G.emptySlot(s, location.panel, location.group, location.block, location.slot), /Settings cannot/);
  assert.equal(G.validate(s), true);
});
