'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=path.resolve(__dirname,'../osc');
function read(rel){return fs.readFileSync(path.join(root,rel),'utf8')}
test('OSC loads NVIDIA archive Customize V2 without experimental V4/Group Editor UI',()=>{
 const index=read('index.html');
 assert.match(index, /src="\/mods\/main-menu-live\.js/);
 assert.match(index, /href="\/mods\/main-menu-live\.css/);
 for(const mod of ['main-menu-unified-native.js','main-menu-unified-native.css',
 'main-menu-group-editor.js','main-menu-group-editor.css','main-menu-group-model.js']) {
   assert.equal(index.includes('/mods/'+mod),false,'Legacy experimental UI must stay unmounted: '+mod);
 }
});
test('source keeps supported newer infrastructure and NEXT model without changing default UI',()=>{
 const index=read('index.html');
 for(const mod of ['launcher-language-sync.js','osc-ui-localization.js',
 'osc-motion.js','nv-layout-v5-core.js','nv-next-v5-native.js'])assert.ok(index.includes('/mods/'+mod),mod);
 const native=read('mods/nv-next-v5-native.js');
 assert.match(native,/DulukaNextNativeLayoutV5Enabled/);
 assert.match(native,/read\(FLAG\) === '1'/);
});
test('archive-style Customize uses original three-panel NVIDIA editor and native md-select',()=>{
 const src=read('mods/main-menu-live.js'),css=read('mods/main-menu-live.css');
 assert.match(src,/nv-main-menu-live-editor/);
 assert.match(src,/ng-model="flexMenu\.desktopMonitor"/);
 assert.match(src,/class="nv-live-workspace"/);
 assert.match(css,/\.nv-live-workspace\{display:flex/);
 assert.equal(css.includes('Native Settings shell V5:'),false);
});
