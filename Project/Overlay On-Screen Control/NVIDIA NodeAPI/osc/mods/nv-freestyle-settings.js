/* ShadowPlay NEXT Freestyle — native Settings-panel organizer.
 * Keeps NVIDIA .nv-live-editor and its Angular md-select/controls alive.
 * Moves DOM nodes only; does not clone or recompile NVIDIA buttons.
 */
(function(window,document){
 'use strict';
 if(window.__nvFreestyleSettings)return;
 var mounted=null,restore=[];
 function node(tag,cls,label){
   var el=document.createElement(tag);if(cls)el.className=cls;
   if(label)el.textContent=label;return el;
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
   detail.appendChild(summary);
   editor.classList.add('nv-freestyle-settings');
   // Preserve every Angular-owned DOM node and scope by moving rather than
   // rebuilding. The old three-panel V2 editor is still available on demand.
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
   return true;
 }
 function scan(){
   var editor=document.querySelector('.nv-live-editor');
   if(!editor){mounted=null;return}
   if(editor!==mounted||!editor.querySelector('.nv-freestyle-grid'))organize(editor);
 }
 window.__nvFreestyleSettings={scan:scan,organize:organize};
 if(window.setInterval)window.setInterval(scan,300);
 if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',scan);
 else scan();
})(window,document);
