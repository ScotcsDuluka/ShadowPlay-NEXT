'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=path.resolve(__dirname,'../..');
const protectedFiles=['Module/Core/module-map.js','index.module.html','index.core-only.html','index.module-no-v5.html','index.module-no-motion.html'];
const snapshot=()=>protectedFiles.map(name=>{const file=path.join(root,name);return fs.existsSync(file)?fs.readFileSync(file):null});
const beforeImport=snapshot();
const {generate,presets,manifest,legacyAllowlist,lateAssets}=require('./build-profiles.cjs');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
// The pristine NVIDIA+mods bootstrap page. index.html is the generated
// production cutover page and must never be hand-edited.
const legacyName='index.legacy.html';
const original=fs.readFileSync(path.join(root,legacyName),'utf8');
const indexed=[...original.matchAll(/["'](\/mods\/[^"'?]+)(?:\?[^"']*)?["']/gi)].map(x=>x[1]);
const diagnosticPresets=Object.keys(presets).filter(n=>!presets[n].production);
test('every Module asset matches its recorded manifest hash',()=>{
 for(const a of manifest.assets){const dst=path.join(root,a.module.replace(/^\//,''));assert.equal(hash(fs.readFileSync(dst)),a.sha256,a.module)}
});
test('no original index asset is unowned and Module paths are unique',()=>{
 const map=new Map(manifest.assets.map(x=>[x.source,x]));assert.equal(map.size,manifest.assets.length);
 for(const src of indexed)assert(map.has(src),src);
 assert.equal(new Set(manifest.assets.map(x=>x.module)).size,manifest.assets.length);
});
test('every diagnostic profile preserves vendor/common/app dependency order, gate and NVIDIA main view',()=>{
 for(const name of diagnosticPresets){const {html}=generate(name);
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
 assert(!v5.includes('/Module/MainMenu/OSCFreestyleV5/'));
 assert(v5.includes('/Module/Overlay/Motion/')&&v5.includes('/Module/MainMenu/NVIDIA/'));
 assert(!motion.includes('/Module/Overlay/Motion/'));
 assert(motion.includes('/Module/MainMenu/OSCFreestyleV5/')&&motion.includes('/Module/MainMenu/NVIDIA/'));
 assert(all.includes('/Module/Overlay/Motion/')&&all.includes('/Module/MainMenu/OSCFreestyleV5/'));
});
test('production cutover page loads everything from Module and keeps NVIDIA bootstrap order',()=>{
 const html=generate('production').html;
 assert(!html.includes('/mods/'),'production page must not reference /mods/');
 assert(!html.includes('module-runtime'),'production page must not carry the diagnostic gate');
 assert(html.includes('src="/Module/Core/late-extensions.js"'));
 const refs=[...html.matchAll(/(?:src|href)=["']([^"']+)["']/gi)].map(x=>x[1]).filter(u=>u.startsWith('/Module/'));
 for(const r of refs)assert(fs.existsSync(path.join(root,r.split('?')[0].replace(/^\//,''))),r);
 const scripts=[...html.matchAll(/<script[^>]+src=["']([^"']+)["']/gi)].map(x=>x[1]);
 assert(scripts.indexOf('vendor.js')>=0&&scripts.indexOf('common.js')>scripts.indexOf('vendor.js')&&scripts.indexOf('app.js')>scripts.indexOf('common.js'));
 assert(html.includes('<div ui-view></div>')&&html.includes('ng-app="main"'));
});
test('deployed index.html is exactly the generated production page',()=>{
 const production=generate('production').html;
 const deployed=fs.readFileSync(path.join(root,'index.html'),'utf8');
 assert.equal(deployed,production,'index.html drifted from the generator; regenerate via node Module/Core/build-profiles.cjs');
 assert.equal(hash(fs.readFileSync(path.join(root,legacyName))),manifest.legacyIndexSha,'legacy rollback page changed; refresh manifest.legacyIndexSha only via --refresh-hashes');
});
test('late manifest covers exactly the retired host-injection set, in allowlist order',()=>{
 const late=lateAssets();
 assert.equal(late.length,16,'retired host-injection channel served 18 allowlist entries, 2 are statically referenced');
 assert.deepEqual(late.map(a=>a.name),legacyAllowlist.filter(n=>!['ansel-game-filter.js','gallery-media-source.js'].includes(n)));
 const manifestOnDisk=JSON.parse(fs.readFileSync(path.join(root,'Module/Core/late-manifest.json'),'utf8'));
 assert.deepEqual(manifestOnDisk.assets,late,'late-manifest.json is stale; regenerate via node Module/Core/build-profiles.cjs --late-manifest');
 for(const a of late)assert(fs.existsSync(path.join(root,a.module.replace(/^\//,''))),a.module);
 for(const a of late)assert(!original.includes(a.source+'"')&&!original.includes(a.source+'?'),'late asset is also statically referenced (double-load): '+a.source);
});
test('late-extensions loader preserves host-injection semantics without mods paths',()=>{
 const js=fs.readFileSync(path.join(root,'Module/Core/late-extensions.js'),'utf8');
 assert(!js.includes('data-nvmod'),'loader elements must not use data-nvmod or the host sync pass removes them');
 assert(!js.includes('setInterval'),'no 2s polling: production loads extensions once per page load');
 assert(js.includes('/Module/Core/late-manifest.json')&&js.includes("readyState === 'complete'")&&js.includes("addEventListener('load'"));
});
test('legacy Source index remains byte-identical to the recorded cutover baseline',()=>{
 const manifestOnDisk=JSON.parse(fs.readFileSync(path.join(root,'Module/module-manifest.json'),'utf8'));
 assert.equal(typeof manifestOnDisk.legacyIndexSha,'string','manifest must record the cutover legacy page hash');
 assert.equal(manifestOnDisk.sourceIndexSha,'18f7faadda33c8287faaa452c9357b3a8df0abce94280eafd50466e3e33a73a6','original 2026-10-26 migration forensic record must stay untouched');
});
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
 for(const file of ['module-runtime.js','module-map.js','build-profiles.cjs','late-extensions.js']){
  const r=cp.spawnSync(process.execPath,['--check',path.join(root,'Module/Core',file)],{encoding:'utf8'});
  assert.equal(r.status,0,file+': '+r.stderr);
 }
});
