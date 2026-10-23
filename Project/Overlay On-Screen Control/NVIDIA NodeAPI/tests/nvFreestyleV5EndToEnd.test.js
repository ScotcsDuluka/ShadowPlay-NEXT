'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const M=require('../osc/mods/nv-layout-v5-core.js');
const read=name=>fs.readFileSync(path.join(__dirname,'../osc/mods',name),'utf8');
test('empty V5 slots have geometry for native drop targets',()=>{
 let s=M.defaultLayout();
 s=M.changeBlock(s,'g6',0,'small','wideLeft');
 s=M.takeControl(s,'Mic');
 const g=M.geometry(s,'edit',1920);
 assert.equal(g.boxes.length,6);
 assert.equal(g.slots.length,11);
 assert.ok(g.slots.some(x=>x.control===null));
 assert.equal(g.slots.filter(x=>x.control==='Settings').length,1);
});
test('drag assign / group move / swap / remove returns keys to catalog',()=>{
 let s=M.defaultLayout();
 s=M.takeControl(s,'Camera');
 s=M.placeControl(s,'Camera','g6',0,2);
 s=M.swapGroups(s,'g1','g2');
 assert.equal(M.findControl(s,'Settings').panel,'right');
 assert.equal(M.locate(s,'g1').panel,'center');
 assert.equal(M.validate(s),true);
 s=M.moveGroup(s,'g6','center');
 assert.equal(M.findControl(s,'Settings').panel,'center');
});
test('control drop into new Group with selected preset is atomic',()=>{
 let s=M.defaultLayout();
 s=M.deleteGroup(s,'g2');
 s=M.addGroup(s,'left');
 const id=s.panels.left.at(-1).id;
 s=M.addBlock(s,id,'side','detailRight');
 s=M.placeControl(s,'Stream',id,0,0);
 assert.equal(M.findControl(s,'Stream').groupId,id);
 assert.equal(M.available(s).includes('Stream'),false);
 assert.equal(M.validate(s),true);
});
test('layout shrinking never discards Settings during right-click conversion',()=>{
 let s=M.defaultLayout();
 s=M.placeControl(s,'Settings','g6',0,2);
 assert.throws(()=>M.changeBlock(s,'g6',0,'small','single'),/Settings/);
 assert.throws(()=>M.deleteGroup(s,'g6'),/Settings/);
});
test('V5 editor mounts in original shell and intercepts native click paths',()=>{
 let editor=read('nv-freestyle-editor.js'),settings=read('nv-freestyle-settings.js'),
      css=read('nv-freestyle-editor.css');
 for(const symbol of ['A.beginEdit()','A.edit(fn)','A.undo()','A.done()','A.cancel()',
 'M.placeControl','M.swapGroups','M.moveGroup','M.addGroup','M.addBlock','M.changeBlock',
 'contextmenu','mousedown','mousemove','mouseup','keydown','Escape','nv-f-overlay'])
   assert.ok(editor.includes(symbol),'missing '+symbol);
 assert.match(editor,/nv-main-menu md-menu > button\.menu-button/);
 assert.match(editor,/stop\(ev\);return/);
 assert.ok(settings.includes('.nv-freestyle-grid'));
 assert.match(css,/pointer-events:auto/);
});
test('floating panel matches original NVIDIA screenshot proportions rather than filling viewport',()=>{
 const s=read('nv-freestyle-settings.css');
 assert.match(s,/left:10\.4vw!important/);
 assert.match(s,/width:79\.2vw!important/);
 assert.match(s,/top:44vh!important/);
 assert.match(s,/grid-template-columns:minmax\(0,4fr\)/);
});
