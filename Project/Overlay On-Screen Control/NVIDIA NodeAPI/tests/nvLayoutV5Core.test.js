'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const M = require('../osc/mods/nv-layout-v5-core.js');

function defaultState() { return M.defaultLayout(); }
function totalCells(s) {
  return M.geometry(s, 'normal', 1920).cells.length;
}
test('native default preserves all 12 Controls and six Groups in 1/4/1 split', () => {
  const s = defaultState();
  assert.equal(M.validate(s), true);
  assert.equal(M.count(s), 6);
  assert.deepEqual(s.panels.left.map(x => x.id), ['g1']);
  assert.deepEqual(s.panels.center.map(x => x.id), ['g2','g3','g4','g5']);
  assert.deepEqual(s.panels.right.map(x => x.id), ['g6']);
  assert.equal(totalCells(s), 12);
  assert.deepEqual(M.available(s), []);
  assert.equal(M.findControl(s, 'Settings').groupId, 'g6');
});
test('all three Panels are unrestricted individually, while global Group max stays six', () => {
  let s = defaultState();
  for (const group of ['g2','g3','g4','g5','g6']) s = M.moveGroup(s, group, 'left');
  assert.equal(s.panels.left.length, 6);
  assert.equal(s.panels.center.length, 0);
  assert.equal(s.panels.right.length, 0);
  assert.throws(() => M.addGroup(s, 'left'), /Only six Groups/);
  s = M.deleteGroup(s, 'g2');
  assert.equal(M.count(s), 5);
  assert.deepEqual(M.available(s), ['Stream']);
  s = M.addGroup(s, 'right');
  assert.equal(M.count(s), 6);
  assert.equal(s.panels.right[0].blocks.length, 0);
});
test('empty Group counts towards six but hides in Normal Mode and appears in Edit Mode', () => {
  let s = M.deleteGroup(defaultState(), 'g2');
  s = M.addGroup(s, 'right');
  const id = s.panels.right.at(-1).id;
  assert.equal(M.geometry(s, 'normal', 1920).boxes.length, 5);
  assert.equal(M.geometry(s, 'edit', 1920).boxes.length, 6);
  assert.ok(M.geometry(s, 'normal', 1920).hiddenGroupIds.includes(id));
  assert.ok(M.geometry(s, 'edit', 1920).boxes.some(x => x.id === id && x.empty));
});
test('Auto-Fill ON yields 240 / 120 / 80 and OFF preserves 80px rows', () => {
  let s = defaultState();
  const geo = state => M.geometry(state, 'normal', 1920);
  assert.equal(geo(s).cells.find(x => x.control === 'Screenshot').height, 80);
  assert.equal(geo(s).cells.find(x => x.control === 'Stream').height, 240);
  s = M.removeBlock(s, 'g1', 2);
  assert.equal(geo(s).cells.find(x => x.control === 'Screenshot').height, 120);
  s = M.removeBlock(s, 'g1', 1);
  assert.equal(geo(s).cells.find(x => x.control === 'Screenshot').height, 240);
  s = M.autoFill(s, false);
  assert.equal(geo(s).cells.find(x => x.control === 'Screenshot').height, 80);
  assert.equal(geo(s).cells.find(x => x.control === 'Stream').height, 240);
  assert.equal(geo(s).cells.find(x => x.control === 'Screenshot').iconScale, 1);
  s = M.autoFill(s, true); assert.equal(s.autoFill, true);
});
test('all 8 Small/Side/Big presets have exact Slot capacities', () => {
  let s = M.deleteGroup(defaultState(), 'g2');
  s = M.addGroup(s, 'left');
  const id = s.panels.left.at(-1).id;
  const variants = [
    ['small','triple',3],['small','wideLeft',2],['small','wideRight',2],
    ['small','single',1],['side','single',1],['side','detailLeft',2],
    ['side','detailRight',2],['big','single',1]
  ];
  variants.forEach(([type, layout, slots]) => {
    const changed = M.addBlock(s,id,type,layout);
    assert.equal(M.locate(changed,id).group.blocks[0].slots.length, slots);
  });
});
test('Control -> Control swaps, Control -> empty Slot transfers without duplication', () => {
  let s = defaultState();
  const original = M.findControl(s,'Settings');
  s = M.placeControl(s, 'Screenshot', 'g6', 2, 0);
  assert.equal(M.findControl(s,'Screenshot').groupId,'g6');
  assert.equal(M.findControl(s,'Settings').groupId,'g1');
  assert.equal(totalCells(s),12);
  assert.equal(M.validate(s),true);
  s = M.placeControl(s, 'Stream', 'g1', 1, 0);
  assert.equal(M.findControl(s,'Stream').groupId,'g1');
  assert.equal(totalCells(s),12);
  assert.notDeepEqual(M.findControl(s,'Settings'),original);
});
test('palette Control fills empty Slot or replaces occupied Slot and returns displaced Control', () => {
  let s = M.takeControl(defaultState(),'Screenshot');
  assert.ok(M.available(s).includes('Screenshot'));
  s = M.placeControl(s,'Screenshot','g2',0,0);
  assert.equal(M.findControl(s,'Screenshot').groupId,'g2');
  assert.equal(M.available(s).includes('Screenshot'),false);
  assert.deepEqual(M.available(s),['Stream']);
  s = M.placeControl(s,'Stream','g1',0,0);
  assert.equal(M.available(s).length,0);
  assert.equal(totalCells(s),12);
});
test('layout shrinking returns excess Controls to catalog in original order', () => {
  let s = defaultState();
  s = M.changeBlock(s,'g6',0,'small','wideLeft');
  assert.deepEqual(M.locate(s,'g6').group.blocks[0].slots,['Performance','Mic']);
  assert.deepEqual(M.available(s),['Camera']);
  s = M.changeBlock(s,'g6',0,'small','single');
  assert.deepEqual(M.available(s),['Mic','Camera']);
  assert.equal(M.locate(s,'g6').group.blocks[0].slots[0],'Performance');
});

test('displaced palette order follows prior Slots after arbitrary swaps, across repeated reductions', () => {
  let s = M.placeControl(defaultState(),'Camera','g6',0,0);
  assert.deepEqual(M.locate(s,'g6').group.blocks[0].slots,['Camera','Mic','Performance']);
  s = M.changeBlock(s,'g6',0,'small','wideLeft');
  assert.deepEqual(M.available(s),['Performance']);
  s = M.changeBlock(s,'g6',0,'small','single');
  assert.deepEqual(M.available(s),['Mic','Performance']);
  assert.deepEqual(M.available(M.clone(s)),['Mic','Performance']);
  const restored = JSON.parse(JSON.stringify(s));
  assert.deepEqual(M.available(restored),['Mic','Performance']);
});

test('Group deletion and Big replacement return Controls in their previous native order', () => {
  let s = M.placeControl(defaultState(),'ManualRecord','g1',0,0);
  assert.deepEqual(M.available(M.deleteGroup(s,'g1')),['ManualRecord','Mods','Screenshot']);
  s = M.changeBlock(s,'g1',1,'big','single');
  assert.deepEqual(M.available(s),['ManualRecord','Screenshot']);
  assert.equal(M.findControl(s,'Mods').groupId,'g1');
});
test('changing Group to Big releases displaced controls and rejects loss of Settings', () => {
  let s = defaultState();
  s = M.changeBlock(s,'g1',0,'big','single');
  assert.equal(M.locate(s,'g1').group.blocks.length,1);
  assert.ok(M.available(s).includes('Mods'));
  assert.ok(M.available(s).includes('ManualRecord'));
  assert.throws(()=>M.changeBlock(defaultState(),'g6',0,'big','single'),/Move Settings/);
});
test('Settings cannot be removed, duplicated, or lost when reducing Slot count', () => {
  let s = defaultState();
  assert.throws(()=>M.takeControl(s,'Settings'),/Settings cannot be removed/);
  assert.throws(()=>M.deleteGroup(s,'g6'),/Move Settings/);
  assert.throws(()=>M.removeBlock(s,'g6',2),/Move Settings/);
  s = M.placeControl(s,'Settings','g6',0,2);
  assert.equal(M.findControl(s,'Settings').block,0);
  assert.throws(()=>M.changeBlock(s,'g6',0,'small','wideLeft'),/Move Settings/);
  assert.throws(()=>M.addBlock(s,'g6','big','single',undefined,true),/Move Settings/);
});

test('a palette replacement cannot eject Settings and rejected changes leave the source intact', () => {
  const s = M.takeControl(defaultState(),'Screenshot');
  const before = M.clone(s);
  assert.throws(()=>M.placeControl(s,'Screenshot','g6',2,0),/Move Settings/);
  assert.deepEqual(s,before);
  assert.equal(M.findControl(s,'Settings').groupId,'g6');
  assert.deepEqual(M.available(s),['Screenshot']);
});
test('deleting Group releases all normal Controls but not Settings',()=>{
  let s = M.deleteGroup(defaultState(),'g1');
  assert.deepEqual(M.available(s),['Screenshot','Mods','ManualRecord']);
  assert.throws(()=>M.deleteGroup(s,'g6'),/Move Settings/);
});
test('Group -> Group swap preserves identities across Panels and no duplicate IDs',()=>{
  let s = M.swapGroups(defaultState(),'g1','g6');
  assert.equal(s.panels.left[0].id,'g6');
  assert.equal(s.panels.right[0].id,'g1');
  assert.equal(M.findControl(s,'Settings').panel,'left');
  assert.equal(M.count(s),6);
});
test('Group insertion preview is immutable; move commits only after drop',()=>{
  let s = defaultState();
  let preview = M.insertionPreview(s,'g1','center',2);
  assert.equal(s.panels.left[0].id,'g1');
  assert.equal(preview.pending,true);
  assert.equal(preview.layout.panels.center.length,5);
  let done = M.moveGroup(s,'g1','center',2);
  assert.deepEqual(preview.layout,done);
  assert.equal(M.validate(done),true);
});

test('Group inserts use pre-removal indexes in either direction and across Panels',()=>{
  const original=defaultState();
  let s=M.moveGroup(original,'g2','center',4);
  assert.deepEqual(s.panels.center.map(g=>g.id),['g3','g4','g5','g2']);
  s=M.moveGroup(s,'g2','center',0);
  assert.deepEqual(s.panels.center.map(g=>g.id),['g2','g3','g4','g5']);
  s=M.moveGroup(s,'g6','center',2);
  assert.deepEqual(s.panels.center.map(g=>g.id),['g2','g3','g6','g4','g5']);
  assert.equal(s.panels.right.length,0);
  assert.equal(M.findControl(s,'Settings').panel,'center');
  assert.equal(M.count(s),6);
  assert.deepEqual(original,defaultState());
  assert.throws(()=>M.moveGroup(s,'g1','center',6),/insertion index/);
});
test('edit session supports live draft, undo, cancel without saving',()=>{
  let saved=0;const original=defaultState();
  const session=M.createEditSession(original,()=>saved++);
  session.apply(s=>M.autoFill(s,false));
  assert.equal(session.get().autoFill,false);
  session.undo();
  assert.equal(session.get().autoFill,true);
  session.apply(s=>M.deleteGroup(s,'g2'));
  assert.equal(session.get().panels.center.length,3);
  assert.deepEqual(session.cancel(),original);
  assert.equal(saved,0);
  assert.equal(session.isEditing(),false);
});
test('Done persists once, preserves autoFill and rejects persistence failure',()=>{
  let saved=null;
  const session=M.createEditSession(defaultState(),s=>saved=s);
  session.apply(s=>M.autoFill(s,false));
  session.apply(s=>M.swapGroups(s,'g1','g6'));
  assert.equal(saved,null);
  const done=session.done();
  assert.deepEqual(saved,done);
  assert.equal(saved.autoFill,false);
  assert.throws(()=>session.undo(),/No Edit session/);
  const failing=M.createEditSession(defaultState(),()=>{throw Error('Disk full')});
  failing.apply(s=>M.autoFill(s,false));
  assert.throws(()=>failing.done(),/Disk full/);
  assert.equal(failing.isEditing(),true);
});

test('Cancel restores ordered palette, empty Groups and Auto-Fill exactly without persisting',()=>{
  let s=M.placeControl(defaultState(),'Camera','g6',0,0);
  s=M.changeBlock(s,'g6',0,'small','single');
  s=M.addGroup(M.deleteGroup(s,'g2'),'right');
  let saved=0;
  const session=M.createEditSession(s,()=>saved++);
  session.apply(draft=>M.autoFill(draft,false));
  session.apply(draft=>M.placeControl(draft,'Mic','g1',0,0));
  session.apply(draft=>M.moveGroup(draft,'g6','left'));
  assert.deepEqual(session.cancel(),s);
  assert.deepEqual(session.get(),s);
  assert.equal(saved,0);
});
test('bad layouts, duplicated keys, excessive Groups and giant rows are rejected',()=>{
  let s=defaultState();s.panels.left[0].blocks[0].slots[0]='Settings';
  assert.throws(()=>M.validate(s),/Duplicate Control/);
  s=defaultState();s.panels.left[0].blocks.push({type:'side',layout:'single',slots:[null]});
  assert.throws(()=>M.validate(s),/3 Blocks/);
  s=defaultState();s.panels.left[0].blocks[0].type='big';
  assert.throws(()=>M.validate(s),/whole Group|240px/);
  s=defaultState();s.panels.left[0].blocks[0].slots[0]='toString';
  assert.throws(()=>M.validate(s),/Unknown Control/);
  s=defaultState();s.controlOrder=M.CONTROLS.slice();s.controlOrder[0]='Settings';
  assert.throws(()=>M.validate(s),/Invalid Control order/);
});
test('Normal Mode always hides decoration metadata for unfilled Groups; retains 240px geometry',()=>{
  let s=M.deleteGroup(defaultState(),'g2');
  s=M.addGroup(s,'right');
  const geo=M.geometry(s,'edit',600);
  assert.equal(geo.height,240);
  assert.ok(geo.scale<1);
  assert.equal(geo.boxes.length,6);
  assert.equal(geo.boxes[0].width,240);
  assert.equal(M.geometry(s,'normal',600).boxes.length,5);
});
