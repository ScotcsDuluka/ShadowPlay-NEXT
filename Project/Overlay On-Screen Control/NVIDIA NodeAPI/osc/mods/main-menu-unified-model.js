(function(root,factory){
  'use strict';
  var api=factory();
  if(typeof module==='object'&&module.exports)module.exports=api;
  if(root)root.NvUnifiedLayoutV4=api;
})(typeof window!=='undefined'?window:null,function(){
  'use strict';
  var PANELS=['left','center','right'], TYPES=['big','side','slot'];
  var CONTROLS=[
    ['Screenshot','Screenshot','icon-screenshot','screenshot-tile'],
    ['Ansel','Photo mode','icon-camera','ansel-tile'],
    ['Mods','Game filter','icon-freestyle','mods-tile'],
    ['InstantReplay','Instant Replay','icon-replay','instant-replay-tile'],
    ['ManualRecord','Record','icon-record','manual-record-tile'],
    ['Stream','Stream','icon-stream','stream-tile'],
    ['Broadcast','Broadcast LIVE','icon-broadcast','broadcast-tile'],
    ['Performance','Performance','icon-performance','performance-tile'],
    ['Mic','Microphone','icon-mic_off','mic-tile'],
    ['Camera','Camera','icon-camera','camera-tile'],
    ['Gallery','Gallery','icon-gallery','gallery-tile'],
    ['Settings','Settings','icon-settings','nv-settings-menu-tile']
  ].map(function(x){return {key:x[0],label:x[1],icon:x[2],name:x[3]};});
  var NAMES={};CONTROLS.forEach(function(c){NAMES[c.key]=c;});
  function clone(a){return JSON.parse(JSON.stringify(a));}
  function block(type,controls){return {type:type,controls:controls.slice()};}
  function initial(legacy,sides){
    var layout={version:4,panels:{left:[],center:[],right:[]}},seen={};
    legacy=legacy||{left:['Screenshot','Ansel','Mods'],center:['InstantReplay','ManualRecord','Stream','Broadcast']};
    sides=sides||{left:(legacy.left||[]).map(function(x){return [x];}),right:[['Performance','Mic','Camera'],['Gallery'],['Settings']]};
    function put(p,type,arr){var keys=[];(arr||[]).forEach(function(key){if(NAMES[key]&&!seen[key]){seen[key]=true;keys.push(key);}});if(keys.length)layout.panels[p].push(block(type,keys));}
    (sides.left||[]).forEach(function(r){put('left',r.length>1?'slot':'side',r);});
    (legacy.left||[]).forEach(function(k){put('left','side',[k]);});
    (legacy.center||[]).forEach(function(k){put('center','big',[k]);});
    (sides.right||[]).forEach(function(r){put('right',r.length>1?'slot':'side',r);});
    // Preserve intentionally hidden optional Controls from legacy V2. Only
    // Settings is mandatory: it must remain somewhere in the layout.
    if(!seen.Settings)put('right','side',['Settings']);
    return normalize(layout);
  }
  function normalize(raw){
    var result={version:4,panels:{left:[],center:[],right:[]}},used={};
    PANELS.forEach(function(p){
      var arr=raw&&raw.panels&&Array.isArray(raw.panels[p])?raw.panels[p]:[];
      arr.forEach(function(b){
        if(!b||TYPES.indexOf(b.type)<0||!Array.isArray(b.controls))return;
        var cleaned=[];
        b.controls.forEach(function(k){
          if(NAMES[k]&&!used[k]){used[k]=true;cleaned.push(k);}
        });
        while(cleaned.length){result.panels[p].push(block(b.type,cleaned.splice(0,9)));}
      });
    });
    if(!used.Settings)result.panels.right.push(block('side',['Settings']));
    validate(result);return result;
  }
  function validate(s){
    if(!s||!s.panels)throw Error('Missing layout');
    var used={};PANELS.forEach(function(p){
      var rows=s.panels[p];if(!Array.isArray(rows))throw Error('Missing panel '+p);
      rows.forEach(function(b){
        if(!b||TYPES.indexOf(b.type)<0||!Array.isArray(b.controls)||!b.controls.length||b.controls.length>9)throw Error('Block needs 1–9 Controls');
        b.controls.forEach(function(k){if(!NAMES[k]||used[k])throw Error('Duplicate or unknown Control '+k);used[k]=true;});
      });
    });
    if(!used.Settings)throw Error('Settings cannot be removed');return true;
  }
  function transaction(src,change){var s=clone(src);change(s);validate(s);return s;}
  function locate(s,k){
    for(var pi=0;pi<PANELS.length;pi++){
      var p=PANELS[pi],blocks=s.panels[p];
      for(var i=0;i<blocks.length;i++){var j=blocks[i].controls.indexOf(k);if(j>=0)return {panel:p,index:i,slot:j};}
    }
    return null;
  }
  function detach(s,k){
    var at=locate(s,k);
    if(at){var rows=s.panels[at.panel],b=rows[at.index];b.controls.splice(at.slot,1);if(!b.controls.length)rows.splice(at.index,1);}
    return at;
  }
  function toBlock(src,key,p,index,type){
    if(!NAMES[key]||PANELS.indexOf(p)<0||TYPES.indexOf(type||'side')<0)throw Error('Unknown control/panel/type');
    return transaction(src,function(s){
      detach(s,key);var rows=s.panels[p];rows.splice(Math.max(0,Math.min(rows.length,Number(index)||0)),0,block(type||'side',[key]));
    });
  }
  function join(src,key,p,index,slot){
    if(!NAMES[key]||PANELS.indexOf(p)<0)throw Error('Unknown control/panel');
    return transaction(src,function(s){
      var b=s.panels[p][index];if(!b)throw Error('Target Block not found');
      if(b.controls.indexOf(key)>=0){if(b.controls.length===1)return; b.controls.splice(b.controls.indexOf(key),1);b.controls.splice(Math.max(0,Math.min(b.controls.length,Number(slot)||0)),0,key);return;}
      if(b.controls.length>=9)throw Error('Block is full (9 Controls)');
      var anchor=b.controls[0];detach(s,key);var at=locate(s,anchor);
      if(!at)throw Error('Block disappeared');
      b=s.panels[at.panel][at.index];
      var i=slot===undefined?b.controls.length:Math.max(0,Math.min(b.controls.length,Number(slot)||0));
      b.controls.splice(i,0,key);
    });
  }
  function changeType(src,p,index,type){
    if(PANELS.indexOf(p)<0||TYPES.indexOf(type)<0)throw Error('Unknown panel/type');
    return transaction(src,function(s){if(!s.panels[p][index])throw Error('Missing Block');s.panels[p][index].type=type;});
  }
  function remove(src,key){
    if(key==='Settings')throw Error('Settings cannot be removed');
    if(!NAMES[key])throw Error('Unknown control');
    return transaction(src,function(s){detach(s,key);});
  }
  function reorder(src,p,index,to){
    if(PANELS.indexOf(p)<0)throw Error('Unknown panel');
    return transaction(src,function(s){
      var a=s.panels[p];if(index<0||index>=a.length)throw Error('Missing Block');
      var b=a.splice(index,1)[0];a.splice(Math.max(0,Math.min(a.length,Number(to)||0)),0,b);
    });
  }
  function blockHeight(b){
    if(b.controls.length===1)return b.type==='big'?240:80;
    if(b.type==='big')return 240;
    return Math.ceil(b.controls.length/3)*80;
  }
  function cells(b,blockX,blockY){
    var n=b.controls.length,w=240,h=blockHeight(b),cols=n<=3?n:3,rows=Math.ceil(n/cols),out=[];
    b.controls.forEach(function(key,i){
      var x,y,width,height,variant;
      if(n===1){x=0;y=0;width=240;height=h;variant=b.type==='big'?'big':'row';}
      else {var col=i%cols,row=Math.floor(i/cols);x=col*(240/cols);y=row*(h/rows);width=240/cols;height=h/rows;variant='small';}
      out.push({key:key,x:blockX+x,y:blockY+y,width:width,height:height,variant:variant,part:i});
    });
    return out;
  }
  function pack(blocks,columns){
    if(!blocks.length)return {width:0,height:0,blocks:[],cells:[]};
    var heights=Array(columns).fill(0),rect=[],tiles=[];
    blocks.forEach(function(b,i){
      var column=0;for(var j=1;j<columns;j++)if(heights[j]<heights[column])column=j;
      var x=column*240,y=heights[column],h=blockHeight(b);
      rect.push({index:i,type:b.type,count:b.controls.length,x:x,y:y,width:240,height:h});
      Array.prototype.push.apply(tiles,cells(b,x,y));heights[column]+=h;
    });
    return {width:columns*240,height:Math.max.apply(null,heights.concat([0])),blocks:rect,cells:tiles};
  }
  function layout(s,viewWidth,viewHeight,options){
    validate(s);
    options=options||{};
    var viewW=Math.max(200,Number(viewWidth)||1920),viewH=Math.max(150,Number(viewHeight)||1080);
    var margin=Math.min(32,viewW*.04),vertical=Math.min(20,viewH*.04),gap=options.gap==null?36:options.gap;
    var alternatives=PANELS.map(function(p){
      var rows=s.panels[p];if(!rows.length)return [pack(rows,1)];
      var variants=[];for(var cols=1;cols<=Math.min(4,rows.length);cols++)variants.push(pack(rows,cols));
      return variants;
    }),best=null;
    alternatives[0].forEach(function(l){alternatives[1].forEach(function(c){alternatives[2].forEach(function(r){
      var a=[l,c,r],nonempty=a.filter(function(x){return x.width>0}).length;
      var width=l.width+c.width+r.width+Math.max(0,nonempty-1)*gap;
      var height=Math.max(l.height,c.height,r.height,1);
      var scale=Math.min(1,(viewW-margin*2)/Math.max(1,width),(viewH-vertical*2)/height);
      var score=scale;
      if(!best||score>best.scale+.00001||(Math.abs(score-best.scale)<.00001&&(height<best.height||(height===best.height&&width<best.width))))best={parts:a,width:width,height:height,scale:scale};
    });});});
    var off=0,panelInfo={},result=[];
    PANELS.forEach(function(p,i){
      var part=best.parts[i],dx=off;
      panelInfo[p]={x:off,width:part.width,height:part.height,blocks:part.blocks.map(function(b){var t=clone(b);t.x+=dx;return t;})};
      part.cells.forEach(function(cell){var t=clone(cell);t.x+=dx;t.panel=p;result.push(t);});
      if(part.width)off+=part.width+gap;
    });
    return {width:best.width,height:best.height,scale:Math.max(.001,best.scale),
      actualWidth:best.width*best.scale,actualHeight:best.height*best.scale,panels:panelInfo,cells:result};
  }
  return {version:4,PANELS:PANELS,TYPES:TYPES,CONTROLS:CONTROLS,initial:initial,normalize:normalize,validate:validate,clone:clone,locate:locate,
    toBlock:toBlock,join:join,changeType:changeType,remove:remove,reorder:reorder,blockHeight:blockHeight,layout:layout};
});
