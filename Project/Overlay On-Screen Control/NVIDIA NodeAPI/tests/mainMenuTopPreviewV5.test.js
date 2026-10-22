'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const M=require('../osc/mods/main-menu-unified-model'),G=require('../osc/mods/main-menu-group-model');
const native=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-unified-native.js'),'utf8');
const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-live.css'),'utf8');

function fixture(editing){
 let s=G.fromV4(M.initial());
 for(let i=0;i<5;i++)s=G.createGroup(s,'left','small','triple');
 const data={};
 const styles=()=>{const values={};return {
    getPropertyValue:k=>values[k]||'',getPropertyPriority:()=>'',setProperty:(k,v)=>{values[k]=v;}
 }};
 const el=()=>{const cls=new Set();return {style:styles(),
   classList:{add:k=>cls.add(k),remove:k=>cls.delete(k),contains:k=>cls.has(k)},
   setAttribute(){},querySelector(){return null;},getBoundingClientRect:()=>({left:200,top:160,width:200,height:80})}};
 const tiles={};M.CONTROLS.forEach(c=>tiles[c.name]=el());
 const bar=el();bar.offsetLeft=0;bar.offsetTop=0;
 bar.parentElement={getBoundingClientRect:()=>({left:200,top:160,width:1500,height:700})};
 const root={querySelector:q=>q==='md-menu-bar'?bar:q.startsWith('md-menu.')?tiles[q.slice(8)]:null,
   getBoundingClientRect:()=>({left:0,top:0,width:1040,height:700})};
 const editorHost={getBoundingClientRect:()=>({left:40,top:40,width:1840,height:1120})};
 const document={documentElement:{classList:{contains:cls=>editing&&cls==='nv-live-layout-editing'}},
    querySelector:sel=>sel==='.nv-live-editor'&&editing?editorHost:null};
 const storage={DulukaMainMenuUnifiedV4Enabled:'1',DulukaMainMenuGroupLayoutAEnabled:'1'};
 const window={innerWidth:1920,innerHeight:1200,NvUnifiedLayoutV4:M,
   __dulukaGroupLayoutA:{enabled:()=>true,get:()=>s,model:G},
   localStorage:{getItem:k=>storage[k]||null,setItem:(k,v)=>{storage[k]=v},removeItem:k=>delete storage[k]},
   getComputedStyle:()=>({getPropertyValue:k=>k==='--nv-top-preview-height'?'380px':'',paddingTop:'402px'})};
 vm.runInNewContext(native,{window,document,JSON,Math},'main-menu-unified-native.js');
 return {api:window.__dulukaUnifiedMainMenu,root,window,state:s,menuOriginY:160,shellStartY:420};
}

test('when editing V5, native 2-row group preview fits fully above the opaque Settings shell',()=>{
 const f=fixture(true);
 const g=f.api.nativeGeometry(f.root);
 assert(g&&g.height>=480,'fixture has at least two 240px rows');
 assert(f.menuOriginY+g.actualHeight<=f.shellStartY-4,
   'all native tiles must fit inside clickable top viewing strip (actual bottom '+(f.menuOriginY+g.actualHeight)+')');
});

test('outside Customize, V5 native geometry may use regular fullscreen height',()=>{
 const f=fixture(false),g=f.api.nativeGeometry(f.root);
 assert(g&&g.actualHeight>250,'normal menu should not be unnecessarily compressed');
});

test('Customize native viewing strip is a shared dimension in CSS and JavaScript',()=>{
 assert.match(css,/--nv-top-preview-height\s*:\s*380px/);
 assert.match(css,/background\s*:\s*linear-gradient\([\s\S]*?var\(--nv-top-preview-height\)/);
 assert.match(native,/--nv-top-preview-height/);
});
