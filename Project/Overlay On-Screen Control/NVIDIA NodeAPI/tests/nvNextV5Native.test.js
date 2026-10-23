'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const M=require('../osc/mods/nv-layout-v5-core.js');
const nativeCode=fs.readFileSync(path.join(__dirname,'../osc/mods/nv-next-v5-native.js'),'utf8');

function makeRuntime(flag, withClock=false, options={}) {
  const model=options.model||M;
  const frames=new Map();let frameId=0, onInterval=null;
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
  const nativeMeta=v4.CONTROLS.slice();
  if(options.gameProfiles!==false&&model.CONTROLS.includes('GameProfiles'))
    nativeMeta.push({key:'GameProfiles',name:'nv-game-profiles-tile'});
  nativeMeta.forEach(c=>{tiles[c.key]={
    key:c.key,style:style(),classList:classes(),
    querySelector(){return null},
    setAttribute(k,v){changes.set(c.key+'/'+k,v)},
    removeAttribute(k){changes.delete(c.key+'/'+k)}
  }});
  const root={
    getBoundingClientRect(){return {left:440,top:120,width:1040,height:350}},
    querySelector(name){
      if(name==='md-menu-bar')return bar;
      const c=nativeMeta.find(x=>name==='md-menu.'+x.name);
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
    NvShadowPlayLayoutV5:model,NvUnifiedLayoutV4:v4,innerWidth:1920,innerHeight:1200,
    localStorage:{getItem(k){return storage.get(k)||null},setItem(k,v){storage.set(k,String(v))},removeItem(k){storage.delete(k)}},
    setInterval:withClock?fn=>{onInterval=fn}:null,
    requestAnimationFrame:withClock?fn=>{const id=++frameId;frames.set(id,fn);return id}:null,
    cancelAnimationFrame:withClock?id=>frames.delete(id):null
  };
  vm.runInNewContext(nativeCode,{window,document}, {filename:'nv-next-v5-native.js',timeout:2500});
  return {window,document,root,bar,tiles,storage,changes,frames,legacy:v4,
    tick(){if(onInterval)onInterval()},
    advance(){const first=frames.entries().next().value;if(!first)return false;
      frames.delete(first[0]);first[1]();return true}
  };
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
 const before=t.legacy.CONTROLS.map(meta=>t.tiles[meta.key]);
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
test('Game Profiles reuses its native tile, stays unassigned by default and leaves V4 catalog intact',()=>{
 const t=makeRuntime(true),api=t.window.__nvNextLayoutV5, native=t.tiles.GameProfiles;
 assert.ok(native,'supported V5 catalog supplies the custom native Angular tile');
 assert.equal(t.legacy.CONTROLS.length,12);
 assert.equal(t.legacy.CONTROLS.some(meta=>meta.key==='GameProfiles'),false);
 assert.equal(api.sync(true).cells.length,12,'the original six-Group default stays unchanged');
 assert.equal(native.style.getPropertyValue('display'),'none');
 assert.equal(t.storage.has(api.storageKey),false,'sync does not save a replacement default');
 const next=M.placeControl(api.get(),'GameProfiles','g1',0,0);
 api.set(next);api.sync(true);
 assert.equal(t.tiles.GameProfiles,native,'assigning reuses the same node and native listeners');
 assert.equal(native.style.getPropertyValue('display'),'block');
 assert.equal(native.style.getPropertyValue('height'),'80px');
 assert.equal(t.tiles.Screenshot.style.getPropertyValue('display'),'none');
 assert.equal(t.changes.get('GameProfiles/data-nv-next-control'),'GameProfiles');
});
test('new catalog waits for Game Profiles and every original tile before changing geometry',()=>{
 const t=makeRuntime(true,false,{gameProfiles:false});
 assert.equal(t.window.__nvNextLayoutV5.sync(true),null);
 assert.equal(t.bar.style.getPropertyValue('width'),'');
 for(const tile of Object.values(t.tiles))assert.equal(tile.style.getPropertyValue('left'),'');
 const original=makeRuntime(true);delete original.tiles.Mods;
 assert.equal(original.window.__nvNextLayoutV5.sync(true),null,'custom Control never relaxes original twelve-tile readiness');
 assert.equal(original.bar.style.getPropertyValue('width'),'');
});
test('adapter still accepts older twelve-Control V5 catalogs without adding to V4 metadata',()=>{
 const older=Object.assign({},M,{CONTROLS:M.CONTROLS.filter(key=>key!=='GameProfiles')});
 const t=makeRuntime(true,false,{model:older});
 assert.ok(t.window.__nvNextLayoutV5.sync(true));
 assert.equal(t.tiles.GameProfiles,undefined);
 assert.equal(t.legacy.CONTROLS.length,12);
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
test('owner switch takes effect without reload and preserves V4/V5 experimental keys',()=>{
 const t=makeRuntime(false),api=t.window.__nvNextLayoutV5;
 const r=api.enable(true);
 assert.equal(r.reloadRequired,false);
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

test('temporary V5 edit ownership never persists the enable flag before Done',()=>{
 const t=makeRuntime(false), api=t.window.__nvNextLayoutV5;
 assert.equal(t.storage.has(api.flagKey),false);
 api.editOwnership(true);
 assert.equal(api.enabled(),true);
 assert.equal(t.storage.has(api.flagKey),false);
 assert.ok(api.sync(true));
 assert.equal(t.bar.style.getPropertyValue('position'),'relative');
 api.beginEdit();api.edit(s=>M.autoFill(s,false));api.cancel();
 api.editOwnership(false);
 assert.equal(api.enabled(),false);
 assert.equal(t.storage.has(api.flagKey),false);
 assert.equal(t.storage.has(api.storageKey),false);
 assert.equal(t.bar.style.getPropertyValue('transform'),'');
 assert.equal(t.bar.style.getPropertyValue('width'),'');
 assert.equal(t.document.documentElement.classList.contains('nv-next-v5-on'),false);
 for(const tile of Object.values(t.tiles)){
  assert.equal(tile.style.getPropertyValue('left'),'');
  assert.equal(tile.style.getPropertyValue('display'),'');
  assert.equal(tile.classList.contains('nv-next-v5-tile'),false);
 }
});
test('committing V5 edit keeps its native owner without modifying legacy saved keys',()=>{
 const t=makeRuntime(false),api=t.window.__nvNextLayoutV5;
 api.editOwnership(true);
 api.beginEdit();api.edit(s=>M.swapGroups(s,'g1','g6'));api.done();
 api.enable(true);api.editOwnership(false);
 assert.equal(api.enabled(),true);
 assert.equal(t.storage.get(api.flagKey),'1');
 assert.equal(t.storage.get('DulukaMainMenuUnifiedV4Enabled'),'1');
 assert.equal(t.storage.get('DulukaMainMenuGroupLayoutAEnabled'),'1');
 assert.equal(t.bar.style.getPropertyValue('width'),'');
 api.sync(true);
 assert.equal(t.bar.style.getPropertyValue('position'),'relative');
});

test('closed or animating Native Host does not keep scheduling V5 RAF geometry work',()=>{
 const t=makeRuntime(true,true), api=t.window.__nvNextLayoutV5;
 const motion={phase:'idle',target:'closed',getState(){return {target:this.target}}};
 t.window.__dulukaOscMotion=motion;
 for(let i=0;i<40;i++)t.tick();
 assert.equal(t.frames.size,0,'closed OSC cannot enqueue repeated RAF loops');
 assert.equal(t.bar.style.getPropertyValue('width'),'','closed OSC stays untouched');
 motion.phase='opening';motion.target='open';
 for(let i=0;i<15;i++)t.tick();
 assert.equal(t.frames.size,0,'entrance Motion may not reflow native tile geometry');
 assert.ok(api.prepareForMotion(t.root),'preflight geometry remains allowed before animation');
 assert.equal(t.frames.size,0);
 motion.phase='idle';t.tick();
 assert.equal(t.frames.size,1,'when native open settles, interval requests one geometry pass');
 assert.equal(t.advance(),true);
 assert.equal(t.bar.style.getPropertyValue('width'),'1496px');
 assert.equal(t.frames.size,1);
 // Native Close may arrive while a new RAF is queued: it must stop the
 // in-flight cascade rather than scheduling another 30 frames.
 motion.phase='closing';motion.target='closed';
 assert.equal(t.advance(),true);
 assert.equal(t.frames.size,0);
 for(let i=0;i<40;i++)t.tick();
 assert.equal(t.frames.size,0,'closing / closed must remain quiescent');
 motion.phase='idle';motion.target='open';t.tick();
 assert.equal(t.frames.size,1,'subsequent reopen resumes V5 geometry once ready');
});

test('temporary edit owner never spins RAF while closed and resumes without persisting a flag',()=>{
 const t=makeRuntime(false,true), api=t.window.__nvNextLayoutV5;
 const motion={phase:'idle',target:'closed',getState(){return {target:this.target}}};
 t.window.__dulukaOscMotion=motion;
 api.editOwnership(true);
 assert.equal(api.enabled(),true);
 for(let i=0;i<40;i++)t.tick();
 assert.equal(t.frames.size,0);
 assert.equal(t.storage.get(api.flagKey),undefined,'Edit must not persist enable flag');
 motion.target='open';
 t.tick();
 assert.equal(t.frames.size,1);
 api.editOwnership(false);
 assert.equal(t.frames.size,0,'owner release cancels queued RAF');
 assert.equal(api.enabled(),false);
 assert.equal(t.storage.get(api.flagKey),undefined);
});
