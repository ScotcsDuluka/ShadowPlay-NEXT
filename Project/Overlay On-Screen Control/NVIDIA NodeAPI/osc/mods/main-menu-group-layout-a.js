(function(root,factory){'use strict';var api=factory();if(typeof module==='object'&&module.exports)module.exports=api;if(root)root.NvGroupLayoutA=api;})(typeof window!=='undefined'?window:null,function(){
'use strict';
var PANELS=['left','center','right'],TYPES=['small','side','big'];
var LAYOUTS={small:['three','wide-left','wide-right','full'],side:['full','text-left','text-right'],big:['full']};
var WIDTH=240,ROW=80;
function copy(x){return JSON.parse(JSON.stringify(x));}
function layoutKeys(type){return LAYOUTS[type]||[];}
function shapes(type,preset){
 var d={
  small:{three:[1/3,1/3,1/3],'wide-left':[2/3,1/3],'wide-right':[1/3,2/3],full:[1]},
  side:{full:[1],'text-left':[3/4,1/4],'text-right':[1/4,3/4]},
  big:{full:[1]}
 };
 return d[type]&&d[type][preset]||null;
}
function makeBlock(type,preset,controls){
 if(TYPES.indexOf(type)<0||layoutKeys(type).indexOf(preset)<0)throw Error('Unknown Block preset');
 var widths=shapes(type,preset);
 if(!Array.isArray(controls)||controls.length!==widths.length)throw Error('Preset requires '+widths.length+' Slots');
 return {type:type,preset:preset,slots:controls.map(function(key){return key||null;})};
}
function group(blocks){return {blocks:copy(blocks||[])};}
function empty(){return {version:5,panels:{left:[],center:[],right:[]}};}
function validate(state){
 if(!state||state.version!==5||!state.panels)throw Error('Invalid group layout version');
 var seen={};PANELS.forEach(function(panel){
  if(!Array.isArray(state.panels[panel]))throw Error('Missing panel '+panel);
  state.panels[panel].forEach(function(g){
   if(!g||!Array.isArray(g.blocks)||!g.blocks.length)throw Error('Group must have Blocks');
   var rows=0;
   g.blocks.forEach(function(b){
    var widths=shapes(b.type,b.preset);
    if(!widths||!Array.isArray(b.slots)||b.slots.length!==widths.length)throw Error('Invalid Block layout');
    rows+=b.type==='big'?3:1;
    b.slots.forEach(function(key){
     if(key==null)return;
     if(typeof key!=='string'||seen[key])throw Error('Duplicate or invalid Control');
     seen[key]=true;
    });
   });
   if(rows>3)throw Error('Group exceeds 240px height');
  });
 });
 if(!seen.Settings)throw Error('Settings cannot be removed');
 return true;
}
function layout(state){
 validate(state);var out={panels:{},cells:[]};
 PANELS.forEach(function(panel){
  var groups=state.panels[panel];
  out.panels[panel]={groups:groups.length,width:WIDTH,height:groups.length*WIDTH};
  groups.forEach(function(g,gi){
   var y=gi*WIDTH;
   g.blocks.forEach(function(b,bi){
    var widths=shapes(b.type,b.preset),h=b.type==='big'?WIDTH:ROW,x=0;
    widths.forEach(function(fraction,si){
     var width=WIDTH*fraction,key=b.slots[si];
     if(key)out.cells.push({key:key,panel:panel,group:gi,block:bi,slot:si,
       type:b.type,preset:b.preset,x:x,y:y,width:width,height:h,
       variant:b.type==='big'?'big':b.type==='side'&&fraction>.5?'row':'small'});
     x+=width;
    });
    y+=h;
   });
  });
 });
 return out;
}
function setSlot(state,panel,groupIndex,blockIndex,slotIndex,key){
 var next=copy(state),g=next.panels[panel]&&next.panels[panel][groupIndex],b=g&&g.blocks[blockIndex];
 if(!b||slotIndex<0||slotIndex>=b.slots.length)throw Error('Slot not found');
 PANELS.forEach(function(p){next.panels[p].forEach(function(group){group.blocks.forEach(function(block){block.slots=block.slots.map(function(k){return k===key?null:k;});});});});
 if(b.slots[slotIndex]==='Settings'&&key!=='Settings')throw Error('Settings cannot be removed');
 b.slots[slotIndex]=key||null;validate(next);return next;
}
function migrateV4(v4){
 var result=empty(),seen={};
 if(!v4||!v4.panels)throw Error('V4 layout required');
 PANELS.forEach(function(p){
  (v4.panels[p]||[]).forEach(function(old){
   // Migration does NOT overwrite V4: each native Control is preserved,
   // split into one full-width Side/Big or up to three Small Slots.
   var keys=(old.controls||[]).filter(function(k){if(seen[k])return false;seen[k]=true;return true;});
   if(old.type==='slot'){
    while(keys.length){var slice=keys.splice(0,3);var preset=slice.length===3?'three':slice.length===2?'wide-left':'full';
      var slots=slice.length===2?[slice[0],slice[1]]:slice;
      result.panels[p].push(group([makeBlock('small',preset,slots)]));}
   } else {
    keys.forEach(function(key){result.panels[p].push(group([makeBlock(old.type==='big'?'big':'side','full',[key])]));});
   }
  });
 });
 if(!seen.Settings)result.panels.right.push(group([makeBlock('side','full',['Settings'])]));
 validate(result);return result;
}
return {version:5,PANELS:PANELS,TYPES:TYPES,LAYOUTS:LAYOUTS,WIDTH:WIDTH,ROW:ROW,
 makeBlock:makeBlock,group:group,empty:empty,validate:validate,layout:layout,
 migrateV4:migrateV4,setSlot:setSlot,shapes:shapes};
});
