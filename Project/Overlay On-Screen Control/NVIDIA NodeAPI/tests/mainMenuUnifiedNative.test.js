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
 assert(gpu.includes('unified.nativeGeometry(root);'));
 assert(gpu.includes('v4RouteFrames<12'),'route stabilization must remain bounded');
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

test('V4 preflight positions menu before Motion, but normal sync is frozen during entrance',()=>{
  const f=fake(),u=f.api;u.setEnabled(true);
  let phase='preparing',writes=0;
  f.windows.__dulukaOscMotion={get phase(){return phase;},getState:()=>({target:'open'})};
  const values={};
  const bar={offsetLeft:0,parentElement:{getBoundingClientRect:()=>({left:440,width:1040})},
    style:{getPropertyValue:k=>values[k]||'',getPropertyPriority:()=>'',setProperty:(k,v)=>{values[k]=v;writes++;}},
    classList:{add(){}},querySelector:()=>null};
  const root={querySelector:q=>q==='md-menu-bar'?bar:null,getBoundingClientRect:()=>({width:1040})};
  assert.equal(u.nativeGeometry(root),null,'regular sync cannot move tiles in preparing phase');
  const first=u.prepareForMotion(root);
  assert(first&&first.cells.length===12,'preflight must be allowed to layout before Motion capture');
  const baseline=values.transform,baselineWrites=writes;
  phase='opening';assert.equal(u.nativeGeometry(root),null);
  assert.equal(values.transform,baseline,'opening cannot move the native rail');
  assert.equal(writes,baselineWrites,'opening makes no geometry style writes');
  phase='idle';u.nativeGeometry(root);
  assert.equal(values.transform,baseline,'idle must keep the same anchored positions as preflight');
});

test('context menu edits the containing native Preview Block without showing Block frames',()=>{
 assert(js.includes("document.addEventListener('contextmenu',openBlockContext,true)"));
 assert(js.includes("document.documentElement.classList.contains('nv-live-layout-editing')"));
 assert(js.includes("event.target.closest('md-menu[data-nv-unified-key]')"));
 assert(js.includes("nativeMenu.hasAttribute('data-customize-preview')"));
 assert(js.includes("key=nativeTile.getAttribute('data-nv-unified-key')"));
 assert(js.includes("where=M.locate(get(),key)"));
 assert(js.includes("showBlockContext(where,key,event.clientX,event.clientY)"));
 assert(js.includes("M.changeType(s,panel,index,type)"));
 assert(js.includes("data-v4-context-type"));
 assert(js.includes("closeBlockContext()"));
 assert(js.includes("document.removeEventListener('contextmenu',openBlockContext,true)"));
 assert(js.includes("document.addEventListener('contextmenu',openBlockContext,true)"));
 assert(js.includes("data-v4-menu"));
 const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-unified-native.css'),'utf8');
 assert(css.includes('.nv-unified-context'));
 assert(css.includes('.nv-unified-context button[aria-checked="true"]'));
 const f=fake();f.api.setEnabled(true);
 const before=f.api.get(),where=M.locate(before,'Settings');
 f.api.changeType(where.panel,where.index,'slot');
 const after=f.api.get();
 assert.equal(after.panels[where.panel][where.index].type,'slot');
 assert.equal(M.locate(after,'Settings').panel,where.panel);
});

test('compact editor puts searchable NVIDIA Controls above the three panels',()=>{
 const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-unified-native.css'),'utf8');
 const controls=js.indexOf("'<div class=\"nv-unified-controls\"");
 const panels=js.indexOf("'<div class=\"nv-unified-panel-row\"");
 assert(controls>=0&&panels>controls,'the catalog must appear above the panel columns');
 assert(js.includes('nv-unified-block-meta'));
 assert(js.includes('data-v4-menu'));
 assert(js.includes('nv-unified-block-options'));
 assert(!js.includes("'<select aria-label=\"Block Type\""),'no per-block type dropdown');
 assert(!js.includes("data-v4-shift=\""),'no per-block move buttons');
 assert(!js.includes('Add selected to Block'),'no repeated action at the bottom of every Block');
 assert(css.includes('.nv-unified-block-options'));
 assert(css.includes('.nv-unified-guide-text'));
 assert(css.includes('.nv-unified-palette'));
 assert(css.includes('grid-template-columns:repeat(3,minmax(0,1fr))'));
});
test('same Block menu works from card options and native Preview without touching V2 keys',()=>{
 assert(js.includes('showBlockContext({panel:index[0],index:Number(index[1])}'));
 assert(js.includes("data-v4-context-panel"));
 assert(js.includes("data-v4-context-remove"));
 assert(js.includes("M.moveBlock(s,panel,index,destination,s.panels[destination].length)"));
 assert(js.includes("removeKey!=='Settings'"));
 assert(js.includes("M.remove(s,removeKey)"));
 assert(js.includes("document.addEventListener('contextmenu',openBlockContext,true)"));
 assert(js.includes("ribbon.addEventListener('wheel'"));
 const f=fake(),v4=f.api;v4.setEnabled(true);
 const first=v4.get();
 const moved=M.moveBlock(first,'left',0,'right',first.panels.right.length);
 const last=v4.set(moved);
 assert(M.validate(last));
 assert.equal(f.values.DulukaMainMenuLayoutV2,undefined);
});

test('V4 defers native geometry while a returned Main-menu has zero size',()=>{
 const f=fake(),u=f.api;u.setEnabled(true);
 const writes=[];
 const bar={style:{getPropertyValue:()=>'',getPropertyPriority:()=>'',setProperty:(k,v)=>writes.push([k,v])}};
 const root={getBoundingClientRect:()=>({width:0,height:0}),querySelector:selector=>selector==='md-menu-bar'?bar:null};
 assert.equal(u.nativeGeometry(root),null,'must ignore zero-sized route placeholder');
 assert.equal(writes.length,0,'do not write any incorrect transform during transient route');
});
test('V4 defers transform if toolbar parent is not laid out yet',()=>{
 const f=fake(),u=f.api;u.setEnabled(true);
 const values={};
 const bar={offsetLeft:0,parentElement:{getBoundingClientRect:()=>({left:0,width:0,height:0})},
 style:{getPropertyValue:k=>values[k]||'',getPropertyPriority:()=>'',setProperty:(k,v)=>{values[k]=v;}},
 classList:{add(){}}};
 const root={getBoundingClientRect:()=>({width:1040,height:340}),querySelector:q=>q==='md-menu-bar'?bar:null};
 assert.equal(u.nativeGeometry(root),null);
 assert.equal(values.transform,undefined,'never set translation against parent x=0 before layout');
});
test('first native V4 route revisits geometry on bounded animation frames',()=>{
 assert(gpu.includes('v4RouteRoot!==root'));
 assert(gpu.includes('v4RouteFrames<12'));
 assert(gpu.includes('window.requestAnimationFrame'));
 assert(gpu.includes('window.cancelAnimationFrame'));
 assert(js.includes('A zero-sized rect means its origin is still transient'));
});
