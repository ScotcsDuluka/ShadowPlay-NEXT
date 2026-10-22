'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),M=require('../osc/mods/main-menu-group-layout-a'),V4=require('../osc/mods/main-menu-unified-model');
const keys=V4.CONTROLS.map(c=>c.key);
const withSettings=g=>({version:5,panels:{left:[g],center:[],right:[M.group([M.makeBlock('side','full',['Settings'])])]}});
test('Group is a fixed 240x240 region and small presets produce correct slot widths',()=>{
 for(const [name,widths] of Object.entries({three:[80,80,80],'wide-left':[160,80],'wide-right':[80,160],full:[240]})){
  const block=M.makeBlock('small',name,keys.slice(0,widths.length)),s=withSettings(M.group([block])),c=M.layout(s).cells.filter(x=>x.panel==='left');
  assert.deepEqual(c.map(x=>x.width),widths);assert(c.every(x=>x.height===80&&x.group===0));
 }
});
test('side presets are text/icon or single full row',()=>{
 for(const [preset,widths] of Object.entries({full:[240],'text-left':[180,60],'text-right':[60,180]})){
  const s=withSettings(M.group([M.makeBlock('side',preset,keys.slice(0,widths.length))]));
  assert.deepEqual(M.layout(s).cells.filter(x=>x.panel==='left').map(x=>x.width),widths);
 }
});
test('big block occupies entire Group and three rows fit in a Group',()=>{
 const g=M.group([M.makeBlock('small','three',keys.slice(0,3)),M.makeBlock('side','full',[keys[3]]),M.makeBlock('small','full',[keys[4]])]);
 const s=withSettings(g),geo=M.layout(s);
 assert.equal(geo.panels.left.height,240);
 assert.deepEqual(geo.cells.filter(x=>x.panel==='left').map(x=>x.y),[0,0,0,80,160]);
 assert.throws(()=>M.validate(withSettings(M.group([...g.blocks,M.makeBlock('side','full',[keys[5]])]))),/exceeds/);
 const big=withSettings(M.group([M.makeBlock('big','full',[keys[0]])]));
 assert.deepEqual(M.layout(big).cells[0].width,240);assert.equal(M.layout(big).cells[0].height,240);
});
test('migrate twelve V4 Controls without losing Settings or mutating the old layout',()=>{
 const source=V4.initial(),prior=JSON.stringify(source);
 const v5=M.migrateV4(source),old=V4.PANELS.flatMap(p=>source.panels[p].flatMap(b=>b.controls));
 const now=M.layout(v5).cells.map(c=>c.key);
 assert.deepEqual(new Set(now),new Set(old));assert.equal(JSON.stringify(source),prior);
 assert(M.validate(v5));assert.equal(now.filter(x=>x==='Settings').length,1);
});
test('V5 rejects duplicate Controls, impossible slot counts, and missing Settings',()=>{
 const s=M.migrateV4(V4.initial());assert(M.validate(s));
 assert.throws(()=>M.makeBlock('small','three',[keys[0]]),/3 Slots/);
 const duplicate=M.migrateV4(V4.initial());duplicate.panels.center[0].blocks[0].slots[0]='Settings';
 assert.throws(()=>M.validate(duplicate),/Duplicate/);
 assert.throws(()=>M.validate(M.empty()),/Settings cannot/);
});
test('setSlot retains Settings mandatory and is immutable',()=>{
 const s=M.migrateV4(V4.initial()),copy=JSON.stringify(s),target=s.panels.center[0].blocks[0].slots[0];
 assert.throws(()=>M.setSlot(s,'right',2,0,0,target),/Settings cannot/);
 const next=M.setSlot(s,'right',2,0,0,'Settings');
 assert(M.validate(next));assert.equal(JSON.stringify(s),copy);
});
