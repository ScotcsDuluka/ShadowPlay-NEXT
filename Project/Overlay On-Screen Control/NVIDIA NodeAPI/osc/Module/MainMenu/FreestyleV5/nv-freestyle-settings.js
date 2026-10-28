/* ShadowPlay NEXT Freestyle — native Settings-panel organizer.
 * Keeps NVIDIA .nv-live-editor and its Angular md-select/controls alive.
 * Moves DOM nodes only; does not clone or recompile NVIDIA buttons.
 */
(function(window,document){
 'use strict';
 if(window.__nvFreestyleSettings)return;
 var mounted=null,original=[],helpText=null,observer=null;
 function node(tag,cls,label){
   var el=document.createElement(tag);if(cls)el.className=cls;
   if(label)el.textContent=label;return el;
 }
 function remember(el){
   var marker=document.createComment('Freestyle Settings original position');
   el.parentNode.insertBefore(marker,el);
   original.push({node:el,marker:marker});
 }
 function restore(editor){
   if(!mounted||editor&&editor!==mounted)return false;
   var current=mounted,grid=current.querySelector('.nv-freestyle-grid');
   original.forEach(function(record){
     if(!record.marker.parentNode)return;
     record.marker.parentNode.insertBefore(record.node,record.marker);
     record.marker.parentNode.removeChild(record.marker);
   });
   if(grid&&grid.parentNode)grid.parentNode.removeChild(grid);
   if(helpText)helpText.node.textContent=helpText.text;
   current.classList.remove('nv-freestyle-settings');
   ['left','top','width','height','palette-height'].forEach(function(key){
     current.style.removeProperty('--nv-f-settings-'+key);
   });
   original=[];helpText=null;mounted=null;return true;
 }
 function bounds(width,height,bar){
   var panelWidth=width*(width<=930 ? .92 : .792),
       top=bar&&bar.height>0?bar.bottom+40:height*.44;
   top=Math.max(12,Math.min(top,Math.max(12,height-160)));
   return {left:(width-panelWidth)/2,top:top,width:panelWidth,
     height:Math.max(0,Math.min(height*.42,height-top-20))};
 }
 function position(){
   if(!mounted||!window.getComputedStyle||!mounted.getBoundingClientRect)return;
   // Motion temporarily transforms the same native bar. Follow its settled
   // bounds, without moving or measuring controls during entrance/exit.
   var motion=window.__dulukaOscMotion,html=document.documentElement;
   if(motion&&motion.phase&&motion.phase!=='idle'||html&&
     (html.classList.contains('nv-motion-active')||html.classList.contains('nv-motion-preparing')))return;
   var bar=document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container md-menu-bar'),
       rect=mounted.getBoundingClientRect(),style=window.getComputedStyle(mounted),
       box=bounds(window.innerWidth,window.innerHeight,bar&&bar.getBoundingClientRect()),
       sx=rect.width/(parseFloat(style.width)||mounted.offsetWidth),
       sy=rect.height/(parseFloat(style.height)||mounted.offsetHeight);
   if(!isFinite(sx)||!isFinite(sy)||sx<=0||sy<=0)return;
   // A fixed child may still use a transformed NVIDIA ancestor as its
   // containing block. Convert viewport bounds through the measured scale;
   // never assume a hard-coded 160px parent offset or host DPI scale.
   var values={left:(parseFloat(style.left)||0)+(box.left-rect.left)/sx,
     top:(parseFloat(style.top)||0)+(box.top-rect.top)/sy,
     width:box.width/sx,height:box.height/sy};
   Object.keys(values).forEach(function(key){
     var name='--nv-f-settings-'+key,value=values[key].toFixed(3)+'px';
     if(mounted.style.getPropertyValue(name)!==value)mounted.style.setProperty(name,value);
   });
 }
 function organize(editor){
   if(!editor||editor.querySelector('.nv-freestyle-grid'))return false;
   var workspace=editor.querySelector('.nv-live-workspace'),
       library=workspace&&workspace.querySelector('.nv-live-library'),
       background=editor.querySelector('nv-osc-background-settings'),
       footer=editor.querySelector(':scope > footer');
   if(!workspace||!library||!background||!footer)return false;
   var grid=node('div','nv-freestyle-grid'),
       left=node('section','nv-freestyle-background'),
       right=node('aside','nv-freestyle-controls'),
       detail=node('details','nv-freestyle-legacy'),
       summary=node('summary','', 'Legacy layout controls');
   grid.setAttribute('aria-label','Freestyle Settings layout');
   left.setAttribute('aria-label','Background Settings');
   right.setAttribute('aria-label','Controls');
   // The V2 workspace is retained for exact node restoration only. Hide it
   // before insertion so it cannot paint while V5 acquires edit ownership.
   detail.hidden=true;detail.open=false;detail.style.display='none';
   detail.setAttribute('aria-hidden','true');
   detail.appendChild(summary);
   if(mounted&&mounted!==editor)restore();
   [workspace,library,background,footer].forEach(remember);
   var help=library.querySelector(':scope > p');
   if(help){helpText={node:help,text:help.textContent};help.textContent='Drag Controls onto the menu.';}
   editor.classList.add('nv-freestyle-settings');
   // Preserve every Angular-owned DOM node and scope by moving rather than
   // rebuilding. V5 owns the visible editor; V2 nodes remain hidden.
   var anchor=workspace;
   editor.insertBefore(grid,anchor);
   grid.appendChild(left);grid.appendChild(right);
   workspace.removeChild(library);
   detail.appendChild(workspace);
   left.appendChild(background);
   left.appendChild(footer);
   left.appendChild(detail);
   right.appendChild(library);
   mounted=editor;
   position();
   return true;
 }
 function scan(){
   var editor=document.querySelector('.nv-live-editor');
   if(!editor){restore();return}
   if(editor!==mounted||!editor.querySelector('.nv-freestyle-grid'))organize(editor);
   position();
 }
 function start(){
   if(!observer&&window.MutationObserver&&document.body){
     // Angular inserts the editor and Background companion in separate DOM
     // changes. Observe both so organization completes before the next paint,
     // rather than showing V2 until the 300ms fallback interval fires.
     observer=new window.MutationObserver(scan);
     observer.observe(document.body,{childList:true,subtree:true});
   }
   scan();
 }
 window.__nvFreestyleSettings={scan:scan,organize:organize,restore:restore,position:position,bounds:bounds};
 if(window.setInterval)window.setInterval(scan,300);
 if(window.addEventListener)window.addEventListener('resize',position);
 if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start);
 else start();
})(window,document);
