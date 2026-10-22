(function(root,factory){
 'use strict';
 var api=factory();
 if(typeof module==='object'&&module.exports)module.exports=api;
 if(root)root.NvGroupLayoutA=api;
})(typeof window!=='undefined'?window:null,function(){
 'use strict';
 var PANELS=['left','center','right'];
 var PRESETS={
   small:{triple:[1,1,1],wideLeft:[2,1],wideRight:[1,2],single:[3]},
   side:{single:[3],detailLeft:[3,1],detailRight:[1,3]},
   big:{single:[3]}
 };
 var LABELS={small:'Small',side:'Side',big:'Big'};
 var CONTROL_KEYS=['Screenshot','Ansel','Mods','InstantReplay','ManualRecord','Stream','Broadcast',
                   'Performance','Mic','Camera','Gallery','Settings'];
 var names={};CONTROL_KEYS.forEach(function(k){names[k]=true;});
 var blockKinds=Object.keys(PRESETS);
 function copy(a){return JSON.parse(JSON.stringify(a));}
 function makeBlock(kind,preset,slots){
   if(!PRESETS[kind]||!PRESETS[kind][preset])throw Error('Unknown Block preset');
   var width=PRESETS[kind][preset].length;
   return {kind:kind,preset:preset,slots:Array.from({length:width},function(_,i){return (slots||[])[i]||null;})};
 }
 function makeGroup(id,blocks){return {id:String(id),blocks:blocks||[]};}
 function isFilled(group){return group.blocks.some(function(b){return b.slots.some(Boolean);});}
 function newLayout(){return {version:5,panels:{left:[],center:[],right:[]}};}
 function capacity(block){return PRESETS[block.kind][block.preset].length;}
 function rows(group){return group.blocks.reduce(function(n,b){return n+(b.kind==='big'?3:1);},0);}
 function find(s,key){
   for(var p=0;p<PANELS.length;p++){var panel=PANELS[p],groups=s.panels[panel];
    for(var g=0;g<groups.length;g++)for(var b=0;b<groups[g].blocks.length;b++){
      var sl=groups[g].blocks[b].slots.indexOf(key);
      if(sl>=0)return {panel:panel,group:g,block:b,slot:sl};
    }
   }return null;
 }
 function validate(s){
   if(!s||s.version!==5||!s.panels)throw Error('Expected Group layout V5');
   var used={},ids={};
   PANELS.forEach(function(p){
     if(!Array.isArray(s.panels[p]))throw Error('Missing panel '+p);
     s.panels[p].forEach(function(g){
       if(!g||!g.id||ids[g.id])throw Error('Duplicate or missing Group ID');
       ids[g.id]=true;
       if(!Array.isArray(g.blocks)||!g.blocks.length||rows(g)>3)throw Error('Group must occupy 1–3 rows');
       if(g.blocks.some(function(b){return b.kind==='big';})&&g.blocks.length!==1)throw Error('Big Block fills the Group');
       g.blocks.forEach(function(b){
         if(!PRESETS[b.kind]||!PRESETS[b.kind][b.preset]||!Array.isArray(b.slots)||b.slots.length!==capacity(b))
           throw Error('Unknown Block preset or Slot count');
         b.slots.forEach(function(k){if(k===null)return;if(!names[k]||used[k])throw Error('Duplicate/unknown Control '+k);used[k]=true;});
       });
     });
   });
   if(!used.Settings)throw Error('Settings cannot be removed');
   return true;
 }
 function transact(s,action){var a=copy(s);action(a);validate(a);return a;}
 function nextId(s){var ids={};PANELS.forEach(function(p){s.panels[p].forEach(function(g){ids[g.id]=true;});});var n=1;while(ids['g'+n])n++;return 'g'+n;}
 function fromV4(v4){
   var s=newLayout(),seen={},id=0;
   PANELS.forEach(function(p){
     (v4&&v4.panels&&v4.panels[p]||[]).forEach(function(b){
       var kind=b.type==='slot'?'small':b.type==='big'?'big':'side',remaining=(b.controls||[]).filter(function(k){
         if(!names[k]||seen[k])return false;seen[k]=true;return true;
       });
       while(remaining.length){
         var pack=remaining.splice(0,kind==='big'?1:kind==='small'?3:2);
         var preset=kind==='big'?'single':kind==='small'?(pack.length===3?'triple':pack.length===2?'wideLeft':'single'):(pack.length===2?'detailLeft':'single');
         var groups=s.panels[p],previous=groups[groups.length-1],next=makeBlock(kind,preset,pack);
         // Adjacent compact rows share a 240x240 Group. Big occupies it all.
         if(kind!=='big'&&previous&&rows(previous)<3&&
            !previous.blocks.some(function(x){return x.kind==='big';}))previous.blocks.push(next);
         else groups.push(makeGroup('g'+(++id),[next]));
       }
     });
   });
   if(!seen.Settings)s.panels.right.push(makeGroup('g'+(++id),[makeBlock('side','single',['Settings'])]));
   validate(s);return s;
 }
 function createGroup(s,p,kind,preset,index){
   if(PANELS.indexOf(p)<0)throw Error('Unknown panel');
   kind=kind||'small';preset=preset|| (kind==='big'?'single':kind==='side'?'single':'triple');
   return transact(s,function(a){var groups=a.panels[p],g=makeGroup(nextId(a),[makeBlock(kind,preset)]);
     groups.splice(index===undefined?groups.length:Math.max(0,Math.min(groups.length,index)),0,g);
   });
 }
 function addBlock(s,p,gi,kind,preset){
   kind=kind||'small';preset=preset|| (kind==='big'?'single':kind==='side'?'single':'triple');
   return transact(s,function(a){var g=a.panels[p]&&a.panels[p][gi];if(!g)throw Error('Group not found');
     g.blocks.push(makeBlock(kind,preset));
   });
 }
 function choosePreset(s,p,gi,bi,kind,preset){
   return transact(s,function(a){var g=a.panels[p]&&a.panels[p][gi],b=g&&g.blocks[bi];if(!b)throw Error('Block missing');
     var replacement=makeBlock(kind,preset),controls=b.slots.filter(Boolean);
     if(controls.length>replacement.slots.length)throw Error('Move Controls before reducing Slots');
     controls.forEach(function(k,i){replacement.slots[i]=k;});g.blocks[bi]=replacement;
   });
 }
 function setSlot(s,key,p,gi,bi,si){
   if(!names[key])throw Error('Unknown Control');
   return transact(s,function(a){
     var g=a.panels[p]&&a.panels[p][gi],b=g&&g.blocks[bi];
     if(!b||!Number.isInteger(si)||si<0||si>=b.slots.length)throw Error('Slot missing');
     var prev=find(a,key),displaced=b.slots[si];
     if(displaced===key)return;
     if(displaced&&!prev)throw Error('Choose an empty Slot or move an existing Control first');
     if(prev)a.panels[prev.panel][prev.group].blocks[prev.block].slots[prev.slot]=displaced||null;
     b.slots[si]=key;
   });
 }
 function emptySlot(s,p,gi,bi,si){
   return transact(s,function(a){
     var b=a.panels[p]&&a.panels[p][gi]&&a.panels[p][gi].blocks[bi];
     if(!b||si<0||si>=b.slots.length)throw Error('Slot missing');
     if(b.slots[si]==='Settings')throw Error('Settings cannot be removed');
     b.slots[si]=null;
   });
 }
 function moveGroup(s,p,gi,target,index){
   return transact(s,function(a){
     if(PANELS.indexOf(target)<0)throw Error('Invalid destination');
     var source=a.panels[p];if(!source||!source[gi])throw Error('Group missing');
     var g=source.splice(gi,1)[0],to=a.panels[target];
     // Unspecified index means append. Explicit indices refer to positions
     // before removing the source Group from its original Panel.
     var at=index===undefined?to.length:Math.max(0,Math.min(index,to.length+(target===p?1:0)));
     if(target===p&&index!==undefined&&at>gi)at--;
     to.splice(Math.min(at,to.length),0,g);
   });
 }
 function removeGroup(s,p,gi){
   return transact(s,function(a){var g=a.panels[p]&&a.panels[p][gi];if(!g)throw Error('Group missing');
     if(isFilled(g))throw Error('Move Controls out of Group first');
     a.panels[p].splice(gi,1);
   });
 }
 function slots(s){var list=[];PANELS.forEach(function(p){s.panels[p].forEach(function(g){g.blocks.forEach(function(b){b.slots.forEach(function(k){if(k)list.push(k);});});});});return list;}
 function geometry(s,viewW,viewH){
   validate(s);
   viewW=Math.max(200,Number(viewW)||1920);viewH=Math.max(150,Number(viewH)||1080);
   var gap=28,parts={},width=0,maxHeight=240;
   PANELS.forEach(function(p){
     var count=s.panels[p].length,cols=Math.min(4,Math.max(1,count)),height=Math.ceil(count/cols)*240;
     parts[p]={cols:cols,width:count?cols*240:0,height:count?height:0};
     if(count){if(width)width+=gap;width+=cols*240;}
     maxHeight=Math.max(maxHeight,height);
   });
   var scale=Math.min(1,(viewW-64)/Math.max(1,width),(viewH-40)/maxHeight);
   var x=0,cells=[],panelBoxes={};
   PANELS.forEach(function(p){
     var part=parts[p];panelBoxes[p]={x:x,width:part.width,height:part.height,groups:[]};
     s.panels[p].forEach(function(g,i){
       var gx=x+(i%part.cols)*240,gy=Math.floor(i/part.cols)*240,dy=0;
       panelBoxes[p].groups.push({id:g.id,x:gx,y:gy,width:240,height:240});
       g.blocks.forEach(function(b,bi){
         var h=b.kind==='big'?240:80,w=PRESETS[b.kind][b.preset],sum=w.reduce(function(a,v){return a+v;},0),left=0;
         b.slots.forEach(function(k,si){var cw=240*w[si]/sum;if(k)cells.push({key:k,x:gx+left,y:gy+dy,width:cw,height:h,variant:b.kind==='big'?'big':b.kind==='side'&&(b.preset==='single'||((b.preset==='detailLeft'&&si===0)||(b.preset==='detailRight'&&si===1)))?'row':'small',panel:p,group:g.id,block:bi,slot:si});left+=cw;});
         dy+=h;
       });
     });
     if(part.width){x+=part.width+gap;}
   });
   return {width:width,height:maxHeight,scale:scale,actualWidth:width*scale,actualHeight:maxHeight*scale,cells:cells,panels:panelBoxes};
 }
 return {version:5,PANELS:PANELS,PRESETS:PRESETS,LABELS:LABELS,CONTROL_KEYS:CONTROL_KEYS,
   fromV4:fromV4,createGroup:createGroup,addBlock:addBlock,choosePreset:choosePreset,
   setSlot:setSlot,emptySlot:emptySlot,moveGroup:moveGroup,removeGroup:removeGroup,validate:validate,clone:copy,
   find:find,slots:slots,rows:rows,geometry:geometry};
});
