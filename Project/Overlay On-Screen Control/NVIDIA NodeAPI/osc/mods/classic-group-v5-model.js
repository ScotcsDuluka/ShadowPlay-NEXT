/* Classic NVIDIA OSC Group V5 model — independent of the legacy V2 renderer.
 * One Group is always a 240x240 cell. Big occupies 3 row units;
 * Small/Side each occupies one 240x80 row, with configurable horizontal slots.
 * Browser/CEF ES5 and Node-compatible; all edits are transactional. */
(function(root,factory){
  var api=factory();
  if(typeof module==='object'&&module.exports)module.exports=api;
  if(root)root.NvClassicGroupV5=api;
})(typeof window!=='undefined'?window:null,function(){
  'use strict';
  var KEY='DulukaClassicGroupV5Layout',BASE=240,ROW=80,PANELS=['left','center','right'];
  var CATALOG=['Screenshot','Ansel','Mods','InstantReplay','ManualRecord','Stream','Broadcast',
               'Performance','Mic','Camera','Gallery','Settings'];
  var LAYOUTS={
    small:{thirds:[80,80,80],twoLeft:[160,80],twoRight:[80,160],full:[240]},
    side:{full:[240],detailLeft:[180,60],detailRight:[60,180]},
    big:{full:[240]}
  };
  function clone(x){return JSON.parse(JSON.stringify(x));}
  function fail(msg){throw new Error(msg);}
  function isKey(k){return CATALOG.indexOf(k)>=0;}
  function slotWidths(type,layout){return LAYOUTS[type]&&LAYOUTS[type][layout];}
  function block(type,layout,keys){
    if(!slotWidths(type,layout))fail('Unsupported '+type+'/'+layout);
    var count=slotWidths(type,layout).length,cs=keys||[];
    if(cs.length>count)fail('Too many controls in '+type+' block');
    var out=[];for(var i=0;i<count;i++)out.push(cs[i]||null);
    return {type:type,layout:layout,controls:out};
  }
  function group(id,blocks){return{id:id,blocks:blocks||[]};}
  function empty(){return{version:5,panels:{left:[],center:[],right:[]}};}
  function defaults(){
    var out=empty();
    out.panels.left=[group('left-1',[
      block('small','full',['Screenshot']),
      block('small','full',['Ansel']),
      block('small','full',['Mods'])
    ])];
    ['InstantReplay','ManualRecord','Stream','Broadcast'].forEach(function(k,i){
      out.panels.center.push(group('center-'+(i+1),[block('big','full',[k])]));
    });
    out.panels.right=[group('right-1',[
      block('small','thirds',['Performance','Mic','Camera']),
      block('side','full',['Gallery']),
      block('side','full',['Settings'])
    ])];
    return out;
  }
  function units(group){
    var used=0;group.blocks.forEach(function(b){used+=b.type==='big'?3:1;});return used;
  }
  function validate(raw){
    if(!raw||raw.version!==5||!raw.panels)fail('Layout version must be 5');
    var seen={},ids={};
    PANELS.forEach(function(panel){
      var groups=raw.panels[panel];if(!Array.isArray(groups))fail('Missing '+panel+' panel');
      groups.forEach(function(g){
        if(!g||typeof g.id!=='string'||!/^[-_a-zA-Z0-9]{1,64}$/.test(g.id)||ids[g.id])fail('Invalid/duplicate group id');
        ids[g.id]=true;
        if(!Array.isArray(g.blocks)||g.blocks.length>3||units(g)>3)fail('Group exceeds 240x240');
        g.blocks.forEach(function(b){
          var sizes=slotWidths(b.type,b.layout);
          if(!sizes||!Array.isArray(b.controls)||b.controls.length!==sizes.length)fail('Invalid block slots');
          b.controls.forEach(function(k){
            if(k===null)return;
            if(!isKey(k)||seen[k])fail('Unknown/duplicate control: '+k);
            seen[k]=true;
          });
        });
      });
    });
    if(!seen.Settings)fail('Settings cannot be removed');
    return true;
  }
  function allSlots(layout){
    var result=[];PANELS.forEach(function(p){layout.panels[p].forEach(function(g,gi){
      g.blocks.forEach(function(b,bi){b.controls.forEach(function(k,si){
        result.push({panel:p,group:gi,groupId:g.id,block:bi,slot:si,key:k});
      });});
    });});return result;
  }
  function locate(layout,key){
    var list=allSlots(layout);for(var i=0;i<list.length;i++)if(list[i].key===key)return list[i];return null;
  }
  function nextId(layout,prefix){
    var idx=1,ids=allGroups(layout).map(function(g){return g.id;});
    while(ids.indexOf(prefix+'-'+idx)>=0)idx++;return prefix+'-'+idx;
  }
  function allGroups(layout){return [].concat(layout.panels.left,layout.panels.center,layout.panels.right);}
  function edit(layout,callback){
    validate(layout);
    var result=clone(layout);callback(result);validate(result);return result;
  }
  function addGroup(layout,panel){
    if(PANELS.indexOf(panel)<0)fail('Unknown panel');
    return edit(layout,function(s){s.panels[panel].push(group(nextId(s,panel),[]));});
  }
  function removeGroup(layout,panel,id){
    if(PANELS.indexOf(panel)<0)fail('Unknown panel');
    return edit(layout,function(s){
      var found=s.panels[panel].filter(function(g){return g.id===id;})[0];
      if(!found)fail('Group not found');
      if(found.blocks.some(function(b){return b.controls.indexOf('Settings')>=0;}))fail('Settings cannot be removed');
      s.panels[panel]=s.panels[panel].filter(function(g){return g.id!==id;});
    });
  }
  function addBlock(layout,panel,id,type,form){
    return edit(layout,function(s){
      if(PANELS.indexOf(panel)<0)fail('Unknown panel');
      var g=s.panels[panel].filter(function(x){return x.id===id;})[0];
      if(!g)fail('Group not found');
      var b=block(type,form);
      if(units(g)+(type==='big'?3:1)>3)fail('Group is full');
      g.blocks.push(b);
    });
  }
  function replaceBlock(layout,panel,id,index,type,form){
    return edit(layout,function(s){
      if(PANELS.indexOf(panel)<0)fail('Unknown panel');
      var g=s.panels[panel].filter(function(x){return x.id===id;})[0];
      if(!g||!g.blocks[index])fail('Block not found');
      var old=g.blocks[index],size=slotWidths(type,form);
      if(!size)fail('Invalid block type/layout');
      var assigned=old.controls.filter(function(k){return !!k;});
      if(assigned.length>size.length)fail('New layout has too few slots');
      g.blocks[index]=block(type,form,assigned);
    });
  }
  function removeBlock(layout,panel,id,index){
    return edit(layout,function(s){
      var g=s.panels[panel]&&s.panels[panel].filter(function(x){return x.id===id;})[0];
      if(!g||!g.blocks[index])fail('Block not found');
      if(g.blocks[index].controls.indexOf('Settings')>=0)fail('Settings cannot be removed');
      g.blocks.splice(index,1);
    });
  }
  function assign(layout,key,panel,id,bi,si){
    if(!isKey(key))fail('Unknown control');
    return edit(layout,function(s){
      var g=s.panels[panel]&&s.panels[panel].filter(function(x){return x.id===id;})[0];
      var b=g&&g.blocks[bi];
      if(!b||si<0||si>=b.controls.length||si!==Math.floor(si))fail('Slot not found');
      var previous=locate(s,key);
      var replaced=b.controls[si];
      if(key===replaced)return;
      if(replaced==='Settings')fail('Settings cannot be replaced');
      // Move, never duplicate, one control. Swap the displaced control when possible.
      if(previous){var pb=s.panels[previous.panel][previous.group].blocks[previous.block];pb.controls[previous.slot]=replaced||null;}
      b.controls[si]=key;
    });
  }
  function removeControl(layout,key){
    if(key==='Settings')fail('Settings cannot be removed');
    return edit(layout,function(s){
      var p=locate(s,key);if(!p)return;
      s.panels[p.panel][p.group].blocks[p.block].controls[p.slot]=null;
    });
  }
  function moveGroup(layout,from,id,to,index){
    return edit(layout,function(s){
      if(PANELS.indexOf(from)<0||PANELS.indexOf(to)<0)fail('Unknown panel');
      var old=s.panels[from],at=-1;
      old.forEach(function(g,i){if(g.id===id)at=i;});if(at<0)fail('Group not found');
      var g=old.splice(at,1)[0],dest=s.panels[to];
      if(index==null)index=dest.length;
      if(index<0||index>dest.length||index!==Math.floor(index))fail('Bad position');
      dest.splice(index,0,g);
    });
  }
  function geometry(layout){
    validate(layout);var out=[];
    PANELS.forEach(function(p){
      layout.panels[p].forEach(function(g,gi){
        var y=0;
        g.blocks.forEach(function(b,bi){
          var widths=slotWidths(b.type,b.layout),x=0,h=b.type==='big'?240:80;
          widths.forEach(function(w,si){
            out.push({panel:p,groupId:g.id,groupIndex:gi,blockIndex:bi,slotIndex:si,
              key:b.controls[si],type:b.type,layout:b.layout,x:x,y:y,width:w,height:h});
            x+=w;
          });
          y+=h;
        });
      });
    });
    return out;
  }
  function fromLegacy(layout,sides){
    var out=defaults(),other={};CATALOG.forEach(function(k){other[k]=true;});
    // Preserve user-configured V2 ordering without importing its incompatible geometry.
    if(layout&&Array.isArray(layout.left)&&Array.isArray(layout.center)){
      var left=layout.left.filter(isKey),center=layout.center.filter(isKey);
      if(left.length){
        out.panels.left=[group('left-1',left.slice(0,3).map(function(k){return block('small','full',[k]);}))];
      }
      if(center.length){
        out.panels.center=center.slice(0,4).map(function(k,i){return group('center-'+(i+1),[block('big','full',[k])]);});
      }
    }
    // Repair duplicate keys by leaving their later slot empty.
    var seen={};PANELS.forEach(function(p){out.panels[p].forEach(function(g){g.blocks.forEach(function(b){
      b.controls=b.controls.map(function(k){if(!k||seen[k])return null;seen[k]=true;return k;});
    });});});
    if(!seen.Settings)out.panels.right.push(group('right-settings',[block('side','full',['Settings'])]));
    validate(out);return out;
  }
  function createStore(storage,seed){
    var current=seed?clone(seed):defaults();validate(current);
    try{var saved=JSON.parse(storage.getItem(KEY)||'null');if(saved){validate(saved);current=saved;}}catch(ignore){}
    var listeners=[];
    function change(next){
      validate(next);
      try{storage.setItem(KEY,JSON.stringify(next));}catch(e){return {ok:false,error:'Save failed; layout unchanged'};}
      current=clone(next);listeners.slice().forEach(function(fn){fn(clone(current));});
      return {ok:true,layout:clone(current)};
    }
    return {get:function(){return clone(current);},change:change,
      edit:function(fn){try{return change(edit(current,fn));}catch(e){return {ok:false,error:e.message};}},
      reset:function(){return change(defaults());},
      subscribe:function(fn){listeners.push(fn);return function(){listeners=listeners.filter(function(f){return f!==fn;});};}};
  }
  return {KEY:KEY,BASE:BASE,ROW:ROW,LAYOUTS:LAYOUTS,CATALOG:CATALOG,
    empty:empty,defaults:defaults,clone:clone,block:block,group:group,validate:validate,
    units:units,locate:locate,geometry:geometry,fromLegacy:fromLegacy,createStore:createStore,
    addGroup:addGroup,removeGroup:removeGroup,addBlock:addBlock,replaceBlock:replaceBlock,
    removeBlock:removeBlock,assign:assign,removeControl:removeControl,moveGroup:moveGroup};
});
