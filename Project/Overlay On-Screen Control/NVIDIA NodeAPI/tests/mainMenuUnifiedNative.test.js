'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const M=require('../osc/mods/main-menu-unified-model.js');
const js=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-unified-native.js'),'utf8');
const orig=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-live.js'),'utf8');
const gpu=fs.readFileSync(path.join(__dirname,'../osc/mods/gpu-profiles.js'),'utf8');
function fake(){
 const values={},windows={innerWidth:1920,innerHeight:1200,NvUnifiedLayoutV4:M,
   localStorage:{getItem:k=>Object.hasOwn(values,k)?values[k]:null,setItem:(k,v)=>{values[k]=String(v)},removeItem:k=>{delete values[k]}},
   __dulukaMenuLayout:{get:()=>({version:2,left:['Screenshot','Ansel','Mods'],center:['InstantReplay','ManualRecord','Stream','Broadcast']}),
    getSides:()=>({left:[['Screenshot'],['Ansel'],['Mods']],right:[['Performance','Mic','Camera'],['Gallery'],['Settings']]}),getBlockTypes:()=>({InstantReplay:'side'})}};
 const document={querySelector:()=>null};vm.runInNewContext(js,{window:windows,document,JSON,Math},'main-menu-unified-native.js');
 return {api:windows.__dulukaUnifiedMainMenu,values,windows};
}
test('opt-in V4 is disabled by default and leaves all production V2 keys alone',()=>{
 const f=fake();assert.equal(f.api.enabled(),false);
 const before=f.api.get(),keys=['DulukaMainMenuLayoutV2','DulukaMainMenuSideRowsV1'];
 assert.equal(before.panels.left.length,3);assert.equal(before.panels.center.length,4);
 assert.equal(before.panels.right.length,3);
 for(const k of keys)assert.equal(f.values[k],undefined);
 assert.equal(f.api.storageKey,'DulukaMainMenuUnifiedV4');
});
test('V4 API lets Settings move everywhere and never delete, without touching old keys',()=>{
 const f=fake(),u=f.api;u.setEnabled(true);
 for(const p of M.PANELS){
   assert.equal(u.toBlock('Settings',p,0,'big'),true);
   assert.equal(M.locate(u.get(),'Settings').panel,p);
 }
 assert.equal(u.remove('Settings'),false);
 assert(M.locate(u.get(),'Settings'));
 assert.equal(f.values.DulukaMainMenuLayoutV2,undefined);
 assert.equal(f.values.DulukaMainMenuSideRowsV1,undefined);
 assert.equal(f.values.DulukaMainMenuUnifiedV4Enabled,'1');
});
test('one native V4 Block holds 9 controls and remains serialized across reloads',()=>{
 const f=fake(),u=f.api;u.setEnabled(true);u.toBlock('Settings','center',0,'slot');
 M.CONTROLS.filter(c=>c.key!=='Settings').slice(0,8).forEach(c=>assert(u.join(c.key,'center',0)));
 assert.equal(u.get().panels.center[0].controls.length,9);
 const before=JSON.stringify(u.get());assert.equal(u.join('Gallery','center',0),false);
 assert.equal(JSON.stringify(u.get()),before);
 assert(f.values.DulukaMainMenuUnifiedV4.includes('"version":4'));
});
test('V4 geometry moves the SAME twelve native tiles using native styles',()=>{
 const f=fake(),api=f.api;api.setEnabled(true);
 const tiles={},styles=()=>{const data={};return {
    getPropertyValue:k=>data[k]?.v||'',getPropertyPriority:k=>data[k]?.p||'',setProperty:(k,v,p)=>{data[k]={v,p}},
    get cssText(){return Object.entries(data).map(([k,x])=>k+':'+x.v).join(';')}
 }};
 function node(){const classes=new Set();return {
   style:styles(),attributes:{},classList:{
     add:k=>classes.add(k),remove:k=>classes.delete(k),contains:k=>classes.has(k)
   },setAttribute(k,v){this.attributes[k]=v},
   querySelector(){return null},querySelectorAll(){return []}
 }}
 for(const c of M.CONTROLS)tiles[c.name]=node();
 const bar=node();
 const root={querySelector:q=>q==='md-menu-bar'?bar:q.startsWith('md-menu.')?tiles[q.slice(8)]:null,
   getBoundingClientRect:()=>({width:1040})};
 const g=api.nativeGeometry(root);
 assert(g);assert.equal(g.cells.length,12);
 assert(bar.style.getPropertyValue('transform').includes('scale('));
 assert.equal(bar.classList.contains('nv-unified-bar'),true);
 assert.equal(tiles['nv-settings-menu-tile'].attributes['data-nv-unified-key'],'Settings');
 assert.equal(tiles['mic-tile'].classList.contains('nv-unified-tile'),true);
 for(const key in tiles)assert(tiles[key].style.cssText.includes('position:absolute'));
});
test('legacy native renderer only delegates geometry when V4 opt-in is enabled',()=>{
 assert(gpu.includes('unified&&unified.enabled()'));
 assert(gpu.includes('unified.nativeGeometry(root);return;'));
 assert(gpu.includes('window.__nvUnifiedV4RequestNativeSync=updateMainMenuPerformance'));
 assert(orig.includes('if(v4&&v4.enabled())'));
 assert(orig.includes('window.__dulukaUnifiedMainMenu.openEditor()'));
 const index=fs.readFileSync(path.join(__dirname,'../osc/index.html'),'utf8');
 assert(index.indexOf('main-menu-unified-model.js')<index.indexOf('main-menu-unified-native.js'));
});

test('V4 migration retains the saved Center Side type from the existing V2 editor',()=>{
 const f=fake();const s=f.api.get();
 assert.equal(s.panels.center[0].controls[0],'InstantReplay');
 assert.equal(s.panels.center[0].type,'side');
 assert.equal(f.values.DulukaMainMenuBlockTypesV1,undefined);
});

test('native renderer aligns the rail to the actual NVIDIA md-toolbar anchor',()=>{
  const f=fake(),api=f.api;api.setEnabled(true);
  let state=api.get();
  state=M.toBlock(state,'Settings','center',0,'big');
  M.CONTROLS.filter(x=>x.key!=='Settings').slice(0,8).forEach(x=>state=M.join(state,x.key,'center',0));
  api.set(state);
  const values={};
  const bar={
    offsetLeft:0,
    parentElement:{getBoundingClientRect:()=>({left:2202,width:960})},
    style:{getPropertyValue:k=>values[k]||'',getPropertyPriority:()=>'',setProperty:(k,v)=>{values[k]=v}},
    classList:{add(){}}
  };
  const root={querySelector:q=>q==='md-menu-bar'?bar:null,getBoundingClientRect:()=>({width:1040})};
  const g=api.nativeGeometry(root);
  assert.equal(g.cells.length,12);
  const tx=Number(/translateX\((-?[\d.]+)px\)/.exec(values.transform)[1]);
  assert(Math.abs(2202+tx-(1920-g.actualWidth)/2)<.01,
    'V4 must align to the translated NVIDIA toolbar, not assume the outer root x');
});
test('a CLOSED OSC Motion phase cannot be overridden by V4 layout sync',()=>{
  const f=fake(),api=f.api;api.setEnabled(true);
  f.windows.__dulukaOscMotion={phase:'idle',getState:()=>({target:'closed'})};
  const root={querySelector(){throw Error('Should not query native DOM while closed')}};
  assert.equal(api.nativeGeometry(root),null);
});

test('V4 editor supports pointer dragging, visible drop targets, undo and escape cancellation',()=>{
 assert(js.includes("wrap.addEventListener('mousedown',dragDown,true)"));
 assert(js.includes("document.addEventListener('mousemove',dragMove,true)"));
 assert(js.includes("document.elementFromPoint(event.clientX,event.clientY)"));
 assert(js.includes("M.moveBlock(s,from[0],Number(from[1]),panel,index)"));
 assert(js.includes("M.join(s,current.key,id[0],Number(id[1]))"));
 assert(js.includes("M.toBlock(s,current.key,panel,get().panels[panel].length,'side')"));
 assert(js.includes("event.key==='Escape'"));
 assert(js.includes("undoStack.pop()"));
 const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-unified-native.css'),'utf8');
 assert(css.includes('.nv-unified-drop-target'));
 assert(css.includes('.nv-unified-drag-ghost'));
});
