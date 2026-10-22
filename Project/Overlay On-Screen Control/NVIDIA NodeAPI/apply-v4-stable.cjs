'use strict';
const fs=require('fs'),path=require('path');
const dir=path.join(__dirname,'osc');
function patch(rel,pairs){
 const file=path.join(dir,rel),orig=fs.readFileSync(file,'utf8');let txt=orig.replace(/\r\n/g,'\n');
 for(const [a,b] of pairs){
  if(!txt.includes(a))throw Error('Anchor not present in '+rel+': '+a.slice(0,85));
  if(txt.split(a).length!==2)throw Error('Anchor ambiguous in '+rel+': '+a.slice(0,85));
  txt=txt.replace(a,b);
 }
 fs.writeFileSync(file,txt);console.log('PATCHED',rel);
}
patch('mods/gpu-profiles.js',[
 ["  function syncNativeMainMenuSlots(root) {\n    var api=window.__dulukaMenuLayout;",
  "  function syncNativeMainMenuSlots(root) {\n    var unified=window.__dulukaUnifiedMainMenu;\n    if(unified&&unified.enabled()){unified.nativeGeometry(root);return;}\n    var api=window.__dulukaMenuLayout;"],
 ["    var visibility = window.__dulukaMainMenuVisibility || {};",
  "    var visibility = window.__dulukaMainMenuVisibility || {};\n    var unified=window.__dulukaUnifiedMainMenu;\n    if(unified&&unified.enabled()){var present=unified.visible(),next={Preferences:!!present.Settings};unified.model.CONTROLS.forEach(function(c){next[c.key]=!!present[c.key];});visibility=next;}"],
 ["  function registerPerformanceSupportListener() {",
  "  window.__nvUnifiedV4RequestNativeSync=updateMainMenuPerformance;\n\n  function registerPerformanceSupportListener() {"]
]);
patch('mods/main-menu-live.js',[
 ["self.extraRows=function(){return Math.max(0,self.sides.right.length-3)*80;};",
  "self.extraRows=function(){return window.__dulukaUnifiedMainMenu&&window.__dulukaUnifiedMainMenu.enabled()?0:Math.max(0,self.sides.right.length-3)*80;};"],
 ["self.variant=function(tile){var key=self.key(tile);var groups=self.sides;",
  "self.variant=function(tile){var key=self.key(tile),v4=window.__dulukaUnifiedMainMenu;if(v4&&v4.enabled()){var cell=v4.cell(key);return cell?cell.variant:'row';}var groups=self.sides;"],
 ["self.start=function(){self.editing=true;editingMotion(true);wantsEditing=false;self.loadDesktopMonitors();update();};",
  "self.start=function(){self.editing=true;editingMotion(true);wantsEditing=false;self.loadDesktopMonitors();update();if(window.__dulukaUnifiedMainMenu&&window.__dulukaUnifiedMainMenu.enabled())$window.setTimeout(function(){window.__dulukaUnifiedMainMenu.openEditor();},0);};"]
]);
patch('index.html',[
 ['<link rel="stylesheet" href="/mods/main-menu-live.css?v=20261021-live">',
  '<link rel="stylesheet" href="/mods/main-menu-live.css?v=20261021-live"><link rel="stylesheet" href="/mods/main-menu-unified-native.css?v=20261022-v4">'],
 ['<script type="text/javascript" src="/mods/main-menu-live.js?v=20261021-live"></script>',
  '<script type="text/javascript" src="/mods/main-menu-live.js?v=20261021-live"></script><script type="text/javascript" src="/mods/main-menu-unified-model.js?v=20261022-v4"></script><script type="text/javascript" src="/mods/main-menu-unified-native.js?v=20261022-v4"></script>']
]);
