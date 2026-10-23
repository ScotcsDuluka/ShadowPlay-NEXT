'use strict';
const test=require('node:test'),a=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const dir=path.join(__dirname,'../osc');
const m=require(path.join(dir,'mods/classic-group-v5-model.js'));
const fresh=()=>m.defaults();
function count(l,k){return m.geometry(l).filter(x=>x.key===k).length}
test('Exactly twelve original controls and Settings once',()=>{const x=fresh();a(m.validate(x));a.equal(m.geometry(x).filter(c=>c.key).length,12);for(const k of m.CATALOG)a.equal(count(x,k),1);});
test('Group size 240x240, blocks fill at most three 80px rows',()=>{const x=fresh();a.equal(m.BASE,240);a.equal(m.ROW,80);for(const p of ['left','center','right'])for(const g of x.panels[p])a.equal(m.units(g),3)});
test('Small layouts: thirds/twoLeft/twoRight/full',()=>{
 a.deepEqual(m.LAYOUTS.small,{thirds:[80,80,80],twoLeft:[160,80],twoRight:[80,160],full:[240]});
});
test('Side layouts: full/detailLeft/detailRight',()=>{a.deepEqual(m.LAYOUTS.side,{full:[240],detailLeft:[180,60],detailRight:[60,180]});});
test('Big layouts: 1 control, 240px',()=>{a.deepEqual(m.LAYOUTS.big,{full:[240]});});
test('Correct horizontal slot geometry and row height',()=>{
 let x=m.replaceBlock(fresh(),'left','left-1',0,'small','thirds');
 a.deepEqual(m.geometry(x).filter(v=>v.groupId==='left-1'&&v.blockIndex===0).map(v=>[v.x,v.y,v.width,v.height]),[[0,0,80,80],[80,0,80,80],[160,0,80,80]]);
});
test('Big cannot coexist with other rows',()=>{a.throws(()=>m.addBlock(fresh(),'left','left-1','big','full'),/full/);});
test('One Big completely fills an empty Group',()=>{
 let x=m.addGroup(fresh(),'left'),id=x.panels.left.at(-1).id;x=m.addBlock(x,'left',id,'big','full');
 a.equal(m.units(x.panels.left.at(-1)),3);a.throws(()=>m.addBlock(x,'left',id,'small','full'),/full/);
});
test('Three rows fit and a fourth does not',()=>{
 let x=m.addGroup(fresh(),'center'),id=x.panels.center.at(-1).id;
 for(let i=0;i<3;i++)x=m.addBlock(x,'center',id,'side','full');
 a.throws(()=>m.addBlock(x,'center',id,'small','full'),/full/);
});
test('Unlimited Groups have unique IDs',()=>{
 let x=fresh();for(let i=0;i<20;i++)x=m.addGroup(x,'center');
 a.equal(x.panels.center.length,24);a.equal(new Set(x.panels.center.map(y=>y.id)).size,24);
});
test('Moving control swaps without duplication',()=>{
 let x=m.assign(fresh(),'Settings','left','left-1',0,0);
 a.equal(count(x,'Settings'),1);a.equal(count(x,'Screenshot'),1);a.equal(m.locate(x,'Screenshot').panel,'right');
});
test('Assigning to an empty slot moves, not clones',()=>{
 let x=m.addGroup(fresh(),'left'),id=x.panels.left.at(-1).id;x=m.addBlock(x,'left',id,'small','thirds');
 x=m.assign(x,'Gallery','left',id,0,2);a.equal(count(x,'Gallery'),1);a.equal(m.locate(x,'Gallery').slot,2);
});
test('Never replace, delete or remove Group with Settings',()=>{
 let x=fresh();a.throws(()=>m.assign(x,'Gallery','right','right-1',2,0),/Settings/);
 a.throws(()=>m.removeControl(x,'Settings'),/Settings/);
 a.throws(()=>m.removeGroup(x,'right','right-1'),/Settings/);
 a.throws(()=>m.removeBlock(x,'right','right-1',2),/Settings/);
});
test('Block layout conversions preserve keys or reject truncation',()=>{
 let x=m.replaceBlock(fresh(),'right','right-1',1,'side','detailRight');
 a.equal(count(x,'Gallery'),1);a.deepEqual(m.geometry(x).filter(c=>c.groupId==='right-1'&&c.blockIndex===1).map(c=>c.width),[60,180]);
 a.throws(()=>m.replaceBlock(x,'right','right-1',0,'side','full'),/few slots/);
});
test('Remove control creates empty slot and can restore it',()=>{
 let x=m.removeControl(fresh(),'Mic');a.equal(count(x,'Mic'),0);
 x=m.assign(x,'Mic','right','right-1',1,0);a.equal(count(x,'Mic'),1);
});
test('Cross-panel Group move and same-panel reorder',()=>{
 let x=m.moveGroup(fresh(),'left','left-1','right',1);a.equal(x.panels.right[1].id,'left-1');
 x=m.moveGroup(x,'center','center-1','center',3);a.deepEqual(x.panels.center.map(g=>g.id),['center-2','center-3','center-4','center-1']);
});
test('Failed and successful changes cannot mutate previous snapshots',()=>{
 let x=fresh(),y=m.clone(x);a.throws(()=>m.addBlock(x,'left','left-1','big','full'));
 m.assign(x,'Settings','left','left-1',0,0);a.deepEqual(x,y);
});
test('V2 config untouched, uses separate V5 storage key',()=>{
 const kv={DulukaMainMenuLayoutV2:'keep'},storage={getItem:k=>kv[k],setItem:(k,v)=>{kv[k]=v}},store=m.createStore(storage);
 a(store.change(m.addGroup(store.get(),'right')).ok);a.equal(kv.DulukaMainMenuLayoutV2,'keep');a(kv[m.KEY]);
});
test('Storage failure leaves active layout unchanged',()=>{
 const storage={getItem:()=>null,setItem:()=>{throw Error('Disk full')}},store=m.createStore(storage),old=store.get();
 a.equal(store.change(m.addGroup(old,'right')).ok,false);a.deepEqual(store.get(),old);
});
test('Invalid persisted state reverts to safe defaults',()=>{
 const storage={getItem:()=>'{\"version\":5,\"panels\":{}}',setItem:()=>{}};
 a.deepEqual(m.createStore(storage).get(),fresh());
});
test('Listener notifications only for committed changes',()=>{
 const kv={},storage={getItem:k=>kv[k],setItem:(k,v)=>{kv[k]=v}},store=m.createStore(storage);let n=0;
 const off=store.subscribe(()=>n++);a(store.change(m.addGroup(store.get(),'right')).ok);a.equal(n,1);off();
 a(store.change(m.addGroup(store.get(),'right')).ok);a.equal(n,1);
});
test('V2 migrations retain original left/center order, ensure Settings',()=>{
 let x=m.fromLegacy({left:['Screenshot','Mods'],center:['ManualRecord','Stream']});
 a(m.validate(x));a.equal(m.locate(x,'ManualRecord').panel,'center');a.equal(count(x,'Settings'),1);
});
