'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const install=require('../osc/mods/hotkey-live');
function deferred(){let resolve,reject;const promise=new Promise((r,j)=>{resolve=r;reject=j;});return{promise,resolve,reject};}
function fixture(){
 const saved={Screenshot:[18,112],RecordToggle:[18,120]},writes=[];
 const shadow={getHotkeyShortcut:name=>Promise.resolve({keys:saved[name].slice()}),setHotkeyShortcut:async(name,keys)=>{writes.push([name,keys.slice()]);saved[name]=keys.slice();}};
 const service={createHotKey(name){const binding={hotKeyName:name,hotKey:[],hotKeyCount:0,keyMatchCount:0,invalidSequence:false,starts:0,stops:0,startHotkeyDetection(){binding.starts++;return Promise.resolve();},stopHotKeyDetection(){binding.stops++;return Promise.resolve();}};shadow.getHotkeyShortcut(name).then(r=>{binding.hotKey=r.keys;binding.hotKeyCount=r.keys.length;});return binding;}};
 const services={shadowPlayService:shadow,hotkeyService:service,$q:{when:Promise.resolve.bind(Promise)}};
 const injector={get:name=>services[name]};
 return{saved,writes,shadow,service,injector,load:()=>install(injector)};
}
test('successful save updates all active bindings immediately and clears partial old sequences',async()=>{
 const f=fixture();f.load();const a=f.service.createHotKey('Screenshot'),b=f.service.createHotKey('Screenshot'),other=f.service.createHotKey('RecordToggle');
 await Promise.all([a.startHotkeyDetection(),b.startHotkeyDetection(),other.startHotkeyDetection()]);
 a.keyMatchCount=1;a.invalidSequence=true;await f.shadow.setHotkeyShortcut('Screenshot',[17,18,123]);
 assert.deepEqual(a.hotKey,[17,18,123]);assert.deepEqual(b.hotKey,[17,18,123]);assert.equal(a.hotKeyCount,3);assert.equal(a.keyMatchCount,0);assert.equal(a.invalidSequence,false);assert.deepEqual(other.hotKey,[18,120]);
 const wrapped=f.shadow.setHotkeyShortcut;f.load();assert.equal(f.shadow.setHotkeyShortcut,wrapped);
});
test('stopped bindings refresh on reactivation and stopped pending starts do not attach listeners',async()=>{
 const f=fixture();const slow=deferred();let gate=false;const read=f.shadow.getHotkeyShortcut;f.shadow.getHotkeyShortcut=name=>gate?slow.promise:read(name);f.load();const a=f.service.createHotKey('Screenshot');await a.startHotkeyDetection();await a.stopHotKeyDetection();
 await f.shadow.setHotkeyShortcut('Screenshot',[17,112]);assert.deepEqual(a.hotKey,[18,112]);await a.startHotkeyDetection();assert.deepEqual(a.hotKey,[17,112]);
 gate=true;const pending=a.startHotkeyDetection();await a.stopHotKeyDetection();const count=a.starts;slow.resolve({keys:[17,112]});await pending;assert.equal(a.starts,count);
});
test('old asynchronous reads cannot replace newly saved keys',async()=>{
 const f=fixture(),slow=deferred();f.shadow.getHotkeyShortcut=()=>slow.promise;f.load();const pending=f.shadow.getHotkeyShortcut('Screenshot');await f.shadow.setHotkeyShortcut('Screenshot',[17,18,123]);slow.resolve({keys:[18,112]});assert.deepEqual((await pending).keys,[17,18,123]);
});
test('fixed local UI keys do not read configurable hotkey settings',async()=>{
 const f=fixture();f.service.nonSPHK={anselHideUIHK:[45]};
 f.service.createHotKey=name=>({hotKeyName:name,hotKey:[45],starts:0,startHotkeyDetection(){this.starts++;return Promise.resolve();},stopHotKeyDetection(){return Promise.resolve();}});
 f.shadow.getHotkeyShortcut=()=>{throw Error('A fixed UI key must not call the settings API');};f.load();const binding=f.service.createHotKey('anselHideUIHK');await binding.startHotkeyDetection();assert.equal(binding.starts,1);assert.deepEqual(binding.hotKey,[45]);
});
test('same-action saves preserve order, snapshot input, and recover after failed saves',async()=>{
 const f=fixture(),first=deferred();let calls=0;f.shadow.setHotkeyShortcut=async(name,keys)=>{f.writes.push(keys.slice());if(++calls===1)await first.promise;if(keys[0]===99)throw Error('Rejected');f.saved[name]=keys.slice();};f.load();const binding=f.service.createHotKey('Screenshot');await binding.startHotkeyDetection();
 const keys=[17,112],one=f.shadow.setHotkeyShortcut('Screenshot',keys);keys[1]=113;const two=f.shadow.setHotkeyShortcut('Screenshot',[18,123]);await new Promise(r=>setImmediate(r));assert.deepEqual(f.writes,[[17,112]]);first.resolve();await Promise.all([one,two]);assert.deepEqual(f.saved.Screenshot,[18,123]);assert.deepEqual(binding.hotKey,[18,123]);
 await assert.rejects(f.shadow.setHotkeyShortcut('Screenshot',[99,123]));assert.deepEqual(binding.hotKey,[18,123]);await f.shadow.setHotkeyShortcut('Screenshot',[17,18,112]);assert.deepEqual(binding.hotKey,[17,18,112]);
});
