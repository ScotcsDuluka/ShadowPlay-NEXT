'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const sync=require('../tools/osc-live-preview.cjs');
function fixture(fn) {
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'osc-live-preview-'));
 const src=path.join(dir,'project','osc'),dest=path.join(dir,'build','osc'),base=path.join(dir,'snapshots');
 fs.mkdirSync(path.join(src,'mods'),{recursive:true});
 fs.mkdirSync(path.join(dest,'mods'),{recursive:true});
 fs.writeFileSync(path.join(src,'index.html'),'<html>Project</html>');
 fs.writeFileSync(path.join(dest,'index.html'),'<html>Build</html>');
 fs.writeFileSync(path.join(src,'mods','osc-motion.js'),'/* PROJECT MOTION */');
 fs.writeFileSync(path.join(dest,'mods','osc-motion.js'),'/* BUILD MOTION */');
 fs.writeFileSync(path.join(dest,'mods','launcher-language.json'),'{"language":"th-TH"}');
 fs.writeFileSync(path.join(dest,'mods','special-bridge.js'),'/* PRESERVE EXTRA */');
 const files=['index.html','mods/osc-motion.js'];
 try{return fn({src,dest,base,files});}finally{fs.rmSync(dir,{recursive:true,force:true});}
}
test('Plan is read-only and reports changed file SHA-256',()=>{
 fixture(({src,dest,files})=>{
  const before=fs.readFileSync(path.join(dest,'index.html'),'utf8');
  const p=sync.plan({source:src,runtime:dest,files});
  assert.equal(p.changed.length,2);
  assert.equal(p.changed[0].existed,true);
  assert.equal(p.changed[0].sourceHash.length,64);
  assert.equal(fs.readFileSync(path.join(dest,'index.html'),'utf8'),before);
 });
});
test('Apply makes complete backups, updates only selected frontend files',()=>{
 fixture(({src,dest,base,files})=>{
  const r=sync.deploy({source:src,runtime:dest,base,files,skipGit:true});
  assert.equal(r.result,'APPLIED');assert.equal(r.changed,2);
  assert.equal(fs.readFileSync(path.join(dest,'index.html'),'utf8'),'<html>Project</html>');
  assert.equal(fs.readFileSync(path.join(dest,'mods','osc-motion.js'),'utf8'),'/* PROJECT MOTION */');
  assert.equal(fs.readFileSync(path.join(dest,'mods','launcher-language.json'),'utf8'),'{"language":"th-TH"}');
  assert.equal(fs.readFileSync(path.join(dest,'mods','special-bridge.js'),'utf8'),'/* PRESERVE EXTRA */');
  const manifest=JSON.parse(fs.readFileSync(path.join(r.folder,'manifest.json'),'utf8'));
  assert.equal(manifest.status,'applied');assert.equal(manifest.files.length,2);
  assert.equal(fs.readFileSync(path.join(r.folder,'files','index.html'),'utf8'),'<html>Build</html>');
  const rolled=sync.rollback(r.session,{base,runtime:dest});
  assert.equal(rolled.result,'ROLLED_BACK');
  assert.equal(fs.readFileSync(path.join(dest,'index.html'),'utf8'),'<html>Build</html>');
  assert.equal(fs.readFileSync(path.join(dest,'mods','osc-motion.js'),'utf8'),'/* BUILD MOTION */');
  assert.equal(fs.readFileSync(path.join(dest,'mods','launcher-language.json'),'utf8'),'{"language":"th-TH"}');
  assert.equal(sync.listSessions(base).at(0).status,'rolled-back');
 });
});
test('Rollback refuses to overwrite external runtime modifications',()=>{
 fixture(({src,dest,base,files})=>{
  const r=sync.deploy({source:src,runtime:dest,base,files,skipGit:true});
  fs.writeFileSync(path.join(dest,'index.html'),'<html>Modified by other process</html>');
  assert.throws(()=>sync.rollback(r.session,{base,runtime:dest}),/changed externally/);
  assert.equal(fs.readFileSync(path.join(dest,'mods','osc-motion.js'),'utf8'),'/* PROJECT MOTION */');
  assert.equal(fs.readFileSync(path.join(dest,'index.html'),'utf8'),'<html>Modified by other process</html>');
 });
});
test('New source file disappears on rollback, unrelated runtime files stay',()=>{
 fixture(({src,dest,base,files})=>{
  fs.writeFileSync(path.join(src,'mods','new-control.js'),'NEW');
  const r=sync.deploy({source:src,runtime:dest,base,files:files.concat('mods/new-control.js'),skipGit:true});
  assert.equal(fs.readFileSync(path.join(dest,'mods','new-control.js'),'utf8'),'NEW');
  sync.rollback(r.session,{base,runtime:dest});
  assert.equal(fs.existsSync(path.join(dest,'mods','new-control.js')),false);
  assert.equal(fs.existsSync(path.join(dest,'mods','special-bridge.js')),true);
 });
});
test('Relative path safety rejects traversal and Windows paths',()=>{
 for(const v of ['../index.html','mods/../../bad.js','C:/Windows/win.ini','/etc/passwd','mods\\x.js']) {
  assert.throws(()=>sync.safeRelative(v));
 }
});
test('No-change apply creates no new session',()=>{
 fixture(({src,dest,base,files})=>{
  fs.copyFileSync(path.join(src,'index.html'),path.join(dest,'index.html'));
  fs.copyFileSync(path.join(src,'mods','osc-motion.js'),path.join(dest,'mods','osc-motion.js'));
  const r=sync.deploy({source:src,runtime:dest,base,files,skipGit:true});
  assert.equal(r.result,'NO_CHANGES');assert.equal(sync.listSessions(base).length,0);
 });
});
