/* Classic OSC V5 visual Group editor. Progressive enhancement over stable V2.
 * V2 native tiles, menu events, OSC Motion and their localStorage are intact.
 * The actual NVIDIA preview remains above the Settings editor, not a fake menu.
 * ES5 compatible for bundled Chromium 73. */
(function(win,doc){
 'use strict';
 var M=win.NvClassicGroupV5;if(!M)return;
 var ICONS={Screenshot:'icon-screenshot',Ansel:'icon-camera',Mods:'icon-freestyle',
   InstantReplay:'icon-replay',ManualRecord:'icon-record',Stream:'icon-stream',
   Broadcast:'icon-broadcast',Performance:'icon-performance',Mic:'icon-mic_off',
   Camera:'icon-camera',Gallery:'icon-gallery',Settings:'icon-settings'};
 var LABEL={Screenshot:'Screenshot',Ansel:'Photo mode',Mods:'Game filter',
   InstantReplay:'Instant Replay',ManualRecord:'Record',Stream:'Stream',
   Broadcast:'Broadcast LIVE',Performance:'Performance',Mic:'Microphone',
   Camera:'Camera',Gallery:'Gallery',Settings:'Settings'};
 var SUB={Screenshot:'Capture screen',Ansel:'Photo mode',Mods:'Filters',
   InstantReplay:'Save replay',ManualRecord:'Record video',Stream:'Streaming',
   Broadcast:'Go live',Performance:'Performance',Mic:'Microphone status',
   Camera:'Camera status',Gallery:'View captures',Settings:'Preferences'};
 var legacy=win.__dulukaMenuLayout,seed=legacy&&typeof legacy.get==='function'?M.fromLegacy(legacy.get()):M.defaults();
 var store=M.createStore(win.localStorage,seed),open=false,shell=null,selected=null,
     error='',history=[],search='',oldStarter=win.__dulukaStartMainMenuEditor;
 var PANELS=['left','center','right'],LABP={left:'LEFT',center:'CENTER',right:'RIGHT'};
 function tag(name,cls){var el=doc.createElement(name);el.className=cls||'';return el}
 function esc(value){return String(value).replace(/[&<>"']/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];});}
 function opts(list,current){return list.map(function(v){return '<option value="'+esc(v)+'"'+(v===current?' selected':'')+'>'+esc(v)+'</option>';}).join('');}
 function svg(k){return '<md-icon class="share-icon '+esc(ICONS[k]||'icon-settings')+'" aria-hidden="true"></md-icon>';}
 function slotHtml(key,type,wide,p,groupId,bi,si){
   var detail=type==='big'||(type==='side'&&wide>=120),style='width:'+wide+'px;';
   return '<button type="button" class="nvc5-slot '+(detail?'nvc5-detail':'nvc5-icononly')+(key===selected?' selected':'')+'"'+
     ' style="'+style+'" title="'+esc(key?(LABEL[key]||key):'Empty slot — choose a control')+'"'+
     ' data-action="slot" data-panel="'+p+'" data-group="'+esc(groupId)+'" data-block="'+bi+'" data-slot="'+si+'"'+
     ' data-key="'+esc(key||'')+'">'+
     (key?svg(key):'<span class="nvc5-plus">+</span>')+
     (detail&&key?'<span class="nvc5-slot-info"><strong>'+esc(LABEL[key]||key)+'</strong><small>'+esc(SUB[key]||'')+'</small></span>':'')+
     '</button>';
 }
 function blockHtml(b,p,id,bi){
   var widths=M.LAYOUTS[b.type][b.layout],slots='';
   widths.forEach(function(w,i){slots+=slotHtml(b.controls[i],b.type,w,p,id,bi,i);});
   return '<div class="nvc5-block nvc5-'+esc(b.type)+'" data-type="'+esc(b.type)+'">'+
     '<div class="nvc5-block-toolbar"><span>'+esc(b.type.toUpperCase())+' #'+(bi+1)+'</span>'+
     '<select data-action="type" data-panel="'+p+'" data-group="'+esc(id)+'" data-block="'+bi+'" aria-label="Block type">'+
       opts(['small','side','big'],b.type)+'</select>'+
     '<select data-action="layout" data-panel="'+p+'" data-group="'+esc(id)+'" data-block="'+bi+'" aria-label="Layout">'+
       opts(Object.keys(M.LAYOUTS[b.type]),b.layout)+'</select>'+
     '<button type="button" data-action="remove-block" data-panel="'+p+'" data-group="'+esc(id)+'" data-block="'+bi+'" title="Delete block">×</button></div>'+
     '<div class="nvc5-block-tiles">'+slots+'</div></div>';
 }
 function groupHtml(g,p,i){
   var blocks=g.blocks.map(function(b,j){return blockHtml(b,p,g.id,j);}).join('');
   var rest=M.units(g);
   return '<article class="nvc5-group"><header class="nvc5-group-head">'+
     '<strong>GROUP '+(i+1)+'</strong><span>'+rest+'/3 rows</span>'+
     '<button type="button" data-action="move-up" data-panel="'+p+'" data-group="'+esc(g.id)+'" title="Move up">↑</button>'+
     '<button type="button" data-action="move-down" data-panel="'+p+'" data-group="'+esc(g.id)+'" title="Move down">↓</button>'+
     '<button type="button" data-action="remove-group" data-panel="'+p+'" data-group="'+esc(g.id)+'" title="Delete group">×</button>'+
     '</header><div class="nvc5-group-body">'+blocks+
     (rest<3?'<div class="nvc5-group-empty" style="height:'+((3-rest)*80)+'px"><span>EMPTY</span></div>':'')+
     '</div><div class="nvc5-group-actions">'+(rest<3?
       '<button type="button" data-action="add-small" data-panel="'+p+'" data-group="'+esc(g.id)+'">+ Small</button>'+
       '<button type="button" data-action="add-side" data-panel="'+p+'" data-group="'+esc(g.id)+'">+ Side</button>'+
       (rest===0?'<button type="button" data-action="add-big" data-panel="'+p+'" data-group="'+esc(g.id)+'">+ Big</button>':'')
       :'<span>FULL</span>')+'</div></article>';
 }
 function panelHtml(layout,p){
   return '<section class="nvc5-panel"><header><h3>'+LABP[p]+'</h3><span>'+layout.panels[p].length+' Groups</span></header>'+
     '<div class="nvc5-panel-scroll" data-scroll="'+p+'">'+layout.panels[p].map(function(g,i){return groupHtml(g,p,i);}).join('')+'</div>'+
     '<button type="button" class="nvc5-addgroup" data-action="add-group" data-panel="'+p+'">+ New Group</button></section>';
 }
 function catalogHtml(layout){
   var assigned={};M.geometry(layout).forEach(function(g){if(g.key)assigned[g.key]=true;});
   return M.CATALOG.filter(function(k){return (LABEL[k]||k).toLowerCase().indexOf(search.toLowerCase())>=0;}).map(function(k){
     return '<button type="button" data-action="select" data-key="'+esc(k)+'" class="'+(selected===k?'selected ':'')+(assigned[k]?'assigned':'')+'">'+
       svg(k)+'<span>'+esc(LABEL[k]||k)+'</span>'+(k==='Settings'?'<small>LOCKED</small>':'')+'</button>';
   }).join('');
 }
 function render(){
   if(!shell)return;
   var state=store.get(),scroll={};Array.prototype.forEach.call(shell.querySelectorAll('[data-scroll]'),function(el){scroll[el.dataset.scroll]=el.scrollTop;});
   shell.innerHTML='<div class="nvc5-editor"><header class="nvc5-title">'+
     '<div><h2>Customize Main-menu <small>GROUP V5 / CLASSIC</small></h2>'+
     '<p>Each Group = one 240×240 Big cell. Small & Side rows are 240×80. Choose a control, then choose a slot.</p></div>'+
     '<button type="button" data-action="undo"'+(!history.length?' disabled':'')+'>Undo</button>'+
     '<button type="button" data-action="reset">Restore layout</button>'+
     '<button type="button" class="nvc5-done" data-action="done">Done</button></header>'+
     '<div class="nvc5-catalog"><div class="nvc5-catalog-header"><strong>CONTROLS</strong>'+
       '<input type="search" class="nvc5-search" placeholder="Search controls" aria-label="Search controls" value="'+esc(search)+'">'+
       '<span>'+esc(selected?('Selected: '+LABEL[selected]):'Select a control')+'</span></div>'+
       '<div class="nvc5-catalog-items">'+catalogHtml(state)+'</div></div>'+
     '<div class="nvc5-panels">'+PANELS.map(function(p){return panelHtml(state,p)}).join('')+'</div>'+
     '<footer class="nvc5-footer"><span>'+ (error?'<strong role="alert">'+esc(error)+'</strong>':'Drag Groups with arrows; control slots keep Settings mandatory. NVIDIA native UI is unchanged while editing.')+'</span>'+
       '<span>3 rows × 80px = 240px</span></footer></div>';
   Array.prototype.forEach.call(shell.querySelectorAll('[data-scroll]'),function(el){if(typeof scroll[el.dataset.scroll]==='number')el.scrollTop=scroll[el.dataset.scroll];});
 }
 function apply(next){
   var before=store.get(),result=store.change(next);
   if(!result.ok){error=result.error;render();return;}
   history.push(before);if(history.length>40)history.shift();
   error='';render();
 }
 function findGroup(layout,p,id){var all=layout.panels[p]||[];for(var i=0;i<all.length;i++)if(all[i].id===id)return i;return -1;}
 function execute(action,node){
   var state=store.get(),p=node.getAttribute('data-panel'),id=node.getAttribute('data-group'),
       bi=Number(node.getAttribute('data-block')),si=Number(node.getAttribute('data-slot'));
   if(action==='done'){closeEditor();return;}
   if(action==='select'){selected=node.getAttribute('data-key');error='';render();return;}
   if(action==='undo'){
     if(history.length){var prev=history[history.length-1],r=store.change(prev);if(r.ok){history.pop();error='';}else error=r.error;}
     render();return;
   }
   if(action==='reset'){apply(M.defaults());return;}
   try {
     if(action==='add-group')apply(M.addGroup(state,p));
     else if(action==='remove-group')apply(M.removeGroup(state,p,id));
     else if(action==='add-small')apply(M.addBlock(state,p,id,'small','full'));
     else if(action==='add-side')apply(M.addBlock(state,p,id,'side','full'));
     else if(action==='add-big')apply(M.addBlock(state,p,id,'big','full'));
     else if(action==='remove-block')apply(M.removeBlock(state,p,id,bi));
     else if(action==='move-up'||action==='move-down'){
       var from=findGroup(state,p,id),newIndex=from+(action==='move-up'?-1:1);
       if(newIndex>=0&&newIndex<state.panels[p].length)
         apply(M.moveGroup(state,p,id,p,newIndex));
     }
     else if(action==='slot'){
       if(!selected){error='Select a Control first.';render();return;}
       apply(M.assign(state,selected,p,id,bi,si));
     }
   }catch(e){error=e.message;render();}
 }
 function onClick(e){var node=e.target.closest('[data-action]');if(!node||!shell.contains(node))return;
   var a=node.getAttribute('data-action');if(a==='layout'||a==='type')return;
   e.preventDefault();e.stopPropagation();execute(a,node);
 }
 function onChange(e){var node=e.target,action=node.getAttribute('data-action');if(action!=='layout'&&action!=='type')return;
   try{
     var layout=store.get(),p=node.getAttribute('data-panel'),id=node.getAttribute('data-group');
     var index=Number(node.getAttribute('data-block')),group=layout.panels[p].filter(function(g){return g.id===id;})[0],
       b=group&&group.blocks[index];
     if(!b)throw Error('Block not found');
     apply(M.replaceBlock(layout,p,id,index,action==='type'?node.value:b.type,action==='type'?'full':node.value));
   }catch(err){error=err.message;render();}
 }
 function onInput(e){if(e.target.classList.contains('nvc5-search')){search=e.target.value;render();var f=shell.querySelector('.nvc5-search');if(f){f.focus();f.setSelectionRange(search.length,search.length);}}}
 function onNativeDown(e){
   if(!open)return;
   var target=e.target.closest?e.target.closest('md-menu'):null;
   if(target&&target.closest('.main-menu-container')){
     e.preventDefault();e.stopImmediatePropagation();e.stopPropagation();
   }
 }
 function onNativeClick(e){
   if(!open)return;
   var target=e.target.closest?e.target.closest('md-menu'):null;
   if(!target||!target.closest('.main-menu-container'))return;
   // Editing must NEVER activate Screenshot, Record, Broadcast, etc.
   e.preventDefault();e.stopImmediatePropagation();e.stopPropagation();
   var k=target.getAttribute('data-nv-control');
   if(!k){for(var key in ICONS){var cls={Performance:'performance-tile',Mic:'mic-tile',Camera:'camera-tile',Gallery:'gallery-tile',Settings:'preferences-tile'}[key];if(cls&&target.classList.contains(cls))k=key;}}
   if(k&&LABEL[k]){selected=k;render();}
 }
 function onNativeKey(e){
   if(!open)return;
   if(e.key==='Escape'){closeEditor();return;}
   var menu=e.target.closest&&e.target.closest('md-menu');
   if(menu&&menu.closest('.main-menu-container')&&(e.key==='Enter'||e.key===' ')){e.preventDefault();e.stopImmediatePropagation();}
 }
 function closeEditor(){
   if(!open)return;open=false;
   doc.documentElement.classList.remove('nvc5-editing');
   doc.removeEventListener('mousedown',onNativeDown,true);
   doc.removeEventListener('touchstart',onNativeDown,true);
   doc.removeEventListener('click',onNativeClick,true);
   doc.removeEventListener('keydown',onNativeKey,true);
   win.removeEventListener('hashchange',onRoute);
   if(shell&&shell.parentNode)shell.parentNode.removeChild(shell);
   shell=null;error='';selected=null;
 }
 function onRoute(){
   if(open&&win.location.hash.indexOf('main-menu')<0){closeEditor();}
 }
 function start(){
   if(open)return;
   function mount(){
     if(open)return;
     if(!doc.querySelector('nv-main-menu'))return;
     open=true;doc.documentElement.classList.add('nvc5-editing');
     shell=tag('section','nvc5-shell');
     shell.setAttribute('role','region');shell.setAttribute('aria-label','Customize Main-menu Group V5');
     doc.body.appendChild(shell);shell.addEventListener('click',onClick,false);
     shell.addEventListener('change',onChange,false);shell.addEventListener('input',onInput,false);
     doc.addEventListener('mousedown',onNativeDown,true);
     doc.addEventListener('touchstart',onNativeDown,true);
     doc.addEventListener('click',onNativeClick,true);
     doc.addEventListener('keydown',onNativeKey,true);
     win.addEventListener('hashchange',onRoute);
     render();
   }
   if(doc.querySelector('nv-main-menu')){mount();return;}
   // Ask Angular to show the real NVIDIA Main-menu; never synthesize it.
   var inj=win.angular&&win.angular.element(doc.documentElement).injector();
   if(inj){try{inj.get('$state').go('main.main-menu');}catch(ignore){}}
   var attempts=0,timer=win.setInterval(function(){if(doc.querySelector('nv-main-menu')||attempts++>24){win.clearInterval(timer);mount();}},60);
 }
 // Legacy Settings links use an Angular UI-Router state, not the global opener.
 // Intercept that route ONLY in the extracted classic working copy so the
 // old V2 panel doesn't appear underneath the V5 editor.
 if(win.angular&&typeof win.angular.module==='function'){
   try{win.angular.module('main').run(['$rootScope',function($rootScope){
     $rootScope.$on('$stateChangeStart',function(event,toState){
       if(toState&&toState.name==='main.customize-main-menu'){
         event.preventDefault();
         win.setTimeout(start,0);
       }
     });
   }]);}catch(ignore){}
 }
 win.__dulukaOpenClassicGroupV5=start;
 win.__dulukaCloseClassicGroupV5=closeEditor;
 // Only this isolated classic sandbox opts in; the user's active V2 remains untouched.
 win.__dulukaStartMainMenuEditor=function(){start();};
})(window,document);
