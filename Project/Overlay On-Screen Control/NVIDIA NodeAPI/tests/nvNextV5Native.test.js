'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const M=require('../osc/mods/nv-layout-v5-core.js');
const nativeCode=fs.readFileSync(path.join(__dirname,'../osc/mods/nv-next-v5-native.js'),'utf8');

function makeRuntime(flag) {
  const storage=new Map([
    ['DulukaMainMenuUnifiedV4Enabled','1'],
    ['DulukaMainMenuGroupLayoutAEnabled','1']
  ]);
  if(flag)storage.set('DulukaNextNativeLayoutV5Enabled','1');
  const changes=new Map(),tiles={},bar={
    style:style(),
    classList:classes(),
    offsetLeft:0,
    parentElement:{getBoundingClientRect(){return {left:480,width:1040,height:240}}},
    getBoundingClientRect(){return {left:480,width:1496,height:240}}
  };
  const v4={
    CONTROLS:[
      ['Screenshot','screenshot-tile'],['Ansel','ansel-tile'],
      ['Mods','mods-tile'],['InstantReplay','instant-replay-tile'],
      ['ManualRecord','manual-record-tile'],['Stream','stream-tile'],
      ['Broadcast','broadcast-tile'],['Performance','performance-tile'],
      ['Mic','mic-tile'],['Camera','camera-tile'],['Gallery','gallery-tile'],
      ['Settings','nv-settings-menu-tile']
    ].map(([key,name])=>({key,name}))
  };
  v4.CONTROLS.forEach(c=>{tiles[c.key]={
    key:c.key,style:style(),classList:classes(),
    querySelector(){return null},
    setAttribute(k,v){changes.set(c.key+'/'+k,v)},
    removeAttribute(k){changes.delete(c.key+'/'+k)}
  }});
  const root={
    getBoundingClientRect(){return {left:440,top:120,width:1040,height:350}},
    querySelector(name){
      if(name==='md-menu-bar')return bar;
      const c=v4.CONTROLS.find(x=>name==='md-menu.'+x.name);
      return c?tiles[c.key]:null;
    }
  };
  let editor=false;
  const document={
    documentElement:{classList:classes()},
    querySelector(q){if(q==='nv-main-menu:not([data-customize-preview]) .main-menu-container')return root;return null}
  };
  if(editor)document.documentElement.classList.add('nv-live-layout-editing');
  const window={
    NvShadowPlayLayoutV5:M,NvUnifiedLayoutV4:v4,innerWidth:1920,innerHeight:1200,
    localStorage:{getItem(k){return storage.get(k)||null},setItem(k,v){storage.set(k,String(v))},removeItem(k){storage.delete(k)}},
    setInterval:null,requestAnimationFrame:null
  };
  vm.runInNewContext(nativeCode,{window,document}, {filename:'nv-next-v5-native.js',timeout:2500});
  return {window,document,root,bar,tiles,storage,changes};
}
function classes(){
 const set=new Set();
 return {add(...x){x.forEach(k=>set.add(k))},remove(...x){x.forEach(k=>set.delete(k))},contains(x){return set.has(x)},
   toggle(x,on){if(on)set.add(x);else set.delete(x)}};
}
function style(){
 const properties=new Map();
 return {setProperty(k,v){properties.set(k,v)},getPropertyValue(k){return properties.get(k)||''},
   getPropertyPriority(k){return 'important'}};
}
test('disabled NEXT V5 does not alter any live native button or old user layout key',()=>{
 const t=makeRuntime(false);
 const api=t.window.__nvNextLayoutV5;
 assert.equal(api.enabled(),false);assert.equal(api.sync(),null);
 assert.equal(t.bar.style.getPropertyValue('width'),'');
 for(const tile of Object.values(t.tiles))assert.equal(tile.style.getPropertyValue('left'),'');
 assert.equal(t.storage.get('DulukaMainMenuUnifiedV4Enabled'),'1');
});
test('enabled adapter reuses all twelve original NVIDIA md-menu elements, no clones',()=>{
 const t=makeRuntime(true);
 const before=Object.values(t.tiles);
 const api=t.window.__nvNextLayoutV5;
 const g=api.sync(true);
 assert.equal(g.cells.length,12);
 assert.equal(g.boxes.length,6);
 assert.equal(g.width,1496);
 assert.equal(t.bar.style.getPropertyValue('width'),'1496px');
 assert.ok(t.bar.style.getPropertyValue('transform').includes('translateX('));
 assert.equal(t.document.documentElement.classList.contains('nv-next-v5-on'),true);
 for(const el of before) {
  assert.equal(t.tiles[el.key],el);
  assert.ok(el.classList.contains('nv-next-v5-tile'));
  assert.equal(el.style.getPropertyValue('position'),'absolute');
  assert.equal(el.style.getPropertyValue('display'),'block');
 }
 const left=+parseFloat(t.tiles.Screenshot.style.getPropertyValue('left'));
 const settings=+parseFloat(t.tiles.Settings.style.getPropertyValue('left'));
 assert.ok(left<settings);
 assert.equal(t.tiles.Stream.style.getPropertyValue('height'),'240px');
 assert.equal(t.tiles.Mic.style.getPropertyValue('height'),'80px');
});
test('native sync hides unassigned Controls without removing or changing their listeners',()=>{
 const t=makeRuntime(true),api=t.window.__nvNextLayoutV5;
 let s=M.takeControl(M.defaultLayout(),'Screenshot');
 api.set(s);api.sync(true);
 assert.equal(t.tiles.Screenshot.style.getPropertyValue('display'),'none');
 assert.equal(t.tiles.Settings.style.getPropertyValue('display'),'block');
 assert.ok(t.changes.has('Screenshot/aria-hidden'));
 s=M.placeControl(s,'Screenshot','g1',0,0);api.set(s);api.sync(true);
 assert.equal(t.tiles.Screenshot.style.getPropertyValue('display'),'block');
 assert.equal(t.changes.has('Screenshot/aria-hidden'),false);
});
test('NEXT V5 edit state stays ephemeral until Done, supports Cancel and Undo',()=>{
 const t=makeRuntime(true),api=t.window.__nvNextLayoutV5;
 t.document.documentElement.classList.add('nv-live-layout-editing');
 api.beginEdit();api.edit(s=>M.autoFill(s,false));
 assert.equal(api.get().autoFill,false);
 assert.equal(t.storage.get(api.storageKey),undefined);
 api.undo();assert.equal(api.get().autoFill,true);
 api.edit(s=>M.swapGroups(s,'g1','g6'));
 assert.equal(api.get().panels.left[0].id,'g6');
 api.cancel();
 assert.equal(api.get().panels.left[0].id,'g1');
 assert.equal(t.storage.get(api.storageKey),undefined);
 api.beginEdit();api.edit(s=>M.autoFill(s,false));api.done();
 assert.equal(JSON.parse(t.storage.get(api.storageKey)).autoFill,false);
});
test('owner switch requests reload and does not erase V4/V5 experimental keys',()=>{
 const t=makeRuntime(false),api=t.window.__nvNextLayoutV5;
 const r=api.enable(true);
 assert.equal(r.reloadRequired,true);
 assert.equal(t.storage.get(api.flagKey),'1');
 assert.equal(t.storage.get('DulukaMainMenuUnifiedV4Enabled'),'1');
 assert.equal(t.storage.get('DulukaMainMenuGroupLayoutAEnabled'),'1');
 api.enable(false);
 assert.equal(t.storage.has(api.flagKey),false);
});
test('Motion preflight is supported; ordinary sync avoids closing and opening phases',()=>{
 const t=makeRuntime(true),api=t.window.__nvNextLayoutV5;
 t.window.__dulukaOscMotion={phase:'opening',getState(){return {target:'open'}}};
 assert.equal(api.sync(),null);
 assert.ok(api.prepareForMotion(t.root));
 t.window.__dulukaOscMotion={phase:'idle',getState(){return {target:'closed'}}};
 assert.equal(api.sync(),null);
});
