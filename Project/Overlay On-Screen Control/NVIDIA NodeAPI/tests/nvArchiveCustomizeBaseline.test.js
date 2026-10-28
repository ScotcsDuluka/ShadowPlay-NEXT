'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=path.resolve(__dirname,'../osc');
function read(rel){return fs.readFileSync(path.join(root,rel),'utf8')}
test('OSC loads NVIDIA archive Customize V2 without experimental V4/Group Editor UI',()=>{
 const index=read('index.html');
 assert.match(index, /src="\/Module\/MainMenu\/NVIDIA\/main-menu-live\.js/);
 assert.match(index, /href="\/Module\/MainMenu\/NVIDIA\/main-menu-live\.css/);
 const unmounted={
  'main-menu-unified-native.js':'/Module/MainMenu/LegacyV4/main-menu-unified-native.js',
  'main-menu-unified-native.css':'/Module/MainMenu/LegacyV4/main-menu-unified-native.css',
  'main-menu-group-editor.js':'/Module/MainMenu/GroupEditor/main-menu-group-editor.js',
  'main-menu-group-editor.css':'/Module/MainMenu/GroupEditor/main-menu-group-editor.css',
  'main-menu-group-model.js':'/Module/MainMenu/GroupEditor/main-menu-group-model.js'};
 for(const mod of Object.keys(unmounted)) {
   assert.equal(index.includes('/mods/'+mod),false,'Legacy experimental UI must stay unmounted: '+mod);
   assert.equal(index.includes(unmounted[mod]),false,'Legacy experimental UI must stay unmounted: '+mod);
 }
});
test('source keeps supported newer infrastructure and NEXT model without changing default UI',()=>{
 const index=read('index.html');
 const mounted={
  'launcher-language-sync.js':'/Module/Base/Localization/launcher-language-sync.js',
  'osc-ui-localization.js':'/Module/Base/Localization/osc-ui-localization.js',
  'osc-motion.js':'/Module/Overlay/Motion/osc-motion.js',
  'nv-layout-v5-core.js':'/Module/MainMenu/FreestyleV5/nv-layout-v5-core.js',
  'nv-next-v5-native.js':'/Module/MainMenu/FreestyleV5/nv-next-v5-native.js'};
 for(const mod of Object.keys(mounted))assert.ok(index.includes(mounted[mod]),mod);
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
