'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const M = require('../osc/mods/nv-layout-v5-core.js');

function random(seed) {
  let x = seed >>> 0;
  return function next() {
    x ^= x << 13; x ^= x >>> 17; x ^= x << 5;
    return (x >>> 0) / 4294967296;
  };
}
const panels = ['left', 'center', 'right'];
const groups = s => panels.flatMap(p => s.panels[p].map(g => g));
const occupied = s => panels.flatMap(p => s.panels[p].flatMap(g =>
  g.blocks.flatMap(b => b.slots.filter(Boolean))));

function invariants(s, width) {
  assert.equal(M.validate(s), true);
  assert.equal(M.validate(JSON.parse(JSON.stringify(s))), true, 'Persisted model validates');
  const edit = M.geometry(s, 'edit', width);
  const normal = M.geometry(s, 'normal', width);
  assert.equal(edit.boxes.length, M.count(s));
  assert.ok(edit.boxes.length <= 6, 'Global Group limit');
  assert.equal(new Set(edit.boxes.map(g => g.id)).size, edit.boxes.length);
  assert.equal(normal.boxes.length, groups(s).filter(g => g.blocks.length).length);
  assert.equal(edit.height, 240);
  const placed = occupied(s);
  assert.equal(placed.length, new Set(placed).size, 'No duplicated Control');
  assert.equal(placed.filter(key => key === 'Settings').length, 1, 'Settings cannot disappear');
  const palette = M.available(s);
  assert.equal(palette.length, new Set(palette).size);
  assert.equal(placed.length + palette.length, M.CONTROLS.length);
  assert.deepEqual(new Set(placed.concat(palette)), new Set(M.CONTROLS));
  assert.equal(edit.cells.length, placed.length);
  for (const box of edit.boxes) {
    assert.equal(box.width, 240); assert.equal(box.height, 240);
    assert.ok(Number.isFinite(box.x));
  }
  for (const cell of edit.slots) {
    assert.ok([cell.x, cell.y, cell.width, cell.height].every(Number.isFinite));
    assert.ok(cell.x >= 0 && cell.y >= 0 && cell.width > 0 && cell.height > 0);
    assert.ok(cell.x + cell.width <= edit.width + 1e-6);
    assert.ok(cell.y + cell.height <= 240 + 1e-6);
  }
}

function attempt(s, rand) {
  const any = list => list[Math.floor(rand() * list.length)];
  const group = any(groups(s));
  const groupId = () => any(groups(s)).id;
  const action = any(['move', 'swap', 'addGroup', 'deleteGroup', 'addBlock',
    'removeBlock', 'changeBlock', 'place', 'remove', 'fill']);
  switch (action) {
    case 'move': {
      const panel = any(panels);
      return M.moveGroup(s, groupId(), panel, Math.floor(rand() * (s.panels[panel].length + 1)));
    }
    case 'swap': return M.swapGroups(s, groupId(), groupId());
    case 'addGroup': return M.addGroup(s, any(panels));
    case 'deleteGroup': return M.deleteGroup(s, groupId());
    case 'addBlock': {
      const type = any(['small', 'side', 'big']);
      return M.addBlock(s, groupId(), type, any(Object.keys(M.PRESETS[type])),
        undefined, rand() < 0.5);
    }
    case 'removeBlock': return M.removeBlock(s, group.id, Math.floor(rand() * 4));
    case 'changeBlock': {
      const type = any(['small', 'side', 'big']);
      return M.changeBlock(s, group.id, Math.floor(rand() * 4),
        type, any(Object.keys(M.PRESETS[type])));
    }
    case 'place': {
      const target = any(groups(s));
      if (!target.blocks.length) throw Error('No target Block');
      const index = Math.floor(rand() * target.blocks.length);
      return M.placeControl(s, any(M.CONTROLS), target.id, index,
        Math.floor(rand() * target.blocks[index].slots.length));
    }
    case 'remove': return M.takeControl(s, any(M.CONTROLS));
    case 'fill': return M.autoFill(s, rand() < 0.5);
    default: throw Error('Unexpected random action');
  }
}

for (let seed = 1; seed <= 20; seed++) {
  test('V5 deterministic randomized state integrity, seed ' + seed, () => {
    const rand = random(seed * 7919);
    let state = M.defaultLayout(), accepted = 0, rejected = 0;
    // Half the runs begin with the exact old catalog metadata. Reading that
    // state must include newly optional Controls without migrating the input.
    if (seed % 2 === 0) state.controlOrder = M.CONTROLS.filter(key => key !== 'GameProfiles');
    const initial = JSON.stringify(state);
    invariants(state, 1920);
    assert.equal(JSON.stringify(state), initial, 'Legacy catalog reads must not mutate saved state');
    assert.deepEqual(M.available(state), ['GameProfiles']);
    for (let step = 0; step < 550; step++) {
      const before = JSON.stringify(state);
      let next;
      try {
        next = attempt(state, rand);
      } catch (error) {
        // Impossible operations are expected and must leave input immutable.
        assert.equal(JSON.stringify(state), before,
          'Rejected mutation touched the source; seed=' + seed + ' step=' + step);
        rejected++;
        continue;
      }
      assert.equal(JSON.stringify(state), before, 'Mutation changed source');
      try { invariants(next, 320 + (step % 20) * 73); }
      catch (error) { error.message += ' [seed=' + seed + ' step=' + step + ']'; throw error; }
      state = next; accepted++;
    }
    assert.ok(accepted > 100 && rejected > 30,
      'The test must exercise both successful and rejected operations');
  });
}

test('V5 repeated Undo, Cancel, Done preserve persisted user state under random edits', () => {
  const rand = random(93271);
  let saved = M.defaultLayout();
  for (let round = 0; round < 100; round++) {
    const original = M.clone(saved);
    let writes = 0;
    const session = M.createEditSession(saved, state => { saved = M.clone(state); writes++; });
    for (let step = 0; step < 8; step++) {
      let next;
      try { next = attempt(session.get(), rand); }
      catch (e) { continue; }
      session.apply(() => next);
      if (step % 4 === 3) session.undo();
    }
    if (round % 2 === 0) {
      assert.deepEqual(session.cancel(), original);
      assert.deepEqual(saved, original);
      assert.equal(writes, 0);
    } else {
      session.done();
      assert.equal(writes, 1);
      assert.equal(M.validate(saved), true);
      assert.equal(M.findControl(saved, 'Settings') !== null, true);
    }
    invariants(saved, 1280);
  }
});
