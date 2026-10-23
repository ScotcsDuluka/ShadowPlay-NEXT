'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const cp=require('node:child_process');
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

function gitFixture(fn) {
 const repo=fs.mkdtempSync(path.join(os.tmpdir(),'osc-live-preview-git-'));
 const src=path.join(repo,...sync.defaults.ROOT_PREFIX.split('/'));
 const dest=path.join(repo,'fixture-runtime'),base=path.join(repo,'fixture-snapshots');
 const git=args=>cp.execFileSync('git',['-C',repo,...args],{encoding:'utf8',windowsHide:true,stdio:['ignore','pipe','pipe']}).trim();
 fs.mkdirSync(path.join(src,'mods'),{recursive:true});
 fs.mkdirSync(path.join(dest,'mods'),{recursive:true});
 fs.writeFileSync(path.join(repo,'.gitattributes'),'*.js text eol=crlf\n');
 fs.writeFileSync(path.join(src,'mods','owned.js'),'/* COMMITTED OWNED */\r\n');
 fs.writeFileSync(path.join(src,'mods','other.js'),'/* COMMITTED OTHER */\r\n');
 fs.writeFileSync(path.join(src,'config.js'),'/* LOCAL CONFIG */\n');
 fs.writeFileSync(path.join(src,'mods','launcher-language.json'),'{}\n');
 fs.writeFileSync(path.join(dest,'mods','owned.js'),'/* OLD OWNED */\n');
 fs.writeFileSync(path.join(dest,'mods','other.js'),'/* PRESERVE OTHER RUNTIME */\n');
 try {
  git(['init','--initial-branch=Stable']);
  git(['config','user.name','Preview Test']);git(['config','user.email','preview-test@example.invalid']);
  git(['add','--all']);git(['commit','-m','Fixture']);
  git(['checkout-index','--all','--force']);
  const files=['mods/owned.js'];
  return fn({repo,src,dest,base,files,git,options:{repo,source:src,runtime:dest,base,files}});
 } finally { fs.rmSync(repo,{recursive:true,force:true}); }
}

test('Targeted committed deploy accepts CRLF normalization and preserves unrelated dirty OSC files',()=>{
 gitFixture(({src,dest,files,git,options})=>{
  fs.writeFileSync(path.join(src,'mods','owned.js'),'/* COMMITTED OWNED */\r\n');
  fs.writeFileSync(path.join(src,'mods','other.js'),'/* OTHER CHAT UNSAVED */\r\n');
  const before=fs.readFileSync(path.join(dest,'mods','other.js'),'utf8');
  const commit=sync.projectPreflight(files,options);
  assert.equal(commit,git(['rev-parse','HEAD']));
  assert.throws(()=>sync.projectPreflight(undefined,options),/not committed/,'default preflight remains strict across all OSC files');
  const result=sync.deploy(options);
  assert.equal(result.result,'APPLIED');assert.equal(result.changed,1);
  assert.equal(fs.readFileSync(path.join(dest,'mods','owned.js'),'utf8'),'/* COMMITTED OWNED */\r\n');
  assert.equal(fs.readFileSync(path.join(dest,'mods','other.js'),'utf8'),before);
  assert.equal(fs.readFileSync(path.join(src,'mods','other.js'),'utf8'),'/* OTHER CHAT UNSAVED */\r\n');
  const manifest=JSON.parse(fs.readFileSync(path.join(result.folder,'manifest.json'),'utf8'));
  assert.deepEqual(manifest.selection,files);
  assert.equal(manifest.commit,commit);
  assert.equal(manifest.files[0].sourceHash,sync.hash(path.join(src,'mods','owned.js')));
  assert.equal(fs.readFileSync(path.join(result.folder,'files','mods','owned.js'),'utf8'),'/* OLD OWNED */\n');
 });
});

test('Targeted preflight rejects dirty selected files, untracked files, excluded config and unsafe paths',()=>{
 gitFixture(({src,dest,options})=>{
  const before=sync.hash(path.join(dest,'mods','owned.js'));
  fs.writeFileSync(path.join(src,'mods','owned.js'),'/* SELECTED DIRTY */\r\n');
  assert.throws(()=>sync.deploy(options),/not committed/);
  assert.equal(sync.hash(path.join(dest,'mods','owned.js')),before);
  assert.equal(sync.listSessions(options.base).length,0);
  fs.writeFileSync(path.join(src,'mods','new.js'),'/* UNTRACKED */');
  assert.throws(()=>sync.projectPreflight(['mods/new.js'],options),/not committed/);
  for(const file of ['config.js','CONFIG.JS','mods/launcher-language.json','../owned.js','mods/owned.js.bak'])
   assert.throws(()=>sync.projectPreflight([file],options),/allowed OSC|Unsafe file/);
  assert.throws(()=>sync.projectPreflight([],options),/at least one/);
 });
});

test('Targeted preflight verifies committed blobs even if Git status hides a selected edit',()=>{
 gitFixture(({src,git,options})=>{
  git(['update-index','--assume-unchanged',sync.defaults.ROOT_PREFIX+'mods/owned.js']);
  fs.writeFileSync(path.join(src,'mods','owned.js'),'/* MASKED UNCOMMITTED */\r\n');
  assert.equal(git(['status','--porcelain','--',sync.defaults.ROOT_PREFIX+'mods/owned.js']),'');
  assert.throws(()=>sync.deploy(options),/does not match committed HEAD/);
 });
});

test('Selected deploy detects source edits made after planning and preserves its backup',()=>{
 gitFixture(({src,dest,options})=>{
  const copy=fs.copyFileSync;
  fs.copyFileSync=function(from,to,...args){
   const result=copy.call(this,from,to,...args);
   if(from===path.join(dest,'mods','owned.js'))
    fs.writeFileSync(path.join(src,'mods','owned.js'),'/* EDITED DURING BACKUP */\r\n');
   return result;
  };
  try { assert.throws(()=>sync.deploy(options),/Source edited during deploy/); }
  finally { fs.copyFileSync=copy; }
  assert.equal(fs.readFileSync(path.join(dest,'mods','owned.js'),'utf8'),'/* OLD OWNED */\n');
  assert.equal(sync.listSessions(options.base)[0].status,'interrupted');
 });
});

test('Selected deploy still requires Stable and cannot use skipGit with real Runtime paths',()=>{
 gitFixture(({git,options})=>{
  git(['switch','-c','another-branch']);
  assert.throws(()=>sync.deploy(options),/non-Stable/);
 });
 assert.throws(()=>sync.deploy({skipGit:true,files:['mods/owned.js']}),/isolated test fixtures/);
});
