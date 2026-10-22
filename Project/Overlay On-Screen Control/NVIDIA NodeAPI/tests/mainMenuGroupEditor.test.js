'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const Group=require('../osc/mods/main-menu-group-model.js');
const V4=require('../osc/mods/main-menu-unified-model.js');
const all=V4.CONTROLS.map(x=>x.key);
const modules=path.join(__dirname,'../osc/mods');
function v4Controls(v4){return V4.PANELS.flatMap(p=>v4.panels[p].flatMap(b=>b.controls));}
function v5Controls(v5){return Group.slots(v5);}
function source(name){return fs.readFileSync(path.join(modules,name),'utf8');}
test('Group contains up to three 80px rows; Big fills the entire 240px Group',()=>{
 const v=Group.fromV4(V4.initial());assert(Group.validate(v));
 const first=v.panels.left[0];
 assert(Group.rows(first)>=1&&Group.rows(first)<=3);
 assert.throws(()=>Group.addBlock(v,'center',0,'big','single'),/Group|Big|row/i);
 let s=Group.createGroup(v,'left','small','triple');
 const i=s.panels.left.length-1;
 s=Group.addBlock(s,'left',i,'side','single');
 s=Group.addBlock(s,'left',i,'small','single');
 assert.equal(Group.rows(s.panels.left[i]),3);
 assert.throws(()=>Group.addBlock(s,'left',i,'small','single'),/Group|row/i);
});
test('all eight layout presets have correct slot widths',()=>{
 const expected={
  small:{triple:[80,80,80],wideLeft:[160,80],wideRight:[80,160],single:[240]},
  side:{single:[240],detailLeft:[160,80],detailRight:[80,160]},
  big:{single:[240]}
 };
 assert.deepEqual(Group.PRESETS, {
  small:{triple:[1,1,1],wideLeft:[2,1],wideRight:[1,2],single:[3]},
  side:{single:[3],detailLeft:[2,1],detailRight:[1,2]},
  big:{single:[3]}
 });
 for(const kind of Object.keys(expected))for(const preset of Object.keys(expected[kind])){
  let g=Group.fromV4(V4.initial());
  g=Group.createGroup(g,'left',kind,preset);
  let index=g.panels.left.length-1;
  const widths=expected[kind][preset];let keys=all.filter(k=>k!=='Settings').slice(0,widths.length);
  keys.forEach((key,slot)=>{g=Group.setSlot(g,key,'left',index,0,slot)});
  const cells=Group.geometry(g,1920,1080).cells.filter(c=>c.group===g.panels.left[index].id);
  assert.deepEqual(cells.map(x=>x.width),widths,kind+'.'+preset);
  assert(cells.every(c=>c.height===(kind==='big'?240:80)));
 }
});
test('migration preserves visible V4 controls and never modifies the original',()=>{
 const v4=V4.initial(),snapshot=JSON.stringify(v4);
 let v5=Group.fromV4(v4);
 assert(Group.validate(v5));assert.deepEqual(new Set(v5Controls(v5)),new Set(v4Controls(v4)));
 assert.equal(v5Controls(v5).length,v4Controls(v4).length);
 assert.equal(JSON.stringify(v4),snapshot);
 assert.equal(Group.find(v5,'Settings').panel,'right');
 for(const key of all)assert(Group.find(v5,key),'Missing '+key);
});
test('hidden V4 controls stay hidden after V5 migration',()=>{
 const v4=V4.initial({left:['Screenshot'],center:['InstantReplay']},{left:[['Screenshot']],right:[['Settings']]});
 const v5=Group.fromV4(v4);
 assert.deepEqual(new Set(v5Controls(v5)),new Set(['Screenshot','InstantReplay','Settings']));
});
test('each panel can host all twelve controls across Groups without duplicates',()=>{
 let state=Group.fromV4(V4.initial());
 for(const panel of Group.PANELS){
  state=Group.createGroup(state,panel,'small','triple');
  let gi=state.panels[panel].length-1;
  for(const key of all) {
   // create an additional Group for each Control to preserve one-Control-per-Slot.
   state=Group.createGroup(state,panel,'side','single');
   state=Group.setSlot(state,key,panel,state.panels[panel].length-1,0,0);
  }
  assert(Group.validate(state));
  assert.deepEqual(new Set(v5Controls(state)),new Set(all));
 }
});
test('placing a Control swaps with another Control and Settings is never removable',()=>{
 let s=Group.fromV4(V4.initial()),a=Group.find(s,'Screenshot'),b=Group.find(s,'Settings');
 const copy=JSON.stringify(s);
 s=Group.setSlot(s,'Settings',a.panel,a.group,a.block,a.slot);
 assert.equal(Group.find(s,'Settings').panel,a.panel);
 assert.equal(Group.find(s,'Screenshot').panel,b.panel);
 assert.equal(JSON.stringify(Group.fromV4(V4.initial())),copy);
 assert.throws(()=>Group.emptySlot(s,a.panel,a.group,a.block,a.slot),/Settings/);
 assert(Group.validate(s));
});
test('preset changes may not silently discard occupied Slots',()=>{
 let state=Group.fromV4(V4.initial()),a=Group.find(state,'Performance');
 assert(a);
 assert.throws(()=>Group.choosePreset(state,a.panel,a.group,a.block,'big','single'),/Move Controls|Group/i);
});
test('group movement is immutable and retains all controls',()=>{
 let state=Group.fromV4(V4.initial()),before=JSON.stringify(state);
 state=Group.moveGroup(state,'left',0,'right',0);
 assert.equal(JSON.stringify(Group.fromV4(V4.initial())),before);
 assert.deepEqual(new Set(v5Controls(state)),new Set(all));
 assert(Group.validate(state));
});
test('geometry fits desktop and compact monitors with no overflow after scaling',()=>{
 let s=Group.fromV4(V4.initial());
 for(const [w,h] of [[1920,1080],[1366,768],[1024,600],[800,480]]){
  const g=Group.geometry(s,w,h),sf=g.scale;
  assert(sf>0&&sf<=1);
  assert(g.actualWidth<=w&&g.actualHeight<=h);
  for(const cell of g.cells)assert(cell.x>=0&&cell.y>=0&&cell.x+cell.width<=g.width+0.01&&cell.y+cell.height<=g.height+.01);
 }
});
test('Group editor scripts load after V4, and old model stays opt-in safe',()=>{
 const index=fs.readFileSync(path.join(__dirname,'../osc/index.html'),'utf8');
 const unified=index.indexOf('/mods/main-menu-unified-native.js?v=');
 const newModel=index.indexOf('/mods/main-menu-group-model.js?v=');
 const newEditor=index.indexOf('/mods/main-menu-group-editor.js?v=');
 assert(unified>=0&&newModel>unified&&newEditor>newModel);
 assert(!index.includes('src="/mods/main-menu-group-layout-a.js'), 'Do not load both models as NvGroupLayoutA');
 const bridge=source('main-menu-unified-native.js'),ui=source('main-menu-group-editor.js');
 assert(bridge.includes('group.model.geometry(group.get()'));
 assert(bridge.includes('group.model.slots(group.get())'));
 assert(ui.includes("localStorage.getItem(FLAG)==='1'"),'Must default off');
 assert(ui.includes('NvGroupLayoutA'));
 assert(ui.includes('NvUnifiedLayoutV4'));
});

test('Group Layout A launches from existing Customize toolbar and can return to V4',()=>{
 const editor=source('main-menu-group-editor.js');
 assert(editor.includes("launcher.setAttribute('data-g-launch','')"));
 assert(editor.includes("launcher.addEventListener('click',function(){api.enable(!enabled());})"));
 assert(editor.includes("launcher.textContent=enabled()?'Use V4 editor':'Group Layout A'"));
 assert(editor.includes("host.classList.remove('nv-group-layout-on')"));
 assert(editor.includes("host.classList.add('nv-group-layout-on')"));
 assert(editor.includes("window.__dulukaUnifiedMainMenu.setEnabled(true)"));
 const css=source('main-menu-group-editor.css');
 assert(css.includes('nv-g-launch'));
});

test('disabled Group A does not modify V4 storage or native handlers and can be toggled back',()=>{
 const vm=require('node:vm');
 const db=new Map(),writes=[];
 const localStorage={
  getItem:key=>db.has(key)?db.get(key):null,
  setItem:(key,value)=>{writes.push([key,String(value)]);db.set(key,String(value));},
  removeItem:key=>{writes.push([key,null]);db.delete(key);}
 };
 const original=V4.initial(),unchanged=JSON.stringify(original);
 let onCalls=0;
 const fakeWindow={
  NvGroupLayoutA:Group,NvUnifiedLayoutV4:V4,localStorage:localStorage,
  __dulukaUnifiedMainMenu:{
   get:()=>V4.clone(original),
   setEnabled:state=>{if(state)onCalls++}
  },
  setInterval:()=>0
 };
 vm.runInNewContext(source('main-menu-group-editor.js'),{
  window:fakeWindow,document:{querySelector:()=>null}
 });
 const app=fakeWindow.__dulukaGroupLayoutA;
 assert(app&&!app.enabled());
 assert.deepEqual(new Set(Group.slots(app.get())),new Set(V4.PANELS.flatMap(p=>original.panels[p].flatMap(x=>x.controls))));
 assert.equal(writes.length,0,'First reading the V5 editor must not change storage');
 assert(app.enable(true));assert.equal(onCalls,1);
 assert.equal(db.get('DulukaMainMenuGroupLayoutAEnabled'),'1');
 app.enable(false);assert(!app.enabled());
 assert.equal(JSON.stringify(original),unchanged);
 assert(!writes.some(x=>x[0]==='DulukaMainMenuLayoutV2'||x[0]==='DulukaMainMenuUnifiedV4'));
});
