'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const updater=fs.readFileSync(path.join(__dirname,'v4-live-updater.cjs'),'utf8');
test('V4 Updater does not open Customize automatically when Alt+Z opens the overlay',()=>{
  assert.doesNotMatch(updater,/__dulukaStartMainMenuEditor\s*\(/);
  assert.match(updater,/info\.route==='main\.main-menu'&&info\.editor&&info\.editing/);
  assert.match(updater,/document\.documentElement\.classList\.contains\('nv-live-layout-editing'\)/);
});
test('V4 updater keeps passive CSS watch and safe JS apply features',()=>{
  assert.match(updater,/await watchUpdates\(\)/);
  assert.match(updater,/window\.__nvV4ApplyRequested/);
  assert.match(updater,/safe\.record==='NotRecording'/);
  assert.match(updater,/Fetch\.enable/);
});
