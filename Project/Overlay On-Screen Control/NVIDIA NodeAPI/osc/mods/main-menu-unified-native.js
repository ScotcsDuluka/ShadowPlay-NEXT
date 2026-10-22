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
    var height=screen||Math.max(240,Math.min(h*.42,h-190));
    var group=window.__dulukaGroupLayoutA;
    if(group&&group.enabled&&group.enabled())
      return group.model.geometry(group.get(),Math.max(320,w-64),height);
    return M.layout(get(),Math.max(320,w-64),height,{gap:36});
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
    // ui-router may attach Main-menu before NVIDIA has laid out its host.
    // A zero-sized rect means its origin is still transient (often x=0).
    // Never persist a transform derived from that half-linked geometry.
    var rootRect=root.getBoundingClientRect&&root.getBoundingClientRect();
    if(rootRect&&(!rootRect.width||(typeof rootRect.height==='number'&&!rootRect.height)))return null;
    var bar=root.querySelector('md-menu-bar');if(!bar)return null;
    var screenW=window.innerWidth||1920,screenH=window.innerHeight||1080;
    var available=editor?Math.max(180,Math.min(screenH*.35,440)):Math.max(220,Math.min(screenH*.54,700));
    // In V5 Customize, native tiles must fit inside the transparent upper
    // viewing strip, NOT behind the opaque Settings content or its click layer.
    var group=window.__dulukaGroupLayoutA;
    var editing=document.documentElement&&document.documentElement.classList&&
      document.documentElement.classList.contains('nv-live-layout-editing');
    if(editing&&group&&group.enabled&&group.enabled()&&window.getComputedStyle){
      var settings=document.querySelector('.nv-live-editor');
      var rail=bar.parentElement||bar.offsetParent;
      if(settings&&rail&&settings.getBoundingClientRect&&rail.getBoundingClientRect){
        var css=window.getComputedStyle(settings);
        var band=parseFloat(css.getPropertyValue('--nv-top-preview-height'));
        var shellTop=settings.getBoundingClientRect().top+band;
        var railTop=rail.getBoundingClientRect().top+(bar.offsetTop||0);
        if(isFinite(band)&&band>0&&isFinite(railTop)&&railTop>=0&&railTop<shellTop){
          // geometry() reserves 40px vertical margin. Add it back so the final
          // scaled Group extent still ends eight pixels before the shell.
          available=Math.min(available,Math.max(52,shellTop-railTop-8+40));
        }
      }
    }
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
      var parentRect=railParent.getBoundingClientRect();
      if(!preMotion&&(!parentRect.width||(typeof parentRect.height==='number'&&!parentRect.height)))return null;
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
  function visible(){var group=window.__dulukaGroupLayoutA;
    if(group&&group.enabled&&group.enabled()){var r={};group.model.slots(group.get()).forEach(function(k){r[k]=true;});return r;}
    var s=get(),set={};M.PANELS.forEach(function(p){s.panels[p].forEach(function(b){b.controls.forEach(function(k){set[k]=true;});});});return set;
  }
  function cell(key){
    var g=geometry(550);return g.cells.filter(function(c){return c.key===key;})[0]||null;
  }
  function escapeText(s){return String(s).replace(/[&<>"']/g,function(ch){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch];});}
  function drawEditor(){
    if(!editor||!editor.isConnected)return;
    var s=get(),q=(editor.querySelector('.nv-unified-search')||{}).value||'';
    q=q.toLowerCase();
    M.PANELS.forEach(function(p){
      var el=editor.querySelector('.nv-unified-panel[data-panel="'+p+'"] .nv-unified-block-list'),html='';
      s.panels[p].forEach(function(b,i){
        html+='<article class="nv-unified-block" data-v4-count="'+b.controls.length+'" data-v4-block="'+p+':'+i+'"><div class="nv-unified-block-head">'+
          '<span class="nv-unified-grip" title="ลากเพื่อย้ายทั้ง Block" role="button" tabindex="0" aria-label="ย้าย Block">⠿</span>'+
          '<span class="nv-unified-block-meta"><strong>Block '+(i+1)+'</strong><small>'+({big:'Big',side:'Side',slot:'Side Slot'}[b.type])+' · '+b.controls.length+'/9</small></span>'+
          '<button type="button" class="nv-unified-block-options" data-v4-menu="'+p+':'+i+'" title="ตั้งค่า Block" aria-label="ตั้งค่า Block '+(i+1)+'">⋯</button></div><div class="nv-unified-members">';
        b.controls.forEach(function(k){
          html+='<div class="nv-unified-member-cell"><button title="'+escapeText(members[k].label)+'" class="nv-unified-member" data-v4-select="'+k+'" draggable="true" data-v4-drag="'+k+'"><md-icon class="share-icon '+members[k].icon+'"></md-icon><span>'+escapeText(members[k].label)+'</span></button>'+
            (k==='Settings'?'<span class="nv-unified-lock" title="Settings cannot be removed">◆</span>':
             '<button class="nv-unified-remove" data-v4-remove="'+k+'" aria-label="Remove '+escapeText(members[k].label)+'"><md-icon class="share-icon icon-app_close"></md-icon></button>')+'</div>';
        });
        html+='</div></article>';
      });
      el.innerHTML=html||'<p class="nv-unified-empty">ลาก Control มาวางที่แผงนี้</p>';
      editor.querySelector('.nv-unified-panel[data-panel="'+p+'"] .nv-unified-count').textContent=s.panels[p].reduce(function(n,b){return n+b.controls.length;},0)+' Controls';
    });
    var catalog=editor.querySelector('.nv-unified-palette');
    catalog.innerHTML=M.CONTROLS.filter(function(c){return !q||c.label.toLowerCase().indexOf(q)>=0||c.key.toLowerCase().indexOf(q)>=0;}).map(function(c){
      var pos=M.locate(s,c.key);return '<button data-v4-select="'+c.key+'" draggable="true" data-v4-drag="'+c.key+'" class="'+(selected===c.key?'selected ':'')+(pos?'placed':'unplaced')+'"><md-icon class="share-icon '+c.icon+'"></md-icon><span>'+escapeText(c.label)+'</span>'+(c.key==='Settings'?'<b>◆</b>':'')+'</button>';
    }).join('');
    var sel=editor.querySelector('.nv-unified-selection');sel.textContent=selected?'เลือก: '+members[selected].label+' · คลิก Block เพื่อจัดเข้ากลุ่ม':'ลากจากคลังลงแผง หรือคลิก Control แล้วคลิก Block';
    var msg=editor.querySelector('.nv-unified-message');if(msg)msg.textContent=lastError;
  }
  function getEditor(){
    if(!enabled())return null;
    var legacy=document.querySelector('.nv-live-editor');
    if(!legacy){editor=null;return null;}
    if(editor===legacy&&legacy.querySelector('.nv-unified-editor'))return editor;
    editor=legacy;editor.classList.add('nv-unified-editor-active');
    var description=legacy.querySelector('header p');
    if(description)description.textContent='ลาก Controls เพื่อจัดเมนู · คลิก ⋯ เพื่อเปลี่ยน Big / Side / Side Slot · 1 Block รองรับ 9 Controls';
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
    wrap.innerHTML='<div class="nv-unified-guide"><div class="nv-unified-guide-text"><strong>จัดวางเมนู</strong><span>ลากเพื่อย้าย · คลิก ⋯ บน Block เพื่อเปลี่ยนรูปแบบ · กด ESC เพื่อยกเลิกการลาก</span></div><button type="button" data-v4-undo aria-label="ย้อนกลับการจัดล่าสุด">↶ ย้อนกลับ</button></div>'+
      '<div class="nv-unified-controls"><header><h3>คลัง Controls</h3><span class="nv-unified-selection"></span></header>'+
      '<input type="search" class="nv-unified-search" placeholder="ค้นหา Controls..." aria-label="ค้นหา Controls"><div class="nv-unified-palette"></div></div>'+
      '<div class="nv-unified-panel-row">'+M.PANELS.map(function(p){
        return '<section class="nv-unified-panel" data-panel="'+p+'"><h3><span>'+({left:'LEFT',center:'CENTER',right:'RIGHT'}[p])+'</span><span class="nv-unified-count"></span></h3><div class="nv-unified-block-list"></div><button class="nv-unified-add-drop" data-v4-add="'+p+'" title="เลือก Control จากคลังแล้วคลิก หรือลากมาวาง">+ วางเพื่อสร้าง Block ใหม่</button></section>';
      }).join('')+'</div><footer class="nv-unified-message" role="status"></footer>';
    var old=legacy.querySelector('.nv-live-workspace');if(old)legacy.insertBefore(wrap,old);else legacy.appendChild(wrap);
    var pending='';
    wrap.addEventListener('input',function(ev){if(ev.target.classList.contains('nv-unified-search'))drawEditor();});
    var ribbon=wrap.querySelector('.nv-unified-palette');
    ribbon.addEventListener('wheel',function(ev){
      if(ribbon.scrollWidth<=ribbon.clientWidth)return;
      if(ev.cancelable)ev.preventDefault();
      ribbon.scrollLeft+=Math.abs(ev.deltaX)>Math.abs(ev.deltaY)?ev.deltaX:ev.deltaY;
    },{passive:false});
    wrap.addEventListener('click',function(ev){
      var target=ev.target.closest&&ev.target.closest('button');
      if(!target){
        var block=ev.target.closest&&ev.target.closest('[data-v4-block]');
        if(selected&&block&&!ev.target.closest('.nv-unified-grip')){
          var part=block.getAttribute('data-v4-block').split(':');
          change(function(s){return M.join(s,selected,part[0],Number(part[1]));},'เพิ่ม Control ลง Block แล้ว');
        }
        return;
      }
      var option=target.getAttribute('data-v4-menu');
      if(option){
        var index=option.split(':'),s=get();
        var block=s.panels[index[0]]&&s.panels[index[0]][Number(index[1])];
        if(block)showBlockContext({panel:index[0],index:Number(index[1])},block.controls[0],
          target.getBoundingClientRect().left,target.getBoundingClientRect().bottom+4);
        return;
      }
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
    function showBlockContext(where,key,x,y){
      closeBlockContext();
      var current=get(),block=current.panels[where.panel]&&current.panels[where.panel][where.index];
      if(!block)return;
      var names={big:'Block Big',side:'Block Side',slot:'Block Side Slot'};
      var popup=document.createElement('div');popup.className='nv-unified-context';
      popup.setAttribute('role','menu');popup.setAttribute('aria-label','ตั้งค่า Block');
      popup.innerHTML='<div class="nv-unified-context-title">Block '+(where.index+1)+
        '<small>'+where.panel.toUpperCase()+' · '+block.controls.length+'/9 Controls</small></div>'+
        '<div class="nv-unified-context-label">รูปแบบ Block</div>'+
        M.TYPES.map(function(type){
          return '<button type="button" role="menuitemradio" aria-checked="'+(block.type===type)+
            '" data-v4-context-type="'+type+'"><span>'+names[type]+'</span>'+
            (block.type===type?'<strong>✓</strong>':'')+'</button>';
        }).join('')+
        '<div class="nv-unified-context-label">ย้าย Block ไปแผง</div>'+
        M.PANELS.map(function(panel){
          return '<button type="button" data-v4-context-panel="'+panel+'"><span>'+panel.toUpperCase()+'</span>'+
            (panel===where.panel?'<strong>✓</strong>':'')+'</button>';
        }).join('')+
        (key!=='Settings'?'<button type="button" class="nv-unified-context-danger" data-v4-context-remove="'+key+'">นำ '+escapeText(members[key].label)+' ออก</button>':'');
      document.body.appendChild(popup);blockContextPopup=popup;
      var w=popup.offsetWidth||240,h=popup.offsetHeight||310;
      popup.style.left=Math.max(8,Math.min(x,(window.innerWidth||1920)-w-8))+'px';
      popup.style.top=Math.max(8,Math.min(y,(window.innerHeight||1080)-h-8))+'px';
      popup.__v4Panel=where.panel;popup.__v4Index=where.index;
    }
    function openBlockContext(event){
      if(!enabled()||!editor||!editor.isConnected||
        !document.documentElement.classList.contains('nv-live-layout-editing'))return;
      if(!event.target.closest)return;
      var nativeTile=event.target.closest('md-menu[data-nv-unified-key]');
      var editorBlock=event.target.closest('.nv-unified-block');
      var key,where;
      if(nativeTile){
        var nativeMenu=nativeTile.closest('nv-main-menu');
        if(!nativeMenu||nativeMenu.hasAttribute('data-customize-preview'))return;
        key=nativeTile.getAttribute('data-nv-unified-key');
        where=M.locate(get(),key);
      }else if(editorBlock&&editorBlock.closest('.nv-unified-editor')){
        var parts=editorBlock.getAttribute('data-v4-block').split(':');
        where={panel:parts[0],index:Number(parts[1])};
        key=get().panels[where.panel][where.index].controls[0];
      }
      if(!where)return;
      event.preventDefault();event.stopImmediatePropagation();
      showBlockContext(where,key,event.clientX,event.clientY);
    }
    function clickBlockContext(event){
      if(!blockContextPopup)return;
      var target=event.target.closest&&event.target.closest('[data-v4-context-type],[data-v4-context-panel],[data-v4-context-remove]');
      if(target&&blockContextPopup.contains(target)){
        event.preventDefault();event.stopImmediatePropagation();
        var panel=blockContextPopup.__v4Panel,index=blockContextPopup.__v4Index;
        var type=target.getAttribute('data-v4-context-type');
        var destination=target.getAttribute('data-v4-context-panel');
        var removeKey=target.getAttribute('data-v4-context-remove');
        closeBlockContext();
        if(type&&M.TYPES.indexOf(type)>=0)
          change(function(s){return M.changeType(s,panel,index,type);},'เปลี่ยนรูปแบบ Block แล้ว');
        else if(destination&&M.PANELS.indexOf(destination)>=0)
          change(function(s){return M.moveBlock(s,panel,index,destination,s.panels[destination].length);},'ย้าย Block แล้ว');
        else if(removeKey&&removeKey!=='Settings')
          change(function(s){return M.remove(s,removeKey);},'นำ Control ออกแล้ว');
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
    // Router and native Performance initialization are independent.
    // If they miss the first frame, heal an unfinished V4 menu without
    // moving an already positioned menu or resetting persisted controls.
    var root=document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container');
    if(root&&root.querySelectorAll&&
       root.querySelectorAll('md-menu.nv-unified-tile').length<Object.keys(visible()).length){
      nativeGeometry(root);
    }
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
  // ui-router can report main.main-menu before the directive has measured
  // its toolbar. Sync in the first visible frames of each internal return
  // (Preferences -> Main-menu), rather than only on the 500ms GPU poll.
  if(window.angular&&window.angular.module){
    window.angular.module('main').run(['$rootScope','$state',function($rootScope,$state){
      var settleRaf=null,maskTimer=null;
      function clearRouteMask(){
        document.documentElement.classList.remove('nv-v4-route-settling');
        if(maskTimer!==null){window.clearTimeout(maskTimer);maskTimer=null;}
      }
      function routeSettle(){
        if(!window.requestAnimationFrame){clearRouteMask();return;}
        if(settleRaf!==null&&window.cancelAnimationFrame)window.cancelAnimationFrame(settleRaf);
        var remaining=16;
        function frame(){
          settleRaf=null;
          if(!enabled()||!$state.current||$state.current.name!=='main.main-menu'){clearRouteMask();return;}
          var root=document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container');
          var result=root&&nativeGeometry(root);
          if(result&&root.querySelectorAll('md-menu.nv-unified-tile').length===Object.keys(visible()).length){
            var bar=root.querySelector('md-menu-bar'),box=bar&&bar.getBoundingClientRect();
            var correctX=(window.innerWidth-result.actualWidth)/2;
            if(box&&Math.abs(box.left-correctX)<=3)clearRouteMask();
          }
          if(--remaining>0)settleRaf=window.requestAnimationFrame(frame);
          else clearRouteMask();
        }
        settleRaf=window.requestAnimationFrame(frame);
      }
      $rootScope.$on('$stateChangeStart',function(e,to,params,from){
        if(enabled()&&to&&to.name==='main.main-menu'&&from&&
           /^main\.preferences(?:\.|$)/.test(from.name||'')){
          document.documentElement.classList.add('nv-v4-route-settling');
          if(maskTimer!==null)window.clearTimeout(maskTimer);
          // Never leave the menu invisible if a route fails or OSC closes.
          maskTimer=window.setTimeout(clearRouteMask,750);
        }else if(!to||to.name!=='main.main-menu')clearRouteMask();
      });
      $rootScope.$on('$stateChangeSuccess',function(e,to){
        if(to&&to.name==='main.main-menu')routeSettle();
        else clearRouteMask();
      });
      $rootScope.$on('$viewContentLoaded',function(){
        if($state.current&&$state.current.name==='main.main-menu')routeSettle();
      });
      $rootScope.$on('$stateChangeError',clearRouteMask);
    }]);
  }
})(window,document);
