(function(window,document){
 'use strict';
 var G=window.NvGroupLayoutA,M=window.NvUnifiedLayoutV4;
 if(!G||!M)return;
 var KEY='DulukaMainMenuGroupLayoutA_V5',FLAG='DulukaMainMenuGroupLayoutAEnabled';
 var editor=null,selected=null,state=null,undo=[],message='',mouse=null,ghost=null;
 var keys={},present={};
 M.CONTROLS.forEach(function(c){keys[c.key]=c;});
 function enabled(){return window.localStorage.getItem(FLAG)==='1';}
 function get(){
   if(!state){try{var raw=window.localStorage.getItem(KEY);if(raw){
     var s=JSON.parse(raw);G.validate(s);state=s;
   }}catch(e){state=null;}
   if(!state)state=G.fromV4(window.__dulukaUnifiedMainMenu.get());}
   return G.clone(state);
 }
 function save(s,label){G.validate(s);state=G.clone(s);window.localStorage.setItem(KEY,JSON.stringify(state));
   message=label||'Saved to Group layout';render();
   if(window.__nvUnifiedV4RequestNativeSync)window.__nvUnifiedV4RequestNativeSync();
   return get();
 }
 function change(action,label){
   try{var old=get(),next=action(old);save(next,label);undo.push(old);if(undo.length>25)undo.shift();}
   catch(e){message=e.message;render();}
 }
 function escape(s){return String(s).replace(/[&<>"']/g,function(ch){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch];});}
 function icon(k){return '<md-icon class="share-icon '+keys[k].icon+'"></md-icon>';}
 var OPTIONS=[
   {kind:'small',preset:'triple',name:'Small · 1+1+1',label:'[C][C][C]'},
   {kind:'small',preset:'wideLeft',name:'Small · 2+1',label:'[  C  ][C]'},
   {kind:'small',preset:'wideRight',name:'Small · 1+2',label:'[C][  C  ]'},
   {kind:'small',preset:'single',name:'Small · Full',label:'[    C    ]'},
   {kind:'side',preset:'single',name:'Side · Full',label:'[ C  Text / Status ]'},
   {kind:'side',preset:'detailLeft',name:'Side · Left detail',label:'[C Text][C]'},
   {kind:'side',preset:'detailRight',name:'Side · Right detail',label:'[C][C Text]'},
   {kind:'big',preset:'single',name:'Big · Full',label:'[ C / Text / Status ]'}
 ];
 var labels={left:'LEFT',center:'CENTER',right:'RIGHT'};
 function options(b){return OPTIONS.map(function(o){return '<option value="'+o.kind+':'+o.preset+'"'+(o.kind===b.kind&&o.preset===b.preset?' selected':'')+'>'+escape(o.name)+'</option>';}).join('');}
 function line(b,p,gidx,bidx){
   var size=G.PRESETS[b.kind][b.preset],sum=size.reduce(function(a,c){return a+c;},0),html='';
   b.slots.forEach(function(k,si){
     var width=(size[si]/sum*100).toFixed(4);
     html+='<div class="nv-g-cell" style="width:'+width+'%"><button type="button" data-g-slot="'+p+':'+gidx+':'+bidx+':'+si+'" class="'+(k?'filled':'empty')+'" title="'+(k?escape(keys[k].label):'Drop Control here')+'">'+
       (k?icon(k)+(b.kind!=='small'?'<span>'+escape(keys[k].label)+'</span>':''):'<span>+</span>')+'</button>'+
       (k&&k!=='Settings'?'<button type="button" class="nv-g-clear" data-g-clear="'+p+':'+gidx+':'+bidx+':'+si+'" title="Remove Control">×</button>':'')+
       '</div>';
   });
   return '<div class="nv-g-line" data-g-kind="'+b.kind+'">'+html+'</div>';
 }
 function group(g,p,i){
   var rows=G.rows(g),n=g.blocks.reduce(function(a,b){return a+b.slots.filter(Boolean).length;},0);
   var html='<article class="nv-g-group" data-g-group="'+p+':'+i+'">'+
     '<div class="nv-g-group-head"><strong>Group '+(i+1)+'</strong><span>'+n+' Controls · 240×240</span>'+
     '<select data-g-move="'+p+':'+i+'" aria-label="Move Group"><option value="">Move to...</option>'+
      G.PANELS.map(function(p2){return '<option value="'+p2+'">'+labels[p2]+'</option>';}).join('')+'</select>'+
     '<button class="nv-g-delete" type="button" data-g-delete="'+p+':'+i+'" title="Delete empty Group">×</button></div>';
   g.blocks.forEach(function(b,bi){
     html+='<div class="nv-g-block" data-g-block="'+p+':'+i+':'+bi+'">'+
       '<div class="nv-g-block-title"><span>Block '+(bi+1)+'</span><select data-g-preset="'+p+':'+i+':'+bi+'" aria-label="Block preset">'+options(b)+'</select></div>'+
       line(b,p,i,bi)+'</div>';
   });
   if(rows<3)html+='<div class="nv-g-expand"><button type="button" data-g-add-block="'+p+':'+i+':small">+ Small row</button>'+
      '<button type="button" data-g-add-block="'+p+':'+i+':side">+ Side row</button></div>';
   html+='</article>';return html;
 }
 function render(){
   if(!editor||!editor.isConnected)return;
   var s=get(),q=(editor.querySelector('.nv-g-search')||{}).value||'';
   q=q.toLowerCase();
   G.PANELS.forEach(function(p){
     var list=editor.querySelector('[data-g-panel="'+p+'"] .nv-g-group-list');
     list.innerHTML=s.panels[p].map(function(g,i){return group(g,p,i);}).join('')||
       '<div class="nv-g-empty">No Groups · Add one below</div>';
     editor.querySelector('[data-g-panel="'+p+'"] .nv-g-panel-count').textContent=s.panels[p].length+' Groups';
   });
   var bar=editor.querySelector('.nv-g-catalog');
   bar.innerHTML=M.CONTROLS.filter(function(c){return !q||c.label.toLowerCase().indexOf(q)>=0||c.key.toLowerCase().indexOf(q)>=0;})
     .map(function(c){var used=G.find(s,c.key);
       return '<button type="button" draggable="true" data-g-control="'+c.key+'" class="'+(selected===c.key?'selected ':'')+(used?'used':'unused')+'">'+icon(c.key)+'<span>'+escape(c.label)+'</span></button>';
     }).join('');
   editor.querySelector('.nv-g-current').textContent=selected?
     'Selected: '+keys[selected].label+' · Click an empty Slot to place it':'Choose a Control, then click a Slot — or drag to it';
   editor.querySelector('.nv-g-message').textContent=message;
 }
 function reset(){state=null;var fresh=G.fromV4(window.__dulukaUnifiedMainMenu.get());change(function(){return fresh;},'Restored from V4 layout');}
 function part(attr){return attr.split(':');}
 function clicked(e){
   var btn=e.target.closest&&e.target.closest('button,select');if(!btn)return;
   if(btn.hasAttribute('data-g-control')){selected=btn.getAttribute('data-g-control');render();return;}
   var sl=btn.getAttribute('data-g-slot');
   if(sl){if(!selected){message='Select a Control above first';render();return;}
     var z=part(sl);change(function(s){return G.setSlot(s,selected,z[0],+z[1],+z[2],+z[3]);},'Control placed');return;}
   var cl=btn.getAttribute('data-g-clear');
   if(cl){var a=part(cl);change(function(s){return G.emptySlot(s,a[0],+a[1],+a[2],+a[3]);},'Slot cleared');return;}
   var add=btn.getAttribute('data-g-add');
   if(add){change(function(s){return G.createGroup(s,add,'small','triple');},'New Group created');return;}
   var extra=btn.getAttribute('data-g-add-block');
   if(extra){var b=part(extra);change(function(s){return G.addBlock(s,b[0],+b[1],b[2],b[2]==='small'?'triple':'single');},'Block added');return;}
   var del=btn.getAttribute('data-g-delete');
   if(del){var d=part(del);change(function(s){return G.removeGroup(s,d[0],+d[1]);},'Empty Group removed');return;}
   if(btn.hasAttribute('data-g-undo')){
     if(undo.length){save(undo.pop(),'Undone');}return;
   }
   if(btn.hasAttribute('data-g-reset')){reset();return;}
 }
 function changed(e){
   var sel=e.target;if(!sel.matches)return;
   var type=sel.getAttribute('data-g-preset');
   if(type){var a=part(type),b=part(sel.value);change(function(s){return G.choosePreset(s,a[0],+a[1],+a[2],b[0],b[1]);},'Preset changed');return;}
   var move=sel.getAttribute('data-g-move');
   if(move&&sel.value){var parts=part(move);change(function(s){return G.moveGroup(s,parts[0],+parts[1],sel.value);},'Group moved');}
 }
 function setup(e){
   var wrap=document.createElement('section');wrap.className='nv-group-layout-editor';wrap.setAttribute('aria-label','Group Layout A');
   wrap.innerHTML='<div class="nv-g-toolbar"><div><strong>GROUP LAYOUT</strong><small>Panel → Group (240×240) → Block → Slot · Preset A</small></div>'+
     '<button data-g-undo type="button">↶ Undo</button><button data-g-reset type="button">Restore Groups</button></div>'+
     '<div class="nv-g-library"><div class="nv-g-library-head"><strong>Controls</strong><span class="nv-g-current"></span></div>'+
     '<input class="nv-g-search" type="search" placeholder="Search NVIDIA Controls..." aria-label="Search Controls"><div class="nv-g-catalog"></div></div>'+
     '<div class="nv-g-panels">'+G.PANELS.map(function(p){return '<section class="nv-g-panel" data-g-panel="'+p+'">'+
       '<header><strong>'+labels[p]+'</strong><span class="nv-g-panel-count"></span></header>'+
       '<div class="nv-g-group-list"></div><button type="button" class="nv-g-add" data-g-add="'+p+'">+ New Group</button></section>';}).join('')+'</div>'+
     '<div class="nv-g-message" role="status"></div>';
   var old=e.querySelector('.nv-unified-editor');if(old)e.insertBefore(wrap,old);else e.appendChild(wrap);
   e.classList.add('nv-group-layout-on');editor=wrap;
   wrap.addEventListener('click',clicked);wrap.addEventListener('change',changed);
   wrap.addEventListener('input',function(ev){if(ev.target.classList.contains('nv-g-search'))render();});
   // NVIDIA CEF: synthetic HTML5 drag/drop is unreliable. Native mouse events
   // work on the same real Slots as click-to-place.
   var pendingKey=null;
   wrap.addEventListener('mousedown',function(ev){var b=ev.target.closest&&ev.target.closest('[data-g-control]');if(b&&ev.button===0)pendingKey=b.getAttribute('data-g-control');},true);
   document.addEventListener('mouseup',function(ev){
     if(!pendingKey||!editor||!editor.isConnected){pendingKey=null;return;}
     var at=document.elementFromPoint(ev.clientX,ev.clientY);
     var slot=at&&at.closest&&at.closest('[data-g-slot]');
     if(slot){var a=part(slot.getAttribute('data-g-slot'));selected=pendingKey;
       change(function(s){return G.setSlot(s,selected,a[0],+a[1],+a[2],+a[3]);},'Control dropped');}
     pendingKey=null;
   },true);
   render();
 }
 function mount(){
   if(!enabled())return;
   var host=document.querySelector('.nv-live-editor');
   if(!host){editor=null;return;}
   if(editor&&editor.isConnected)return;
   if(!host.querySelector('.nv-unified-editor'))window.__dulukaUnifiedMainMenu.openEditor();
   if(!host.querySelector('.nv-group-layout-editor'))setup(host);
 }
 var api=window.__dulukaGroupLayoutA={
   storageKey:KEY,flagKey:FLAG,enabled:enabled,get:get,set:save,
   enable:function(value){if(value)window.localStorage.setItem(FLAG,'1');else window.localStorage.removeItem(FLAG);
      if(!value){var host=document.querySelector('.nv-live-editor');if(host)host.classList.remove('nv-group-layout-on');}
      mount();return enabled();},
   render:render,mount:mount,model:G,select:function(key){if(keys[key]){selected=key;render();}}
 };
 if(window.setInterval)window.setInterval(mount,270);
 mount();
})(window,document);