'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../osc/Module/Overlay/Motion/osc-motion-timing.js'),'utf8');
const plain=value=>JSON.parse(JSON.stringify(value));
const defaults={enabled:false,mode:'osc',backgroundFade:true,topAnimation:true,
  mainMenuAnimation:true,openDuration:500,closeDuration:240};
function fixture(raw){
  const values=new Map([['DulukaNextNativeLayoutV5','USER LAYOUT'],['DulukaNextNativeLayoutV5Enabled','1']]);
  if(raw!==undefined)values.set('DulukaOscMotionSettings',raw);
  const writes=[];let fail=false;
  const window={localStorage:{getItem:key=>values.get(key)||null,setItem(key,value){
    if(fail)throw Error('Quota');writes.push([key,value]);values.set(key,value);
  }}};
  vm.runInNewContext(source,{window});
  return {api:window.__dulukaOscMotionSettings,timing:window.__dulukaOscMotionTiming,values,writes,fail(value){fail=value;}};
}
test('animation defaults expose defensive copies and do not write user storage on load',()=>{
  const f=fixture();assert.deepEqual(plain(f.api.get()),defaults);assert.equal(f.writes.length,0);
  const copy=f.api.get();copy.enabled=true;assert.equal(f.api.get().enabled,false);
  assert.equal(f.values.get('DulukaNextNativeLayoutV5'),'USER LAYOUT');
  assert.equal(f.api.error,'');
});
test('saved settings normalize independently without quantizing duration or rewriting stored text',()=>{
  const raw=JSON.stringify({enabled:false,mode:'default',backgroundFade:false,topAnimation:false,
    mainMenuAnimation:false,openDuration:337,closeDuration:9001});
  const f=fixture(raw);
  assert.deepEqual(plain(f.api.get()),{...defaults,enabled:false,mode:'default',backgroundFade:false,
    topAnimation:false,mainMenuAnimation:false,openDuration:337,closeDuration:2000});
  assert.equal(f.timing.open.duration,337);assert.equal(f.timing.closed.duration,2000);
  assert.equal(f.values.get(f.api.storageKey),raw);assert.equal(f.writes.length,0);
  for(const raw of ['{invalid','null','[]',JSON.stringify({mode:'unknown',enabled:'false',openDuration:'123'})])
    assert.deepEqual(plain(fixture(raw).api.get()),defaults);
});
test('failed persistence rolls back settings and compatibility timing without notifying subscribers',()=>{
  const f=fixture();let changes=0;f.api.subscribe(()=>changes++);f.fail(true);
  assert.equal(f.api.set({enabled:false,mode:'default',openDuration:1500}),false);
  assert.deepEqual(plain(f.api.get()),defaults);assert.equal(f.timing.open.duration,500);
  assert.equal(changes,0);assert.equal(f.values.has(f.api.storageKey),false);assert.ok(f.api.error);
  f.fail(false);assert.equal(f.api.set({openDuration:1500}),true);
  assert.equal(changes,1);assert.equal(f.api.error,'');assert.equal(f.timing.open.duration,1500);
  assert.equal(f.values.get('DulukaNextNativeLayoutV5'),'USER LAYOUT');
});
test('partial updates preserve other fields and subscriber errors or mutations cannot corrupt preferences',()=>{
  const f=fixture();let count=0;
  f.api.subscribe(()=>{throw Error('A UI observer failed');});
  const unsubscribe=f.api.subscribe(next=>{count++;next.enabled=true;});
  assert.equal(f.api.set({openDuration:49,closeDuration:999.7,mode:'default'}),true);
  assert.deepEqual(plain(f.api.get()),{...defaults,openDuration:50,closeDuration:1000,mode:'default'});
  assert.equal(count,1);unsubscribe();
  f.api.set({mode:'osc',topAnimation:false});assert.equal(count,1);
  assert.equal(f.api.get().topAnimation,false);assert.equal(f.api.get().enabled,false);
  assert(f.writes.every(([key])=>key==='DulukaOscMotionSettings'));
});
test('Reset restores500/240 and approved curves after a custom engine configuration',()=>{
  const f=fixture();const curves=plain({open:f.timing.open.bezier,closed:f.timing.closed.bezier});
  f.api.set({mode:'default',enabled:false,openDuration:50,closeDuration:2000,backgroundFade:false});
  assert.equal(f.api.reset(),true);assert.deepEqual(plain(f.api.get()),defaults);
  assert.equal(f.timing.open.duration,500);assert.equal(f.timing.closed.duration,240);
  assert.deepEqual(plain({open:f.timing.open.bezier,closed:f.timing.closed.bezier}),curves);
});
