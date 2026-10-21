'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),M=require('../osc/mods/main-menu-unified-model');
const all=M.CONTROLS.map(c=>c.key);
function used(s){return M.PANELS.flatMap(p=>s.panels[p].flatMap(b=>b.controls));}
test('legacy native settings and side groups migrate with all twelve controls',()=>{
 const old={left:['Ansel','Screenshot','Mods'],center:['ManualRecord','Stream','InstantReplay','Broadcast']};
 const side={left:[['Screenshot','Ansel'],['Mods']],right:[['Performance','Mic','Camera'],['Gallery'],['Settings']]};
 const s=M.initial(old,side);
 assert.equal(s.version,4);assert.deepEqual(s.panels.left[0],{type:'slot',controls:['Screenshot','Ansel']});
 assert.deepEqual(s.panels.right[0].controls,['Performance','Mic','Camera']);
 assert.equal(used(s).length,12);assert.equal(new Set(used(s)).size,12);
});
test('every panel can receive any of twelve controls and any block type',()=>{
 let s=M.initial();
 for(const c of all){for(const p of M.PANELS){s=M.toBlock(s,c,p,s.panels[p].length,'big');assert.equal(M.locate(s,c).panel,p);}}
 assert.equal(used(s).length,12);
 for(const p of M.PANELS)for(const type of M.TYPES)if(s.panels[p].length){s=M.changeType(s,p,0,type);assert.equal(s.panels[p][0].type,type);}
});
test('one grouped block accepts nine controls, rejects tenth without changing source',()=>{
 let s=M.initial(),base=s;
 s=M.toBlock(s,'Settings','center',0,'slot');
 for(const c of all.filter(x=>x!=='Settings').slice(0,8))s=M.join(s,c,'center',0);
 assert.equal(s.panels.center[0].controls.length,9);assert.equal(M.blockHeight(s.panels.center[0]),240);
 assert.throws(()=>M.join(s,'Gallery','center',0),/full/);
 assert.equal(s.panels.center[0].controls.length,9);
 assert.equal(used(base).length,12);
});
test('Settings cannot be deleted but is movable and can change type',()=>{
 let s=M.initial();
 assert.throws(()=>M.remove(s,'Settings'),/cannot be removed/);
 for(const p of M.PANELS){s=M.toBlock(s,'Settings',p,0,'side');s=M.changeType(s,p,0,'big');assert.equal(M.locate(s,'Settings').panel,p);}
 assert(M.validate(s));
});
test('grouped grid is 2–3 icons per row and grows from 1 to 9 without overflow',()=>{
 for(let count=1;count<=9;count++){
   let s={version:4,panels:{left:[{type:'slot',controls:all.slice(0,count)}],center:[],right:[{type:'side',controls:['Settings']}]}};
   if(count===9&&s.panels.left[0].controls.indexOf('Settings')<0)M.validate(s);
   let a=M.layout(s,1000,650);assert.equal(a.cells.length,count+1);
   for(const tile of a.cells)assert(tile.x>=0&&tile.y>=0&&tile.x+tile.width<=a.width+.1&&tile.y+tile.height<=a.height+.1);
   assert(a.actualWidth<=1000&&a.actualHeight<=650);
 }
});
test('nine icons in Big Block fill 3x3 without stretching each icon outside 80x80 cell',()=>{
 let s=M.initial(),n=0;
 for(const key of all.slice(0,9)){if(!n++)s=M.toBlock(s,key,'center',0,'big');else s=M.join(s,key,'center',0);}
 let l=M.layout(s,1366,768),center=l.cells.filter(x=>x.panel==='center');
 assert.equal(center.length,9);assert(center.every(x=>x.width===80&&x.height===80&&x.variant==='small'));
});
test('adaptive layout fits multiple screens, including when one panel has all 12 controls',()=>{
 let s=M.initial();
 for(const c of all)s=M.toBlock(s,c,'left',s.panels.left.length,'big');
 for(const [w,h] of [[3840,2160],[1920,1200],[1680,1050],[1280,720],[960,540],[420,220]]){
   const g=M.layout(s,w,h);
   assert(g.scale>0&&g.scale<=1);
   assert(g.actualWidth<=w+.001&&g.actualHeight<=h+.001);
   assert(g.cells.every(x=>x.x+x.width<=g.width+.001&&x.y+x.height<=g.height+.001));
 }
});
test('normalization deduplicates, preserves extra controls and repairs Settings',()=>{
 const s=M.normalize({panels:{left:[{type:'slot',controls:[...all.slice(0,11),...all.slice(0,5)]}],center:[],right:[]}});
 assert.equal(s.panels.left[0].controls.length,9);
 assert.equal(s.panels.left[1].controls.length,2);
 assert(M.locate(s,'Settings'));assert.equal(new Set(used(s)).size,used(s).length);
});
test('moving/reordering blocks never mutates old layout objects',()=>{
 const a=M.initial(),raw=JSON.stringify(a),b=M.reorder(a,'center',0,a.panels.center.length-1);
 assert.notDeepEqual(b.panels.center,a.panels.center);
 assert.equal(JSON.stringify(a),raw);
});

test('legacy migration preserves intentionally removed optional Controls',()=>{
 const legacy={left:['Screenshot'],center:['ManualRecord']},
       rows={left:[['Screenshot']],right:[['Settings']]};
 const s=M.initial(legacy,rows);
 assert.deepEqual(used(s),['Screenshot','ManualRecord','Settings']);
 assert.equal(M.locate(s,'Mic'),null);
 assert.equal(M.locate(s,'Settings').panel,'right');
});

test('dragging a complete Block between panels preserves its nine native Controls',()=>{
 let s=M.initial();
 s=M.toBlock(s,'Settings','center',0,'big');
 M.CONTROLS.filter(c=>c.key!=='Settings').slice(0,8).forEach(c=>s=M.join(s,c.key,'center',0));
 const before=JSON.stringify(s);
 const moved=M.moveBlock(s,'center',0,'left',0);
 assert.equal(moved.panels.left[0].controls.length,9);
 assert.equal(moved.panels.left[0].type,'big');
 assert.equal(M.locate(moved,'Settings').panel,'left');
 assert.equal(JSON.stringify(s),before);
 assert.equal(new Set(used(moved)).size,used(moved).length);
});
