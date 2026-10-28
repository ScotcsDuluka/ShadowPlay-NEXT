'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=path.resolve(__dirname,'../..');
const protectedFiles=['Module/Core/module-map.js','index.module.html','index.core-only.html','index.module-no-v5.html','index.module-no-motion.html'];
const snapshot=()=>protectedFiles.map(name=>{const file=path.join(root,name);return fs.existsSync(file)?fs.readFileSync(file):null});
const beforeImport=snapshot();
const {generate,presets,manifest}=require('./build-profiles.cjs');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const original=fs.readFileSync(path.join(root,'index.html'),'utf8'),indexed=[...original.matchAll(/["'](\/mods\/[^"'?]+)(?:\?[^"']*)?["']/gi)].map(x=>x[1]);
test('every Module asset copy is byte-identical to the Source',()=>{
 for(const a of manifest.assets){const src=path.join(root,a.source.replace(/^\//,'')),dst=path.join(root,a.module.replace(/^\//,''));assert.equal(hash(fs.readFileSync(src)),a.sha256,a.source);assert.equal(hash(fs.readFileSync(dst)),a.sha256,a.module)}
});
test('no original index asset is unowned and Module paths are unique',()=>{
 const map=new Map(manifest.assets.map(x=>[x.source,x]));assert.equal(map.size,manifest.assets.length);
 for(const src of indexed)assert(map.has(src),src);
 assert.equal(new Set(manifest.assets.map(x=>x.module)).size,manifest.assets.length);
});
test('every profile preserves vendor/common/app dependency order and NVIDIA main view',()=>{
 for(const name of Object.keys(presets)){const {html}=generate(name);
 const refs=[...html.matchAll(/<script[^>]+src=["']([^"']+)["']/gi)].map(x=>x[1]);
 assert(refs.indexOf('vendor.js')>=0&&refs.indexOf('common.js')>refs.indexOf('vendor.js')&&refs.indexOf('app.js')>refs.indexOf('common.js'),name);
 assert(html.includes('<div ui-view></div>')&&html.includes('ng-app="main"'),name);
 assert(refs.includes('/Module/Core/module-map.js')&&refs.some(x=>x.startsWith('/Module/Core/module-runtime.js?profile=')),name);
 }
});
test('all profile preserves original script and stylesheet order exactly after URL unmapping',()=>{
 const {html}=generate('all');
 let normalized=html;
 for(const a of manifest.assets)normalized=normalized.split(a.module).join(a.source);
 normalized=normalized.replace(/\s*<script src="\/Module\/Core\/module-map\.js"><\/script>/,'');
 normalized=normalized.replace(/\s*<script src="\/Module\/Core\/module-runtime\.js\?profile=all&omit="><\/script>/,'');
 assert.equal(normalized,original);
 const refs=[...html.matchAll(/["'](\/Module\/[^"'?]+)(?:\?[^"']*)?["']/gi)].map(x=>x[1]);
 for(const r of refs)assert(fs.existsSync(path.join(root,r.replace(/^\//,''))),r);
});
test('core-only strips all static mods and optional camera test script',()=>{
 const {html,activeAssets}=generate('core-only');assert.equal(activeAssets,0);
 assert(!/<(?:script|link)\b[^>]*(?:src|href)=["']\/mods\//i.test(html));
 assert(!html.includes('src="/nvcamera-test-mode.js"'));
 assert(html.includes('vendor.js')&&html.includes('common.js')&&html.includes('app.js'));
});
test('no-V5 and no-Motion disable only named groups',()=>{
 const read=name=>generate(name).html;
 const v5=read('without-v5'),motion=read('without-motion'),all=read('all');
 assert(!v5.includes('/Module/MainMenu/FreestyleV5/'));
 assert(v5.includes('/Module/Overlay/Motion/')&&v5.includes('/Module/MainMenu/NVIDIA/'));
 assert(!motion.includes('/Module/Overlay/Motion/'));
 assert(motion.includes('/Module/MainMenu/FreestyleV5/')&&motion.includes('/Module/MainMenu/NVIDIA/'));
 assert(all.includes('/Module/Overlay/Motion/')&&all.includes('/Module/MainMenu/FreestyleV5/'));
});
test('legacy Source index remains byte-identical to pre-migration snapshot',()=>assert.equal(hash(fs.readFileSync(path.join(root,'index.html'))),manifest.sourceIndexSha));
test('import and profile inspection never rewrite existing diagnostic artifacts',()=>{
 for(const name of Object.keys(presets))generate(name);
 assert.deepEqual(snapshot(),beforeImport);
});
test('Core Only intercepts DOM-inserted extension scripts but leaves core scripts untouched',()=>{
 const vm=require('node:vm'),js=fs.readFileSync(path.join(root,'Module/Core/module-runtime.js'),'utf8');
 function fake(profile){
  const changes=[];
  class ElementNode{
   constructor(tag,url){this.nodeType=1;this.tagName=tag;this.url=url;}
   getAttribute(name){return name==='src'||name==='href'?this.url:null;}
   appendChild(node){changes.push({method:'append',url:node.url});return node;}
   insertBefore(node){changes.push({method:'insert',url:node.url});return node;}
   replaceChild(node,previous){changes.push({method:'replace',url:node.url});return previous;}
  }
  const document={baseURI:'http://localhost:59011/index.core-only.html',currentScript:{src:'http://localhost:59011/Module/Core/module-runtime.js?profile='+profile+'&omit='},scripts:[]};
  const window={__spnModuleOwners:{'/mods/osc-motion.js':'overlay.motion','/Module/Overlay/Motion/osc-motion.js':'overlay.motion'}};
  vm.runInNewContext(js,{window,document,Node:ElementNode,URL});
  const target=new ElementNode('DIV',''),extension=new ElementNode('SCRIPT','/mods/osc-motion.js'),core=new ElementNode('SCRIPT','app.js');
  target.appendChild(extension);target.appendChild(core);
  return{changes,audit:window.__spnModuleAudit.snapshot()};
 }
 const blocked=fake('core-only');assert.deepEqual(blocked.changes.map(x=>x.url),['app.js']);assert.equal(blocked.audit.blocked.length,1);
 const allowed=fake('all');assert.deepEqual(allowed.changes.map(x=>x.url),['/mods/osc-motion.js','app.js']);assert.equal(allowed.audit.blocked.length,0);
});
test('generated Module Core scripts parse without SyntaxErrors',()=>{
 const cp=require('node:child_process');
 for(const file of ['module-runtime.js','module-map.js','build-profiles.cjs']){
  const r=cp.spawnSync(process.execPath,['--check',path.join(root,'Module/Core',file)],{encoding:'utf8'});
  assert.equal(r.status,0,file+': '+r.stderr);
 }
});

