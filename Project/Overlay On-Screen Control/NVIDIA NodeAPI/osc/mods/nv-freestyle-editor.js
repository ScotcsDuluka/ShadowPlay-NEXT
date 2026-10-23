/* NVIDIA ShadowPlay NEXT — Freestyle V5 editor.
 * Edits the REAL original md-menu controls. No clone invokes a native action.
 * Settings uses the archive's NVIDIA panel, with BACKGROUND and CONTROLS.
 */
(function (window, document) {
  'use strict';
  if (window.__nvFreestyleEditor) return;
  var A = window.__nvNextLayoutV5, M = window.NvShadowPlayLayoutV5;
  if (!A || !M) return;
  var host = null, overlay = null, tools = null, picker = null;
  var editing = false, originallyEnabled = false, chosen = null;
  var dragging = null, message = '', pending = null, pendingTimer = null;
  var lastWidth = 0, lastTop = 0, lastLeft = 0, suppressClick = false, suppressTimer = null;
  var LABELS = {
    Screenshot:'Screenshot',Ansel:'Photo mode',Mods:'Game filter',
    InstantReplay:'Instant Replay',ManualRecord:'Record',Stream:'Stream',
    Broadcast:'Broadcast LIVE',Performance:'Performance',Mic:'Microphone',
    Camera:'Camera',Gallery:'Gallery',Settings:'Settings'
  };
  function node(tag, cls, txt) {
    var el = document.createElement(tag);if(cls)el.className=cls;
    if(txt!==undefined)el.textContent=txt;return el;
  }
  function button(cls, label, parent, click) {
    var b=node('button',cls,label);b.type='button';parent.appendChild(b);
    if(click)b.addEventListener('click',click);return b;
  }
  function target(event, selector) {
    return event.target && event.target.closest ? event.target.closest(selector) : null;
  }
  function stop(ev) {ev.preventDefault();ev.stopPropagation();if(ev.stopImmediatePropagation)ev.stopImmediatePropagation();}
  function alertText(text) {
    message=String(text||'');
    var el=tools&&tools.querySelector('.nv-f-status');
    if(el)el.textContent=message;
  }
  function apply(fn) {
    try{A.edit(fn);alertText('');redraw();return true}
    catch(error){alertText(error.message);return false}
  }
  function setSelected(key) {
    if(key && M.CONTROLS.indexOf(key)<0)return;
    chosen=key;alertText(key ? 'Selected: '+LABELS[key]+'. Drop onto a Slot or Group.' : '');
    if(tools)tools.querySelector('.nv-f-selection').textContent=key?'Selected: '+LABELS[key]:'Select or drag a Control';
    updatePalette(A.get());
  }
  function updatePalette(st) {
    var scope=host&&window.angular&&window.angular.element(host).scope();
    if(!scope||scope.$$destroyed||!scope.$evalAsync)return;
    var available=M.available(st);
    scope.$evalAsync(function(){
      var shell=scope.flexMenu;
      if(!shell)return;
      shell.selected=chosen||'';
      if(shell.items)shell.items.sort(function(a,b){
        var ai=available.indexOf(a.key),bi=available.indexOf(b.key);
        return (ai<0?100+M.CONTROLS.indexOf(a.key):ai)-
          (bi<0?100+M.CONTROLS.indexOf(b.key):bi);
      });
    });
  }
  function removePopup() {
    if(picker){picker.remove();picker=null}
  }
  function choosePreset(x,y,groupId,blockIndex,key) {
    removePopup();
    var state=A.get(),found=M.locate(state,groupId);
    if(!found)return;
    var existing=Number.isInteger(blockIndex)&&found.group.blocks[blockIndex],
      menu=node('md-menu','nv-f-context'),
      content=node('md-menu-content','nv-f-context-content');
    menu.setAttribute('role','menu');
    content.setAttribute('role','presentation');
    menu.appendChild(content);
    content.appendChild(node('div','nv-f-menu-head',existing?'Block Type / Layout':'Create Block'));
    ['small','side','big'].forEach(function(type){
      Object.keys(M.PRESETS[type]).forEach(function(layout){
        var count=M.PRESETS[type][layout].length;
        var row=node('md-menu-item','nv-f-menu-item');
        var b=button('md-button',type.toUpperCase()+' · '+layout+' ('+count+' Slots)',row,function(ev){
          stop(ev);removePopup();
          apply(function(s){
            if(existing)return M.changeBlock(s,groupId,blockIndex,type,layout);
            var added=M.addBlock(s,groupId,type,layout,undefined,type==='big');
            return key?M.placeControl(added,key,groupId,M.locate(added,groupId).group.blocks.length-1,0):added;
          });
        });
        b.setAttribute('data-f-preset',type+':'+layout);
        b.setAttribute('role','menuitem');content.appendChild(row);
      });
    });
    picker=menu;document.body.appendChild(menu);
    var rect=menu.getBoundingClientRect();
    menu.style.left=Math.max(8,Math.min(x,window.innerWidth-rect.width-8))+'px';
    menu.style.top=Math.max(8,Math.min(y,window.innerHeight-rect.height-8))+'px';
  }
  function choosePanel(x,y,action) {
    removePopup();
    var menu=node('md-menu','nv-f-context');
    menu.appendChild(node('div','nv-f-menu-head','Place Group in Panel'));
    M.PANEL_NAMES.forEach(function(panel) {
      var row=node('md-menu-item','nv-f-menu-item');
      button('md-button',panel.toUpperCase(),row,function(ev){
        stop(ev);removePopup();apply(function(s){return action(s,panel)});
      });
      menu.appendChild(row);
    });
    picker=menu;document.body.appendChild(menu);
    var rect=menu.getBoundingClientRect();
    menu.style.left=Math.max(8,Math.min(x,window.innerWidth-rect.width-8))+'px';
    menu.style.top=Math.max(8,Math.min(y,window.innerHeight-rect.height-8))+'px';
  }
  function createTools() {
    if(!host||tools)return;
    var area=host.querySelector('.nv-freestyle-controls .nv-live-library');
    if(!area)return;
    tools=node('div','nv-f-tools');
    area.insertBefore(tools,area.firstChild);
    var row=node('div','nv-f-actions');tools.appendChild(row);
    var fill=node('label','nv-f-autofill');row.appendChild(fill);
    var check=node('input');check.type='checkbox';check.className='nv-f-auto-input';
    check.checked=A.get().autoFill;fill.appendChild(check);
    fill.appendChild(node('span','','Auto-Fill Group'));
    check.addEventListener('change',function(){apply(function(s){return M.autoFill(s,check.checked)})});
    button('nv-f-tool','Undo',row,function(){try{A.undo();redraw()}catch(e){alertText(e.message)}});
    var commands=node('div','nv-f-actions');tools.appendChild(commands);
    var add=button('nv-f-tool nv-f-add','+ Group',commands,function(ev){
      choosePanel(ev.clientX,ev.clientY,function(s,p){return M.addGroup(s,p)});
    });
    add.setAttribute('data-f-create','true');
    button('nv-f-tool','Cancel',commands,function(){cancel(true)});
    tools.appendChild(node('div','nv-f-selection','Select or drag a Control'));
    var status=node('div','nv-f-status');status.setAttribute('role','status');tools.appendChild(status);
    var legacy=host.querySelector('.nv-freestyle-legacy');
    if(legacy){legacy.open=false;legacy.style.display='none';}
  }
  function bars() {
    var root=document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container');
    return root&&root.querySelector('md-menu-bar');
  }
  function redraw() {
    if(!editing||!host)return;
    var motion=window.__dulukaOscMotion;
    if((motion && motion.phase && motion.phase!=='idle') ||
      document.documentElement.classList.contains('nv-motion-active')){
      if(overlay)overlay.style.display='none';
      return;
    }
    if(overlay)overlay.style.display='block';
    var g=A.sync(true)||M.geometry(A.get(),'edit',Math.max(320,window.innerWidth-80));
    var bar=bars();
    if(!bar)return;
    var r=bar.getBoundingClientRect();
    if(!r.width||!r.height)return;
    var scale=r.width/Math.max(1,g.width), y=r.y;
    lastWidth=r.width;lastTop=y;lastLeft=r.x;
    if(!overlay){overlay=node('div','nv-f-overlay');document.body.appendChild(overlay)}
    overlay.textContent='';
    var st=A.renderState?A.renderState():A.get();
    var fragment=document.createDocumentFragment();
    // Drag destinations for panels with zero Groups are always present.
    M.PANEL_NAMES.forEach(function(panel, i){
      var badge=node('div','nv-f-panel-target',panel.toUpperCase());
      badge.setAttribute('data-f-panel',panel);
      badge.style.left=(r.left+(i/2)*Math.max(1,r.width-100))+'px';
      badge.style.top=(y-49)+'px';
      fragment.appendChild(badge);
    });
    g.boxes.forEach(function(b){
      var grp=node('div','nv-f-group'+(b.empty?' nv-f-empty':'')),
        x=r.x+b.x*scale,w=b.width*scale,h=b.height*scale,
        model=M.locate(st,b.id).group;
      grp.setAttribute('data-f-group',b.id);
      grp.setAttribute('data-f-panel-of',b.panel);
      grp.style.cssText='left:'+x+'px;top:'+y+'px;width:'+w+'px;height:'+h+'px';
      var head=node('div','nv-f-group-head',b.id+' · '+b.panel);
      head.setAttribute('data-f-drag-group',b.id);head.title='Drag Group to move / swap';
      grp.appendChild(head);
      if(!model.blocks.some(function(x){return x.slots.indexOf('Settings')>=0})) {
        var del=button('nv-f-delete','×',grp,function(ev){stop(ev);apply(function(s){return M.deleteGroup(s,b.id)})});
        del.setAttribute('aria-label','Delete Group '+b.id);
      }
      if(b.empty){
        var add=button('nv-f-empty-target','+ Block',grp,function(ev){
          stop(ev);choosePreset(ev.clientX,ev.clientY,b.id,null,chosen);
        });
        add.setAttribute('data-f-empty-group',b.id);
      }else if(model.blocks.length<3&&!model.blocks.some(function(b){return b.type==='big'})){
        var plus=button('nv-f-add-block','+ Block',grp,function(ev){
          stop(ev);choosePreset(ev.clientX,ev.clientY,b.id,null,chosen);
        });
        plus.setAttribute('data-f-add-block',b.id);
      }
      fragment.appendChild(grp);
    });
    g.slots.forEach(function(s){
      var sl=node('div','nv-f-slot'+(s.control?' nv-f-occupied':' nv-f-vacant'),
        s.control?'':'+'),
        attr=s.groupId+':'+s.block+':'+s.slot;
      sl.setAttribute('data-f-slot',attr);
      sl.setAttribute('data-f-group-id',s.groupId);
      sl.setAttribute('data-f-block-index',String(s.block));
      sl.setAttribute('data-f-slot-index',String(s.slot));
      sl.style.cssText='left:'+(r.x+s.x*scale)+'px;top:'+(y+s.y*scale)+'px;width:'+(s.width*scale)+'px;height:'+(s.height*scale)+'px';
      sl.title=s.control?LABELS[s.control]+' · right click to edit block':'Empty Slot · click to place';
      fragment.appendChild(sl);
    });
    overlay.appendChild(fragment);
    // Let ng-repeat move whole native blocks (button + Angular anchors), so
    // searching after a palette reorder cannot detach neighboring controls.
    updatePalette(st);
    if(tools) {
      var input=tools.querySelector('.nv-f-auto-input');
      if(input)input.checked=st.autoFill;
      var create=tools.querySelector('[data-f-create]');
      if(create)create.disabled=M.count(st)>=M.MAX_GROUPS;
      var selection=tools.querySelector('.nv-f-selection');
      if(selection)selection.textContent=chosen?'Selected: '+LABELS[chosen]:'Select or drag a Control';
    }
    // Native faces use Angular ng-class for Big/Side/Small. Draft mouse edits
    // run outside Angular, so render their real icon/title layout this turn.
    var angularScope=window.angular&&window.angular.element(host).scope();
    if(angularScope&&!angularScope.$$destroyed)angularScope.$evalAsync(function(){});
  }
  function start() {
    if(editing)return;
    var el=document.querySelector('.nv-live-editor.nv-freestyle-settings');
    if(!el||el.classList.contains('ng-leave'))return;
    var shellScope=window.angular&&window.angular.element(el).scope();
    if(shellScope&&(shellScope.$$destroyed||shellScope.flexMenu&&shellScope.flexMenu.editing===false))return;
    host=el;
    originallyEnabled=A.enabled();
    // Live edit is an in-memory owner lease, not a permanent feature flag.
    // Only Done commits a new owner; Cancel reverts the exact native styles.
    if(!originallyEnabled)A.editOwnership(true);
    try {A.beginEdit();editing=true}
    catch(error){
      if(!originallyEnabled)A.editOwnership(false);
      host=null;alertText(error.message);return;
    }
    host.classList.add('nv-freestyle-v5-edit');
    createTools();redraw();
  }
  function hideShell() {
    if(host){
      host.style.setProperty('visibility','hidden','important');
      host.style.setProperty('pointer-events','none','important');
    }
  }
  function cleanup() {
    hideShell();
    if(dragging&&dragging.ghost)dragging.ghost.remove();
    dragging=null;removePopup();
    if(pendingTimer){window.clearTimeout(pendingTimer);pendingTimer=null}
    pending=null;
    if(overlay){overlay.remove();overlay=null}
    if(host)host.classList.remove('nv-freestyle-v5-edit');
    if(tools){tools.remove();tools=null}
    if(host) {
      var legacy=host.querySelector('.nv-freestyle-legacy');
      if(legacy)legacy.style.display='';
    }
    editing=false;host=null;chosen=null;
  }
  function closeShell() {
    // Close the Angular shell directly. A synthetic Done click can be blocked
    // by OSC Motion while closing, leaving a detached V5 lease reopened by scan.
    var scope=host&&window.angular&&window.angular.element(host).scope();
    hideShell();
    if(scope&&scope.flexMenu&&scope.flexMenu.done)scope.flexMenu.done();
  }
  function finish() {
    if(!editing)return;
    try {
      A.done();
      // Done makes V5 the sole persistent layout owner. No reload required:
      // the current Edit Mode already uses this same native adapter.
      if(!originallyEnabled)A.enable(true);
      A.editOwnership(false);
      closeShell();
      cleanup();
    }catch(error){alertText(error.message);throw error}
  }
  function cancel(triggerV2) {
    if(!editing)return;
    A.cancel();
    if(triggerV2)closeShell();
    cleanup();
    A.editOwnership(false); // Restores original native md-menu styles on Cancel.
  }
  function markPending(next) {
    if(pendingTimer){window.clearTimeout(pendingTimer);pendingTimer=null}
    if(pending&&pending.ready){A.preview(null);if(editing)redraw()}
    pending=next;
    if(!next)return;
    pendingTimer=window.setTimeout(function(){
      if(!dragging||!pending)return;
      var dest=M.locate(A.get(),pending.groupId);
      if(!dest)return;
      try {
        var index=dest.index+(pending.edge==='after'?1:0);
        pending.proposal=dragging.kind==='newGroup'?M.addGroup(A.get(),dest.panel,index):
          M.insertionPreview(A.get(),dragging.id,dest.panel,index).layout;
        pending.ready=true;A.preview(pending.proposal);redraw();
      }catch(error){alertText(error.message);return}
      if(overlay) {
        var mark=overlay.querySelector('[data-f-group="'+pending.groupId+'"]');
        if(mark)mark.classList.add('nv-f-insert-preview');
      }
    },360);
  }
  function hoverFromEvent(ev,kind) {
    var el=document.elementFromPoint(ev.clientX,ev.clientY);
    if(!el)return null;
    var groupPriority=(kind==='group'||kind==='newGroup');
    var group=el.closest&&el.closest('[data-f-group]');
    // Slots and blank Group space share the same model hit test. Guide borders
    // are pointer-transparent so native tiles cannot swallow an empty drop.
    if(!group&&overlay)Array.prototype.some.call(overlay.querySelectorAll('[data-f-group]'),function(candidate){
      var r=candidate.getBoundingClientRect();
      if(ev.clientX>=r.left&&ev.clientX<=r.right&&ev.clientY>=r.top&&ev.clientY<=r.bottom){group=candidate;return true}
      return false;
    });
    if(groupPriority&&!group&&overlay){
      var hitSlot=el.closest&&el.closest('[data-f-slot]');
      if(hitSlot)group=overlay.querySelector('[data-f-group="'+hitSlot.getAttribute('data-f-group-id')+'"]');
    }
    if(groupPriority&&group){
      var box=group.getBoundingClientRect(),fraction=(ev.clientX-box.left)/Math.max(box.width,1);
      return {kind:'group',id:group.getAttribute('data-f-group'),
        edge:fraction<.2?'before':fraction>.8?'after':'swap'};
    }
    var slot=el.closest&&el.closest('[data-f-slot]');
    if(slot)return {kind:'slot',groupId:slot.getAttribute('data-f-group-id'),
      block:+slot.getAttribute('data-f-block-index'),slot:+slot.getAttribute('data-f-slot-index')};
    var panel=el.closest&&el.closest('[data-f-panel]');
    if(panel)return {kind:'panel',panel:panel.getAttribute('data-f-panel')};
    if(group) {
      var rect=group.getBoundingClientRect(),fraction=(ev.clientX-rect.left)/Math.max(rect.width,1);
      return {kind:'group',id:group.getAttribute('data-f-group'),
        edge:fraction<.2?'before':fraction>.8?'after':'swap'};
    }
    if(el.closest&&el.closest('.nv-freestyle-controls'))return {kind:'catalog'};
    return null;
  }
  function down(ev) {
    if(!editing||ev.button!==0)return;
    var group=target(ev,'[data-f-drag-group]'),
      slot=target(ev,'[data-f-slot]'),
      control=target(ev,'.nv-freestyle-controls [data-menu-key]'),
      create=target(ev,'[data-f-create]');
    if(group)dragging={kind:'group',id:group.getAttribute('data-f-drag-group')};
    else if(slot){
      var b=slot.getAttribute('data-f-slot').split(':'),
        block=M.locate(A.get(),b[0]).group.blocks[+b[1]],
        key=block&&block.slots[+b[2]];
      if(key){dragging={kind:'control',key:key};setSelected(key)}
    }else if(control){
      var k=control.getAttribute('data-menu-key');
      if(M.CONTROLS.indexOf(k)>=0){dragging={kind:'control',key:k};setSelected(k)}
    }else if(create&&!create.disabled)dragging={kind:'newGroup'};
    if(dragging){
      dragging.x=ev.clientX;dragging.y=ev.clientY;dragging.active=false;
      dragging.target=null;stop(ev);
    }
  }
  function move(ev) {
    if(!dragging||!editing)return;
    // The native Host omits button modifiers on mousemove. The down/up pair
    // owns this drag; DOM buttons=0 must not discard real captured movement.
    if(!dragging.active) {
      if(Math.abs(ev.clientX-dragging.x)+Math.abs(ev.clientY-dragging.y)<8)return;
      dragging.active=true;
      dragging.ghost=node('div','nv-f-drag-ghost',
        dragging.kind==='control'?LABELS[dragging.key]:
          dragging.kind==='group' ? 'GROUP ' + dragging.id : '+ GROUP');
      document.body.appendChild(dragging.ghost);
    }
    dragging.ghost.style.left=(ev.clientX+13)+'px';
    dragging.ghost.style.top=(ev.clientY+14)+'px';
    var t=hoverFromEvent(ev,dragging.kind);
    dragging.target=t;
    if((dragging.kind==='group'||dragging.kind==='newGroup')&&t&&t.kind==='group'&&t.edge!=='swap') {
      if(!pending || pending.groupId!==t.id || pending.edge!==t.edge)
        markPending({groupId:t.id,edge:t.edge,at:Date.now()});
    }else if(pending)markPending(null);
    stop(ev);
  }
  function up(ev) {
    if(!dragging)return;
    var drag=dragging;dragging=null;
    if(drag.ghost)drag.ghost.remove();
    if(!drag.active)return;
    suppressClick=true;
    if(suppressTimer)window.clearTimeout(suppressTimer);
    suppressTimer=window.setTimeout(function(){suppressClick=false;suppressTimer=null},0);
    var t=drag.target||hoverFromEvent(ev,drag.kind);
    if(!t){markPending(null);stop(ev);return}
    if(drag.kind==='control') {
      if(t.kind==='slot')apply(function(s){return M.placeControl(s,drag.key,t.groupId,t.block,t.slot)});
      else if(t.kind==='group'){
        var g=M.locate(A.get(),t.id).group;
        if(g.blocks.length<3&&!g.blocks.some(function(b){return b.type==='big'}))
          choosePreset(ev.clientX,ev.clientY,t.id,null,drag.key);
      }else if(t.kind==='catalog'&&drag.key!=='Settings')
        apply(function(s){return M.takeControl(s,drag.key)});
    }else if(drag.kind==='group'){
      if(t.kind==='panel')apply(function(s){return M.moveGroup(s,drag.id,t.panel)});
      else if(t.kind==='group'&&t.id!==drag.id){
        var dest=M.locate(A.get(),t.id);
        var accepted=pending && pending.groupId===t.id &&
          pending.edge===t.edge && t.edge!=='swap' && pending.ready;
        if(accepted){var proposal=pending.proposal;A.preview(null);apply(function(){return proposal})}
        else apply(function(s){return M.swapGroups(s,drag.id,t.id)});
      }
    }else if(drag.kind==='newGroup'){
      if(t.kind==='panel')apply(function(s){return M.addGroup(s,t.panel)});
      else if(t.kind==='group'){
        if(pending&&pending.ready){var added=pending.proposal;A.preview(null);apply(function(){return added})}
        else{var loc=M.locate(A.get(),t.id);apply(function(s){return M.addGroup(s,loc.panel,loc.index+(t.edge==='before'?0:1))})}
      }
    }
    markPending(null);stop(ev);
  }
  function contextual(ev) {
    if(!editing)return;
    var slot=target(ev,'[data-f-slot]'),
      group=target(ev,'[data-f-group]');
    if(!slot&&!group){var hit=hoverFromEvent(ev,'control');
      if(hit&&hit.kind==='group'&&overlay)group=overlay.querySelector('[data-f-group="'+hit.id+'"]');
    }
    if(slot) {
      stop(ev);choosePreset(ev.clientX,ev.clientY,slot.getAttribute('data-f-group-id'),
        +slot.getAttribute('data-f-block-index'),null);
    }else if(group) {
      stop(ev);choosePreset(ev.clientX,ev.clientY,group.getAttribute('data-f-group'),null,chosen);
    }
  }
  function click(ev) {
    var native=target(ev,'nv-main-menu md-menu > button.menu-button');
    if(native&&(editing||document.documentElement.classList.contains('nv-live-layout-editing'))){stop(ev);return}
    if(!editing)return;
    if(suppressClick){suppressClick=false;stop(ev);return}
    var popup=target(ev,'.nv-f-context');
    if(picker&&!popup)removePopup();
    var restore=target(ev,'.nv-live-editor > header .osc-button-gray');
    if(restore){stop(ev);apply(function(){return M.defaultLayout()});return}
    var doneButton=target(ev,'.nv-live-editor > header .osc-button-green');
    if(doneButton){stop(ev);try{finish()}catch(e){}return}
    var slot=target(ev,'[data-f-slot]');
    if(slot){
      stop(ev);
      if(!chosen) {alertText('Select a Control from the Controls list.');return}
      apply(function(s){return M.placeControl(s,chosen,slot.getAttribute('data-f-group-id'),
        +slot.getAttribute('data-f-block-index'),+slot.getAttribute('data-f-slot-index'))});
      return;
    }
    var ctl=target(ev,'.nv-freestyle-controls [data-menu-key]');
    if(ctl){stop(ev);setSelected(ctl.getAttribute('data-menu-key'));return}
  }
  function keyboard(ev) {
    if(!editing){
      if(document.documentElement.classList.contains('nv-live-layout-editing')&&target(ev,'nv-main-menu md-menu')&&(ev.key==='Enter'||ev.key===' '))stop(ev);
      return;
    }
    if(ev.key==='Escape'){
      if(picker){removePopup();stop(ev);return}
      // Native Angular Material owns Escape while an md-select/md-menu is
      // expanded. Let the popup dismiss itself; do not cancel Freestyle.
      // Angular Material may close its popup on an earlier capture listener,
      // *before* this listener runs. The key's original target still identifies
      // the native select/option that handled Escape, even after md-active goes.
      var source=ev.target;
      if(source&&source.closest&&(source.closest('md-option') ||
         source.closest('md-select-menu') || source.closest('md-select') ||
         source.closest('.md-select-menu-container') ||
         source.closest('.md-open-menu-container')))return;
      if(document.querySelector('.md-select-menu-container.md-active') ||
         document.querySelector('.md-open-menu-container.md-active'))return;
      cancel(true);stop(ev);
    }else if((ev.ctrlKey||ev.metaKey)&&ev.key.toLowerCase()==='z'){
      try{A.undo();redraw()}catch(error){alertText(error.message)}
      stop(ev);
    }
    if(target(ev,'nv-main-menu md-menu')&&
      (ev.key==='Enter'||ev.key===' ')){stop(ev)}
  }
  function scan() {
    var editor=document.querySelector('.nv-live-editor.nv-freestyle-settings');
    if(editor&&editor.classList.contains('ng-leave'))editor=null;
    var shell=editor&&window.angular&&window.angular.element(editor).scope();
    if(shell&&(shell.$$destroyed||shell.flexMenu&&shell.flexMenu.editing===false)){
      if(editing){A.cancel();cleanup();A.editOwnership(false)}
      return;
    }
    var motion=window.__dulukaOscMotion,state=motion&&motion.getState&&motion.getState();
    // The native Host can report a stale idle/closed Motion target while the
    // real Main-menu and Settings editor are still alive (e.g. an md-select
    // opens/closes). Only treat closed as an exit when route/closing state
    // corroborates it; otherwise MutationObserver would cancel on any popup.
    var routeClosed=window.location&&window.location.hash==='#/base';
    var closing=motion&&motion.phase==='closing';
    if(state&&state.target==='closed'&&(routeClosed||closing||!editor)){
      if(editing)cancel(true);
      return;
    }
    if(editor && !editing)start();
    else if(editing&&(!editor||editor!==host)){
      A.cancel();cleanup();A.editOwnership(false);
    }else if(editing){
      // Do not paint Edit outlines over the CEF entrance animation; then
      // track the exact scaled native bar rect without a second layout owner.
      var rect=bars()&&bars().getBoundingClientRect();
      if(!overlay || (overlay.style.display==='none') ||
        (rect && (Math.abs(rect.width-lastWidth)>1||Math.abs(rect.y-lastTop)>1||Math.abs(rect.x-lastLeft)>1)))redraw();
    }
  }
  document.addEventListener('mousedown',down,true);
  document.addEventListener('mousemove',move,true);
  document.addEventListener('mouseup',up,true);
  document.addEventListener('contextmenu',contextual,true);
  document.addEventListener('click',click,true);
  document.addEventListener('keydown',keyboard,true);
  window.addEventListener('blur',function(){if(dragging){
    if(dragging.ghost)dragging.ghost.remove();dragging=null;markPending(null);
  }});
  window.__nvFreestyleEditor={
    scan:scan,begin:start,render:redraw,done:finish,cancel:cancel,
    isEditing:function(){return editing},
    select:setSelected,apply:apply,
    model:M,
    dragState:function(){return {kind:dragging&&dragging.kind,target:dragging&&dragging.target,pending:pending}}
  };
  window.setInterval(scan,250);
  function observeEditor() {
    if(window.MutationObserver&&document.body){
      var observer=new window.MutationObserver(scan);
      observer.observe(document.body,{childList:true,subtree:true});
    }
    scan();
  }
  if(document.readyState!=='loading')observeEditor();
  else document.addEventListener('DOMContentLoaded',observeEditor);
})(window,document);
