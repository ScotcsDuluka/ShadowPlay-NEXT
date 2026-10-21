(function(window,document){
  'use strict';
  // Opt-in V4 bridge. This never recreates NVIDIA's md-menu or menu-button.
  // V2 remains the fallback and its persisted keys are never overwritten.
  var M=window.NvUnifiedLayoutV4;
  if(!M)return;
  var KEY='DulukaMainMenuUnifiedV4',FLAG='DulukaMainMenuUnifiedV4Enabled';
  var state=null,selected=null,lastError='',editor=null,pendingIdle=null,undoStack=[];
  var members={};
  M.CONTROLS.forEach(function(c){members[c.key]=c;});
  function stored(key){try{return window.localStorage.getItem(key);}catch(e){return null;}}
  function enabled(){return stored(FLAG)==='1';}
  function backupLegacy(){
    var old=window.__dulukaMenuLayout;
    var result=M.initial(old&&old.get(),old&&old.getSides());
    // Carry the user's current per-Control Big/Side choices into V4 without
    // touching the original DulukaMainMenuBlockTypesV1 key.
    var types=old&&old.getBlockTypes?old.getBlockTypes():{};
    result.panels.center.forEach(function(b){
      if(b.controls.length===1&&types[b.controls[0]]==='side')b.type='side';
    });
    return result;
  }
  function get(){
    if(!state){
      var raw=stored(KEY);
      try{state=raw?M.normalize(JSON.parse(raw)):backupLegacy();}
      catch(e){state=backupLegacy();}
    }
    return M.clone(state);
  }
  function notify(){
    if(window.__nvUnifiedV4RequestNativeSync)window.__nvUnifiedV4RequestNativeSync();
    if(editor)drawEditor();
  }
  function set(next){
    M.validate(next);
    var clean=M.normalize(next);
    try{window.localStorage.setItem(KEY,JSON.stringify(clean));}
    catch(e){throw Error('Could not save unified layout');}
    state=clean;notify();return get();
  }
  function change(action,success){
    try{var before=get();set(action(before));undoStack.push(before);if(undoStack.length>25)undoStack.shift();lastError=success||'Layout saved';}
    catch(e){lastError=e.message;}
    if(editor){var msg=editor.querySelector('.nv-unified-message');if(msg)msg.textContent=lastError;}
    return !/full|cannot|Unknown|Could not|Missing|Duplicate/.test(lastError);
  }
  function geometry(screen){
    var h=window.innerHeight||1080,w=window.innerWidth||1920;
    return M.layout(get(),Math.max(320,w-64),screen||Math.max(240,Math.min(h*.42,h-190)),{gap:36});
  }
  function namedTile(root,key){var tile=members[key];return tile&&root.querySelector('md-menu.'+tile.name);}
  function setStyle(el,name,value){
    if(!el)return;
    if(el.style.getPropertyValue(name)!==value||el.style.getPropertyPriority(name)!=='important')
      el.style.setProperty(name,value,'important');
  }
  function nativeGeometry(root,preMotion){
    if(!enabled()||!root)return null;
    // Never compete with OSC Motion's entrance/exit. It drives the same native
    // rail position during 'opening' and restores it at 'idle'.
    var motion=window.__dulukaOscMotion,phase=motion&&motion.phase;
    // A routed CEF page can remain visible while OSC Motion is CLOSED.
    // Do not force native geometry over a real close/exit animation.
    if(!preMotion&&motion&&motion.getState&&motion.getState().target==='closed')return null;
    if(!preMotion&&phase&&phase!=='idle'){
      if(pendingIdle===null&&window.setTimeout)pendingIdle=window.setTimeout(function(){
        pendingIdle=null;
        if(enabled()&&window.__nvUnifiedV4RequestNativeSync)window.__nvUnifiedV4RequestNativeSync();
      },240);
      return null;
    }
    var bar=root.querySelector('md-menu-bar');if(!bar)return null;
    var screenW=window.innerWidth||1920,screenH=window.innerHeight||1080;
    var available=editor?Math.max(180,Math.min(screenH*.35,440)):Math.max(220,Math.min(screenH*.54,700));
    var g=geometry(available);
    var x=(screenW-g.actualWidth)/2;
    // NVIDIA sizes and centers .main-menu-container from md-menu-bar.
    // Writing a new width *changes the native root width* (e.g. 1040 -> 1592).
    // Measure the rail origin only AFTER applying that width; otherwise the
    // old 1040px origin is applied twice and the left panel overflows.
    setStyle(bar,'width',g.width+'px');
    setStyle(bar,'min-width',g.width+'px');
    setStyle(bar,'height',g.height+'px');
    var railParent=bar.parentElement||bar.offsetParent;
    var stableX;
    if(railParent&&railParent.getBoundingClientRect){
      // The NVIDIA md-toolbar can be translated far outside the centered
      // .main-menu-container. Anchor relative to the actual toolbar, not
      // an assumed 1040px root (otherwise live edits drift off screen).
      stableX=railParent.getBoundingClientRect().left+(bar.offsetLeft||0);
    }else{
      var rootW=root.getBoundingClientRect?root.getBoundingClientRect().width:1040;
      if(!isFinite(rootW)||rootW<960)rootW=1040;
      stableX=(screenW-rootW)/2+40;
    }
    setStyle(bar,'transform-origin','top left');
    setStyle(bar,'transform','translateX('+(x-stableX)+'px) scale('+g.scale+')');
    bar.classList.add('nv-unified-bar');
    var present={};
    g.cells.forEach(function(cell){
      var el=namedTile(root,cell.key);if(!el)return;
      present[cell.key]=true;
      el.setAttribute('data-nv-unified-key',cell.key);
      if(cell.key==='Settings'||members[cell.key]&&M.CONTROLS.indexOf(members[cell.key])>=7)
        el.setAttribute('data-nv-side-key',cell.key);
      setStyle(el,'position','absolute');
      setStyle(el,'left',cell.x+'px');setStyle(el,'top',cell.y+'px');
      setStyle(el,'width',cell.width+'px');setStyle(el,'height',cell.height+'px');
      setStyle(el,'right','auto');
      el.classList.add('nv-unified-tile');
      if(el.classList.contains('nv-side-small-tile')!==(cell.variant==='small'))el.classList[cell.variant==='small'?'add':'remove']('nv-side-small-tile');
      if(el.classList.contains('nv-side-full-tile')===(cell.variant==='small'))el.classList[cell.variant==='small'?'remove':'add']('nv-side-full-tile');
      if(el.classList.contains('nv-unified-big')!==(cell.variant==='big'))el.classList[cell.variant==='big'?'add':'remove']('nv-unified-big');
      if(el.classList.contains('nv-flex-tile')===false)el.classList.add('nv-flex-tile');
      var info=el.querySelector('.nv-side-native-info');
      if(info)info.style.display=cell.variant==='row'?'block':'none';
    });
    M.CONTROLS.forEach(function(c){var el=namedTile(root,c.key);if(el&&!present[c.key]){el.classList.remove('nv-unified-tile');}});
    return g;
  }
  function visible(){var s=get(),set={};M.PANELS.forEach(function(p){s.panels[p].forEach(function(b){b.controls.forEach(function(k){set[k]=true;});});});return set;}
  function cell(key){
    var g=M.layout(get(),1920,550);return g.cells.filter(function(c){return c.key===key;})[0]||null;
  }
  function escapeText(s){return String(s).replace(/[&<>"']/g,function(ch){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch];});}
  function drawEditor(){
    if(!editor||!editor.isConnected)return;
    var s=get(),q=(editor.querySelector('.nv-unified-search')||{}).value||'';
    q=q.toLowerCase();
    M.PANELS.forEach(function(p){
      var el=editor.querySelector('.nv-unified-panel[data-panel="'+p+'"] .nv-unified-block-list'),html='';
      s.panels[p].forEach(function(b,i){
        html+='<article class="nv-unified-block" data-v4-count="'+b.controls.length+'" data-v4-block="'+p+':'+i+'"><div class="nv-unified-block-head"><span class="nv-unified-grip" title="Drag this Block to another panel" role="button" tabindex="0" aria-label="Drag Block">⠿</span>'+
          '<select aria-label="Block Type" data-v4-type="'+p+':'+i+'">'+M.TYPES.map(function(type){return '<option value="'+type+'"'+(type===b.type?' selected':'')+'>'+({big:'Big',side:'Side',slot:'Side Slot'}[type])+'</option>';}).join('')+
          '</select><span>'+b.controls.length+'/9</span><button data-v4-shift="'+p+':'+i+':-1" aria-label="Move block earlier">↑</button>'+
          '<button data-v4-shift="'+p+':'+i+':1" aria-label="Move block later">↓</button></div><div class="nv-unified-members">';
        b.controls.forEach(function(k){
          html+='<div class="nv-unified-member-cell"><button title="'+escapeText(members[k].label)+'" class="nv-unified-member" data-v4-select="'+k+'" draggable="true" data-v4-drag="'+k+'"><md-icon class="share-icon '+members[k].icon+'"></md-icon><span>'+escapeText(members[k].label)+'</span></button>'+
            (k==='Settings'?'<span class="nv-unified-lock" title="Settings cannot be removed">◆</span>':
             '<button class="nv-unified-remove" data-v4-remove="'+k+'" aria-label="Remove '+escapeText(members[k].label)+'"><md-icon class="share-icon icon-app_close"></md-icon></button>')+'</div>';
        });
        html+='</div><button class="nv-unified-join" data-v4-join="'+p+':'+i+'"'+(b.controls.length>=9?' disabled':'')+'>+ Add selected to Block</button></article>';
      });
      el.innerHTML=html||'<p class="nv-unified-empty">Drop a Control here</p>';
      editor.querySelector('.nv-unified-panel[data-panel="'+p+'"] .nv-unified-count').textContent=s.panels[p].reduce(function(n,b){return n+b.controls.length;},0)+' Controls';
    });
    var catalog=editor.querySelector('.nv-unified-palette');
    catalog.innerHTML=M.CONTROLS.filter(function(c){return !q||c.label.toLowerCase().indexOf(q)>=0||c.key.toLowerCase().indexOf(q)>=0;}).map(function(c){
      var pos=M.locate(s,c.key);return '<button data-v4-select="'+c.key+'" draggable="true" data-v4-drag="'+c.key+'" class="'+(selected===c.key?'selected ':'')+(pos?'placed':'unplaced')+'"><md-icon class="share-icon '+c.icon+'"></md-icon><span>'+escapeText(c.label)+'</span>'+(c.key==='Settings'?'<b>◆</b>':'')+'</button>';
    }).join('');
    var sel=editor.querySelector('.nv-unified-selection');sel.textContent=selected?'Selected: '+members[selected].label:'Select a Control, then add it to a panel or Block.';
    var msg=editor.querySelector('.nv-unified-message');if(msg)msg.textContent=lastError;
  }
  function getEditor(){
    if(!enabled())return null;
    var legacy=document.querySelector('.nv-live-editor');
    if(!legacy){editor=null;return null;}
    if(editor===legacy&&legacy.querySelector('.nv-unified-editor'))return editor;
    editor=legacy;editor.classList.add('nv-unified-editor-active');
    var description=legacy.querySelector('header p');
    if(description)description.textContent='Arrange Big, Side or Side Slot in any panel. Each Block holds 1–9 Controls. Settings cannot be removed.';
    // Intercept the legacy Restore button: in V4 this resets only the V4
    // arrangement. The saved V2 layout must never be reset or overwritten.
    editor.addEventListener('click',function(event){
      var button=event.target.closest&&event.target.closest('header button');
      if(button&&button.getAttribute('ng-click')==='flexMenu.reset()'){
        event.preventDefault();event.stopImmediatePropagation();
        change(function(){return backupLegacy();},'V4 layout restored from NVIDIA arrangement');
      }
    },true);
    var wrap=document.createElement('section');wrap.className='nv-unified-editor';wrap.setAttribute('aria-label','Flexible Main-menu Customize');
    wrap.innerHTML='<div class="nv-unified-guide"><span>Drag to arrange · Right-click a tile in the preview to change its Block Type.</span><button type="button" data-v4-undo aria-label="Undo last layout change">↶ Undo</button></div><div class="nv-unified-panel-row">'+M.PANELS.map(function(p){
      return '<section class="nv-unified-panel" data-panel="'+p+'"><h3>'+p.toUpperCase()+' <span class="nv-unified-count"></span></h3><div class="nv-unified-block-list"></div><button data-v4-add="'+p+'">+ New Block</button></section>';
    }).join('')+'</div><div class="nv-unified-controls"><header><h3>Controls</h3><span class="nv-unified-selection"></span></header>'+
      '<input type="search" class="nv-unified-search" placeholder="Search Controls..." aria-label="Search Controls"><div class="nv-unified-palette"></div>'+
      '<p>Each Block holds up to 9 Controls. Drag to group, or select and press Add. Settings can move but cannot be removed.</p></div><footer class="nv-unified-message" role="status"></footer>';
    var old=legacy.querySelector('.nv-live-workspace');if(old)legacy.insertBefore(wrap,old);else legacy.appendChild(wrap);
    var pending='';
    wrap.addEventListener('input',function(ev){if(ev.target.classList.contains('nv-unified-search'))drawEditor();});
    wrap.addEventListener('change',function(ev){var id=ev.target.getAttribute('data-v4-type');if(!id)return;var parts=id.split(':');
      change(function(s){return M.changeType(s,parts[0],Number(parts[1]),ev.target.value);},'Block Type updated');
    });
    wrap.addEventListener('click',function(ev){
      var target=ev.target.closest&&ev.target.closest('button');if(!target)return;
      if(target.hasAttribute('data-v4-undo')){if(undoStack.length){set(undoStack.pop());lastError='Undone';drawEditor();}return;}
      var k=target.getAttribute('data-v4-select');if(k){selected=k;drawEditor();return;}
      var del=target.getAttribute('data-v4-remove');if(del){change(function(s){return M.remove(s,del);},'Control removed');return;}
      var add=target.getAttribute('data-v4-add');if(add){if(!selected){lastError='Select a Control first';drawEditor();return;}
        change(function(s){return M.toBlock(s,selected,add,s.panels[add].length,'side');},'Moved to '+add);return;}
      var join=target.getAttribute('data-v4-join');if(join){if(!selected){lastError='Select a Control first';drawEditor();return;}
        var a=join.split(':');change(function(s){return M.join(s,selected,a[0],Number(a[1]));},'Grouped Controls');return;}
      var move=target.getAttribute('data-v4-shift');if(move){var x=move.split(':');
        change(function(s){return M.reorder(s,x[0],Number(x[1]),Number(x[1])+Number(x[2]));},'Block reordered');}
    });
    wrap.addEventListener('dragstart',function(e){var t=e.target.closest&&e.target.closest('[data-v4-drag]');if(t){pending=t.getAttribute('data-v4-drag');e.dataTransfer.setData('text/plain',pending);}});
    wrap.addEventListener('dragover',function(e){if(e.target.closest&&e.target.closest('.nv-unified-panel'))e.preventDefault();});
    wrap.addEventListener('drop',function(e){var panel=e.target.closest&&e.target.closest('.nv-unified-panel');if(!panel)return;
      e.preventDefault();var key=e.dataTransfer.getData('text/plain')||pending;if(!members[key])return;
      var target=e.target.closest('.nv-unified-block'),p=panel.getAttribute('data-panel');
      if(target){var b=target.getAttribute('data-v4-block').split(':');change(function(s){return M.join(s,key,b[0],Number(b[1]));},'Grouped Controls');}
      else change(function(s){return M.toBlock(s,key,p,s.panels[p].length,'side');},'Moved Control');
    });
    // The native Preview is intentionally borderless. Right-click a REAL
    // NVIDIA md-menu tile while editing to configure the containing Block.
    // Outside Customize we leave the original NVIDIA context menu alone.
    var blockContextPopup=null;
    function closeBlockContext(){
      if(blockContextPopup&&blockContextPopup.parentNode)blockContextPopup.parentNode.removeChild(blockContextPopup);
      blockContextPopup=null;
    }
    function openBlockContext(event){
      if(!enabled()||!editor||!editor.isConnected||
        !document.documentElement.classList.contains('nv-live-layout-editing'))return;
      if(!event.target.closest)return;
      var nativeTile=event.target.closest('md-menu[data-nv-unified-key]');
      if(!nativeTile)return;
      var nativeMenu=nativeTile.closest('nv-main-menu');
      if(!nativeMenu||nativeMenu.hasAttribute('data-customize-preview'))return;
      var key=nativeTile.getAttribute('data-nv-unified-key'),current=get(),where=M.locate(current,key);
      if(!where)return;
      event.preventDefault();event.stopImmediatePropagation();
      closeBlockContext();
      var block=current.panels[where.panel][where.index];
      var names={big:'Block Big',side:'Block Side',slot:'Block Side Slot'};
      var popup=document.createElement('div');popup.className='nv-unified-context';
      popup.setAttribute('role','menu');popup.setAttribute('aria-label','Block type options');
      popup.innerHTML='<div class="nv-unified-context-title">'+escapeText(members[key].label)+
        '<small>'+escapeText(where.panel.toUpperCase())+' · '+block.controls.length+'/9 Controls</small></div>'+
        M.TYPES.map(function(type){
          return '<button type="button" role="menuitemradio" aria-checked="'+(block.type===type)+
            '" data-v4-context-type="'+type+'"><span>'+names[type]+'</span>'+
            (block.type===type?'<strong>✓</strong>':'')+'</button>';
        }).join('');
      document.body.appendChild(popup);blockContextPopup=popup;
      var w=popup.offsetWidth||220,h=popup.offsetHeight||160;
      popup.style.left=Math.max(8,Math.min(event.clientX,(window.innerWidth||1920)-w-8))+'px';
      popup.style.top=Math.max(8,Math.min(event.clientY,(window.innerHeight||1080)-h-8))+'px';
      popup.__v4Panel=where.panel;popup.__v4Index=where.index;
    }
    function clickBlockContext(event){
      if(!blockContextPopup)return;
      var target=event.target.closest&&event.target.closest('[data-v4-context-type]');
      if(target&&blockContextPopup.contains(target)){
        event.preventDefault();event.stopImmediatePropagation();
        var type=target.getAttribute('data-v4-context-type');
        var panel=blockContextPopup.__v4Panel,index=blockContextPopup.__v4Index;
        closeBlockContext();
        if(M.TYPES.indexOf(type)>=0)
          change(function(s){return M.changeType(s,panel,index,type);},'Block Type: '+type);
        return;
      }
      if(!blockContextPopup.contains(event.target))closeBlockContext();
    }
    function escapeBlockContext(event){
      if(blockContextPopup&&event.key==='Escape'){closeBlockContext();event.preventDefault();}
    }
    document.addEventListener('contextmenu',openBlockContext,true);
    document.addEventListener('click',clickBlockContext,true);
    document.addEventListener('keydown',escapeBlockContext,true);
    // Older NVIDIA CEF does not reliably fire HTML5 drag/drop. Use native
    // mouse events, a non-interactive ghost and actual elementFromPoint hits.
    // Works for both catalog tiles and whole Block handles.
    var drag=null,ghost=null,hit=null,suppressClick=false;
    function clearTarget(){
      if(hit&&hit.classList)hit.classList.remove('nv-unified-drop-target');
      hit=null;
    }
    function cancelDrag(){
      clearTarget();if(ghost&&ghost.parentNode)ghost.parentNode.removeChild(ghost);
      ghost=null;drag=null;wrap.classList.remove('nv-unified-dragging');
    }
    function dragDown(event){
      if(event.button!==0||event.target.closest('select,input,[data-v4-remove],[data-v4-undo],[data-v4-shift],[data-v4-add],[data-v4-join]'))return;
      var tile=event.target.closest('[data-v4-drag]'),grip=event.target.closest('.nv-unified-grip');
      if(!tile&&!grip)return;
      var from=grip&&grip.closest('[data-v4-block]');
      drag={kind:grip?'block':'control',key:tile&&tile.getAttribute('data-v4-drag'),
        source:from&&from.getAttribute('data-v4-block'),x:event.clientX,y:event.clientY,active:false};
      // Prevent browser-native drag images from stealing mouseup events in CEF.
      event.preventDefault();
    }
    function dragMove(event){
      if(!drag)return;
      var delta=Math.abs(event.clientX-drag.x)+Math.abs(event.clientY-drag.y);
      if(!drag.active&&delta<7)return;
      if(!drag.active){
        drag.active=true;ghost=document.createElement('div');ghost.className='nv-unified-drag-ghost';
        ghost.textContent=drag.kind==='block'?'Move Block':members[drag.key].label;
        document.body.appendChild(ghost);wrap.classList.add('nv-unified-dragging');
      }
      if(event.cancelable)event.preventDefault();
      ghost.style.left=(event.clientX+15)+'px';ghost.style.top=(event.clientY+13)+'px';
      clearTarget();
      var over=document.elementFromPoint(event.clientX,event.clientY);
      if(!over)return;
      var target=over.closest('.nv-unified-block, .nv-unified-panel');
      if(target){hit=target;hit.classList.add('nv-unified-drop-target');}
    }
    function dragUp(event){
      if(!drag)return;
      var current=drag,drop=hit;
      if(current.active){
        suppressClick=true;
        window.setTimeout(function(){suppressClick=false;},80);
        if(drop){
          var blockNode=drop.closest('[data-v4-block]'),panelNode=drop.closest('[data-panel]');
          var panel=panelNode&&panelNode.getAttribute('data-panel');
          if(panel){
            if(current.kind==='block'){
              var from=current.source.split(':'),index=blockNode?Number(blockNode.getAttribute('data-v4-block').split(':')[1]):get().panels[panel].length;
              change(function(s){return M.moveBlock(s,from[0],Number(from[1]),panel,index);},'Block moved');
            }else if(blockNode){
              var id=blockNode.getAttribute('data-v4-block').split(':');
              change(function(s){return M.join(s,current.key,id[0],Number(id[1]));},'Control grouped');
            }else{
              change(function(s){return M.toBlock(s,current.key,panel,get().panels[panel].length,'side');},'New Block created');
            }
          }
        }else lastError='Drop over a green panel or Block';
      }
      cancelDrag();
      if(editor)drawEditor();
    }
    function preventNativeDrag(event){if(event.target.closest('[data-v4-drag]'))event.preventDefault();}
    function cancelOnKey(event){if(event.key==='Escape'&&drag){cancelDrag();event.preventDefault();}}
    function suppressAfterDrag(event){if(suppressClick){event.preventDefault();event.stopImmediatePropagation();}}
    wrap.addEventListener('mousedown',dragDown,true);
    document.addEventListener('mousemove',dragMove,true);
    document.addEventListener('mouseup',dragUp,true);
    document.addEventListener('keydown',cancelOnKey,true);
    wrap.addEventListener('dragstart',preventNativeDrag,true);
    wrap.addEventListener('click',suppressAfterDrag,true);
    var destroyTimer=window.setInterval(function(){
      if(wrap.isConnected)return;
      window.clearInterval(destroyTimer);cancelDrag();closeBlockContext();
      document.removeEventListener('contextmenu',openBlockContext,true);
      document.removeEventListener('click',clickBlockContext,true);
      document.removeEventListener('keydown',escapeBlockContext,true);
      document.removeEventListener('mousemove',dragMove,true);
      document.removeEventListener('mouseup',dragUp,true);
      document.removeEventListener('keydown',cancelOnKey,true);
    },600);
    drawEditor();
    if(window.__nvUnifiedV4RequestNativeSync)window.__nvUnifiedV4RequestNativeSync();
    return editor;
  }
  function observeEditor(){
    if(!enabled())return;
    var e=document.querySelector('.nv-live-editor');
    if(e&&(!editor||editor!==e||!e.querySelector('.nv-unified-editor')))getEditor();
    if(!e)editor=null;
  }
  function enableTemporary(value){
    if(value){window.localStorage.setItem(FLAG,'1');state=null;}
    else{window.localStorage.removeItem(FLAG);editor=null;}
    return enabled();
  }
  var api=window.__dulukaUnifiedMainMenu={
    storageKey:KEY,flagKey:FLAG,enabled:enabled,setEnabled:enableTemporary,
    get:get,set:set,reset:function(){return set(backupLegacy());},
    visible:visible,cell:cell,geometry:geometry,nativeGeometry:nativeGeometry,
    prepareForMotion:function(root){return enabled()&&nativeGeometry(root,true);},
    toBlock:function(k,p,i,t){return change(function(s){return M.toBlock(s,k,p,i,t);},'Control moved');},
    join:function(k,p,i){return change(function(s){return M.join(s,k,p,i);},'Grouped Controls');},
    changeType:function(p,i,t){return change(function(s){return M.changeType(s,p,i,t);},'Block Type updated');},
    remove:function(k){return change(function(s){return M.remove(s,k);},'Control removed');},
    openEditor:getEditor,observeEditor:observeEditor,lastError:function(){return lastError;},model:M
  };
  if(window.setInterval)window.setInterval(observeEditor,320);
})(window,document);
