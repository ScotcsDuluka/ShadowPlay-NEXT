(function () {
  'use strict';

  var entryId = 'nv-gpu-profiles-entry';
  var pageId = 'nv-gpu-profiles-page';
  var runningGamesUrl = '/Duluka/v1/gpu/running-games';
  var addGameUrl = '/Duluka/v1/gpu/profiles/add';
  var managerUrl = '/Duluka/v1/gpu/manager';
  var graphicsStatusUrl = '/Duluka/v1/gpu/graphics/status';
  var graphicsOpenUrl = '/Duluka/v1/gpu/graphics/open';
  var executablePickerUrl = '/Duluka/v1/gpu/file-picker/executable';
  var saveUrl = '/Duluka/v1/gpu/profiles/save';
  var removeUrl = '/Duluka/v1/gpu/profiles/remove';
  var applyUrl = '/Duluka/v1/gpu/preference/apply';
  var removePreferenceUrl = '/Duluka/v1/gpu/preference/remove';
  var tileMarker = '__nvGpuProfilesTile';
  var mainMenuTileMarker = '__nvGpuProfilesMainMenuTile';
  var mainMenuTileStyleId = 'nv-gpu-profiles-mainmenu-tile-style';
  var gameProfilesReturnState = 'main.preferences';
  var observer;
  var hookedMenus = [];
  var activeRoute = null;
  var readinessListenerInstalled = false;
  var pickerReturnState = null;
  var pickerReturnStatus = '';
  var pickerInProgress = false;
  var performanceSupportAvailable = null;
  var performanceSupportListenerRegistered = false;
  var liveLayoutUnsubscribe = null;

  function openGameProfilesFromMainMenu() {
    if (!window.angular) return;
    var injector = window.angular.element(document.documentElement).injector();
    if (!injector) return;
    gameProfilesReturnState = 'main.main-menu';
    return injector.get('$state').go('main.gpu-profiles');
  }

  function ensureMainMenuTileStyle() {
    var style = document.getElementById(mainMenuTileStyleId);
    if (!style) {
      style = document.createElement('style');
      style.id = mainMenuTileStyleId;
    }
    var css = '.main-menu-container{min-width:960px!important;}' +
      '.main-menu-container md-menu-bar{min-width:960px!important;}' +
      '.main-menu-container md-menu.preferences-tile.nv-gpu-profiles-left-tile {' +
      'left: -360px !important; right: auto !important; top: 160px !important; ' +
      'width: 80px !important; height: 80px !important; }' +
      '.main-menu-container md-menu.performance-tile{left:calc(100% + 40px)!important;right:auto!important;width:80px!important;}' +
      '.main-menu-container .performance-tile .gallery-tile-align{padding:0!important;justify-content:center;}' +
      '.main-menu-container .performance-tile img{width:40px!important;height:40px!important;}' +
      '.main-menu-container .performance-tile .right-tile-offset{display:none!important;}' +
      '.main-menu-container md-menu.mic-tile{top:0!important;right:-200px!important;width:80px!important;height:80px!important;}' +
      '.main-menu-container md-menu.camera-tile{top:0!important;right:-280px!important;width:80px!important;height:80px!important;}' +
      '.main-menu-container .mic-tile md-icon,.main-menu-container .camera-tile md-icon{width:40px!important;height:40px!important;font-size:40px!important;}' +
      '.main-menu-container md-menu.preferences-tile,.main-menu-container md-menu.nv-settings-menu-tile{position:absolute!important;left:auto!important;top:160px!important;right:-280px!important;width:240px!important;height:80px!important;}' +
      '.main-menu-container .nv-settings-menu-tile nv-osc-tile>div{position:relative;height:74px;}' +
      '.main-menu-container .nv-settings-menu-tile .osc-tile-header{position:absolute;left:68px;top:0;height:74px!important;display:flex;align-items:center;}' +
      '.main-menu-container .nv-settings-menu-tile .osc-tile-header h2{font-size:18px;margin:0;}' +
      '.main-menu-container .nv-settings-menu-tile .osc-tile-icon{position:absolute;left:20px;top:21px;width:32px;height:32px;}' +
      '.main-menu-container .nv-settings-menu-tile .osc-tile-icon md-icon{width:32px;height:32px;font-size:32px;}' +
      '.main-menu-container .nv-settings-menu-tile .osc-tile-footer{display:none!important;}' +
      '.main-menu-container md-menu.nv-game-profiles-tile:not([data-nv-next-control]){display:none!important;}' +
      '';
    if (style.textContent !== css) style.textContent = css;
    if (!style.parentNode) (document.head || document.documentElement).appendChild(style);
  }

  // Reposition the original NVIDIA md-menu elements without replacing any
  // Angular controllers, click actions, or focus/keyboard behavior.
  // Route transitions recreate the native root at 0x0 for a short time.
  // Give the first V4 route render a few animation frames to settle, rather
  // than depending on the next unrelated 500ms performance polling tick.
  var v4RouteRoot=null,v4RouteRaf=null,v4RouteFrames=0;
  function syncNativeMainMenuSlots(root) {
    var next=window.__nvNextLayoutV5;
    if(next&&next.enabled&&next.enabled()){
      // Geometry has one owner, including the transient edit lease.
      if(v4RouteRaf!==null&&window.cancelAnimationFrame)window.cancelAnimationFrame(v4RouteRaf);
      v4RouteRaf=null;v4RouteRoot=null;v4RouteFrames=0;
      next.sync();
      return;
    }
    var unified=window.__dulukaUnifiedMainMenu;
    if(unified&&unified.enabled()){
      if(v4RouteRoot!==root){
        if(v4RouteRaf!==null&&window.cancelAnimationFrame)window.cancelAnimationFrame(v4RouteRaf);
        v4RouteRoot=root;v4RouteRaf=null;v4RouteFrames=0;
      }
      unified.nativeGeometry(root);
      if(v4RouteFrames<12&&v4RouteRaf===null&&window.requestAnimationFrame){
        v4RouteRaf=window.requestAnimationFrame(function(){
          v4RouteRaf=null;
          if(root!==v4RouteRoot||root.isConnected===false)return;
          v4RouteFrames++;
          syncNativeMainMenuSlots(root);
        });
      }
      return;
    }
    var api=window.__dulukaMenuLayout;if(!api||!root)return;
    var map={};api.items.forEach(function(item){map[item.key]=item.name;});
    function setStyle(element,key,value){if(element.style.getPropertyValue(key)!==value||element.style.getPropertyPriority(key)!=='important')element.style.setProperty(key,value,'important');}
    // A side panel disappears entirely when all of its blocks are removed.
    // The original NVIDIA controls are retained; only their pixel geometry moves.
    var bar=root.querySelector('md-menu-bar'),layout=api.get();
    var sides=api.getSides?api.getSides():{left:layout.left.map(function(k){return [k];}),right:[['Performance','Mic','Camera'],['Gallery'],['Settings']]};
    var centerCount=api.centerColumns?api.centerColumns(layout):layout.center.length,hasLeft=sides.left.length>0,hasCenter=centerCount>0;
    var total=240+(hasLeft?280:0)+(hasCenter?240*centerCount+40:0);
    var begin=((window.innerWidth||1920)-total)/2;
    var barX=begin+(hasLeft?280:0),rightX=hasCenter?centerCount*240+40:0;
    if(bar){
      // The native container briefly starts at x=0 during the first Angular
      // render, then moves to its CSS-centered position. Anchoring the rail
      // against that transient rect caused a +440px slide on first open.
      // Use the container's settled CSS center instead of its current x.
      var rootWidth=root.getBoundingClientRect?root.getBoundingClientRect().width:1040;
      // The native root can be zero-sized while Angular is linking the view.
      // Never use that transient width to position or animate the rail.
      if(!isFinite(rootWidth)||rootWidth<960)rootWidth=1040;
      var stableBarOrigin=((window.innerWidth||1920)-rootWidth)/2+40;
      setStyle(bar,'width',(centerCount*240)+'px');
      setStyle(bar,'min-width',(centerCount*240)+'px');
      setStyle(bar,'transform','translateX('+(barX-stableBarOrigin)+'px)');
    }
    function apply(element,x,y,w,h,small){
      if(!element)return;
      setStyle(element,'position','absolute');setStyle(element,'left',x+'px');setStyle(element,'top',y+'px');
      setStyle(element,'width',w+'px');setStyle(element,'height',h+'px');setStyle(element,'right','auto');
      if(element.classList.contains('nv-side-small-tile')!==small)element.classList[small?'add':'remove']('nv-side-small-tile');
      if(element.classList.contains('nv-side-full-tile')===small)element.classList[small?'remove':'add']('nv-side-full-tile');
    }
    function groups(rows,offset,right){
      rows.forEach(function(row,r){row.forEach(function(key,c){
        var nativeMap={Performance:'performance-tile',Mic:'mic-tile',Camera:'camera-tile',Gallery:'gallery-tile',Settings:'nv-settings-menu-tile'};
        var name=right&&nativeMap[key]?nativeMap[key]:map[key];
        var element=root.querySelector('md-menu.'+name);
        if(element){
          if(!right&&!element.classList.contains('nv-flex-tile'))element.classList.add('nv-flex-tile');
          apply(element,offset+c*240/row.length,r*80,240/row.length,80,row.length>1);
          if(right){
            element.setAttribute('data-nv-side-key',key);
            if(key==='Performance'||key==='Mic'||key==='Camera'){
              var button=element.querySelector&&element.querySelector('button.menu-button');
              if(button){
                var info=button.querySelector('.nv-side-native-info');
                if(!info){
                  info=document.createElement('span');
                  info.className='nv-side-native-info';
                  var title=document.createElement('strong'),sub=document.createElement('small');
                  info.appendChild(title);info.appendChild(sub);button.appendChild(info);
                }
                var labels={Performance:'Performance',Mic:'Microphone',Camera:'Camera'};
                var titleText=labels[key],subText='';
                var tileState=window.angular&&window.angular.element(root).scope();
                while(tileState&&!tileState.mainMenu)tileState=tileState.$parent;
                var nativeTile=tileState&&tileState.mainMenu&&tileState.mainMenu.tiles&&tileState.mainMenu.tiles[key];
                if(nativeTile)subText=nativeTile.shortcut||(nativeTile.state&&nativeTile.state[nativeTile.currentState]||{}).status||nativeTile.currentState||'';
                if(info.firstChild.textContent!==titleText)info.firstChild.textContent=titleText;
                if(info.lastChild.textContent!==subText)info.lastChild.textContent=subText;
                if(info.style.display!==(row.length===1?'block':'none'))info.style.display=row.length===1?'block':'none';
              }
            }
          }
        }
      });});
    }
    groups(sides.left,-280,false);
    api.cells(layout).forEach(function(cell){
      if(cell.group!=='center')return;
      var element=root.querySelector('md-menu.'+map[cell.key]);
      if(!element)return;
      if(!element.classList.contains('nv-flex-tile'))element.classList.add('nv-flex-tile');
      apply(element,cell.x,cell.y,cell.width,cell.height,false);
    });
    groups(sides.right,rightX,true);
  }

  function updateMainMenuPerformance() {
    // Customize can be opened directly, without ever visiting Main-menu.
    ensureMainMenuTileStyle();
    var root = document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container');
    if (!root || !window.angular) return;
    var scope = window.angular.element(root).scope();
    while (scope && !scope.mainMenu) scope = scope.$parent;
    if (!scope || !scope.mainMenu || !scope.mainMenu.tiles) return;

    var tiles = scope.mainMenu.tiles;
    var changed = false;
    if(!liveLayoutUnsubscribe&&window.__dulukaMenuLayout)liveLayoutUnsubscribe=window.__dulukaMenuLayout.subscribe(function(){updateMainMenuPerformance();});
    var nextOwner = window.__nvNextLayoutV5;
    var profilesVisible = !!(nextOwner && nextOwner.enabled && nextOwner.enabled() &&
      nextOwner.model && nextOwner.model.CONTROLS.indexOf('GameProfiles') >= 0);
    if (!tiles.GameProfiles || !tiles.GameProfiles[mainMenuTileMarker]) {
      tiles.GameProfiles = {
        name:'nv-game-profiles-tile',extraclass:'nv-gpu-profiles-mainmenu-tile',
        title:'Game Profiles',icon:'icon-edit_tune',initialFocus:'false',
        visible:profilesVisible,enabled:true,currentState:'Ready',
        state:{Ready:{icon:'icon-edit_tune',status:'',statusBrush:'',items:[]}},
        click:openGameProfilesFromMainMenu
      };
      tiles.GameProfiles[mainMenuTileMarker] = true;
      changed = true;
    }
    if (tiles.GameProfiles.visible !== profilesVisible) {
      tiles.GameProfiles.visible = profilesVisible;
      changed = true;
    }

    var performance = tiles.Performance;
    if (!performance) {
      if (changed) scope.$applyAsync();
      return;
    }

    if (!performance.visible && (!window.__dulukaMainMenuVisibility || window.__dulukaMainMenuVisibility.Performance !== false)) {
      performance.visible = true;
      changed = true;
    }
    if (performanceSupportAvailable !== null &&
        performance.enabled !== performanceSupportAvailable) {
      performance.enabled = performanceSupportAvailable;
      changed = true;
    }
    ['Performance', 'Gallery', 'Mic', 'Camera'].forEach(function (name) {
      var tile = tiles[name];
      if (tile && tile.extraclass !== 'three-right') {
        tile.extraclass = 'three-right';
        changed = true;
      }
    });
    var quietMode = tiles.QuietMode2;
    if (quietMode) {
      if (typeof quietMode.__dulukaAvailable !== 'boolean') {
        quietMode.__dulukaAvailable = quietMode.visible === true;
      }
      if (quietMode.visible !== false) {
        quietMode.visible = false;
        changed = true;
      }
    }
    var settings = tiles.Preferences;
    if (settings && !settings.__dulukaSettingsSubmenu) {
      var injector = window.angular.element(document.documentElement).injector();
      settings.name = 'nv-settings-menu';
      settings.extraclass = 'nv-settings-menu-tile';
      settings.title = 'Settings';
      settings.currentState = 'Menu';
      settings.state = { Menu: {icon:'icon-settings',status:'',items:[
        {id:'settings',name:'Settings',icon:'icon-settings',visible:true,enabled:true,
          click:function () { return injector.get('$state').go('main.preferences'); }},
        {id:'customize-main-menu',name:'Customize Main-menu',icon:'icon-share',visible:true,enabled:true,
          click:function () { if(window.__dulukaStartMainMenuEditor)return window.__dulukaStartMainMenuEditor(); }},
        {id:'quiet-mode',name:'Whisper Mode',icon:'icon-volume_off',visible:false,enabled:false,
          click:function () {
            var current = scope.mainMenu.tiles.QuietMode2;
            if (current && typeof current.click === 'function') return current.click();
          }},
        {id:'overlay-fps',name:'Overlay FPS',icon:'icon-performance',visible:true,enabled:true,
          click:function () { return injector.get('$state').go('main.osc-fps'); }},
        {id:'renderer-mode',name:'Renderer Mode',icon:'icon-performance',visible:true,enabled:true,
          click:function () { return injector.get('$state').go('main.osc-renderer'); }}
      ]}};
      delete settings.click;
      settings.__dulukaSettingsSubmenu = true;
      changed = true;
    }
    if (settings && settings.__dulukaSettingsSubmenu) {
      // Retire an already-linked submenu entry without replacing its array or
      // changing the other native actions while Material has the menu open.
      for (var profileIndex = settings.state.Menu.items.length - 1; profileIndex >= 0; profileIndex--) {
        if (settings.state.Menu.items[profileIndex].id === 'game-profiles') {
          settings.state.Menu.items.splice(profileIndex, 1);
          changed = true;
        }
      }
      if (!settings.state.Menu.items.some(function (item) { return item.id === 'overlay-fps'; })) {
        settings.state.Menu.items.push({id:'overlay-fps',name:'Overlay FPS',icon:'icon-performance',visible:true,enabled:true,
          click:function () { return window.angular.element(document.documentElement).injector().get('$state').go('main.osc-fps'); }});
        changed = true;
      }
      if (!settings.state.Menu.items.some(function (item) { return item.id === 'renderer-mode'; })) {
        settings.state.Menu.items.push({id:'renderer-mode',name:'Renderer Mode',icon:'icon-performance',visible:true,enabled:true,
          click:function () { return window.angular.element(document.documentElement).injector().get('$state').go('main.osc-renderer'); }});
        changed = true;
      }
      var quietItem = settings.state.Menu.items.filter(function (item) { return item.id === 'quiet-mode'; })[0];
      var customizeItem = settings.state.Menu.items.filter(function (item) { return item.id === 'customize-main-menu'; })[0];
      if (customizeItem && customizeItem.icon !== 'icon-share') {
        customizeItem.icon = 'icon-share';
        changed = true;
      }
      if (quietItem) {
        var available = !!(quietMode && quietMode.__dulukaAvailable);
        if (quietItem.visible !== available) { quietItem.visible = available; changed = true; }
        if (quietItem.enabled !== available) { quietItem.enabled = available; changed = true; }
      }
    }
    // Gallery and Settings share the same native md-menu-bar positioning context.
    // The scoped CSS anchors Settings with right:-280px, top:160px, underneath
    // Gallery. Measuring during the first Angular digest created stale inline
    // positions and sometimes covered Instant Replay; never pin pixel offsets.
    var visibility = window.__dulukaMainMenuVisibility || {};
    var unified=window.__dulukaUnifiedMainMenu;
    var nextV5=window.__nvNextLayoutV5;
    if(nextV5&&nextV5.enabled&&nextV5.enabled()){
      // Stale V2 visibility must not unlink potential V5 drop targets.
      visibility={Preferences:true};
      nextV5.model.CONTROLS.forEach(function(key){visibility[key==='Settings'?'Preferences':key]=true;});
    }else if(unified&&unified.enabled()){var present=unified.visible(),next={Preferences:!!present.Settings};unified.model.CONTROLS.forEach(function(c){next[c.key]=!!present[c.key];});visibility=next;}
    ['Performance','Mic','Camera','Gallery','Preferences'].forEach(function(name){var desired=name==='Preferences'||visibility[name]!==false;if(tiles[name]&&tiles[name].visible!==desired){tiles[name].visible=desired;changed=true;}});
    Object.keys(visibility).forEach(function (name) {
      if (['Performance','Mic','Camera','Gallery','Preferences'].indexOf(name)>=0 || typeof visibility[name] !== 'boolean' || !tiles[name]) return;
      if (tiles[name].visible !== visibility[name]) { tiles[name].visible = visibility[name]; changed = true; }
    });
    if (changed) scope.$applyAsync();
    syncNativeMainMenuSlots(root);
    // Keep the native control's tooltip and accessible name localized by the
    // existing phrase bridge, without changing the OSC's menu template.
    var button = root.querySelector('.nv-gpu-profiles-mainmenu-tile .menu-button');
    if (button) {
      button.title = 'Game Profiles';
      button.setAttribute('aria-label', 'Game Profiles');
    }
  }

  window.__nvUnifiedV4RequestNativeSync=updateMainMenuPerformance;

  function registerPerformanceSupportListener() {
    if (performanceSupportListenerRegistered || !window.angular) return;
    var injector = window.angular.element(document.documentElement).injector();
    if (!injector) return;
    injector.get('eventAggregator').on('FEATURE_SUPPORT_STATE_CHANGED', function (available) {
      performanceSupportAvailable = available === true;
      updateMainMenuPerformance();
    });
    performanceSupportListenerRegistered = true;
  }

  function onPreferencesHome() {
    return /#\/(?:base|main)\/preferences(?:[?#]|$)/.test(window.location.hash);
  }

  function postJson(url, body) {
    return requestJson(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
  }

  function setLayout(element, direction) {
    element.setAttribute('layout', direction);
  }

  function makeNativeItem(name, detail, selected, iconDataUri) {
    var item = document.createElement('div');
    item.className = 'preferences-item hover-list-item layout-row nv-gpu-list-item';
    setLayout(item, 'row');
    if (selected) {
      item.classList.add('hover-list-item-focus');
      item.dataset.selected = 'true';
    }
    item.setAttribute('role', 'button');
    item.tabIndex = 0;
    var icon = iconDataUri ? document.createElement('img') : document.createElement('md-icon');
    icon.className = iconDataUri
      ? 'preferences-item-icon nv-gpu-exe-icon'
      : 'share-icon icon-normal preferences-item-icon icon-edit_tune';
    if (iconDataUri) {
      icon.src = iconDataUri;
      icon.alt = '';
      icon.setAttribute('aria-hidden', 'true');
    }
    item.appendChild(icon);
    var text = document.createElement('div');
    text.className = 'nv-gpu-list-item-text';
    text.setAttribute('layout', 'column');
    text.setAttribute('layout-align', 'center start');
    var label = document.createElement('div');
    label.className = 'preferences-item-label';
    label.textContent = name;
    var sublabel = document.createElement('div');
    sublabel.className = 'preferences-item-label preferences-item-sublabel';
    var sublabelText = document.createElement('span');
    sublabelText.textContent = detail || '';
    sublabel.appendChild(sublabelText);
    text.appendChild(label);
    text.appendChild(sublabel);
    item.appendChild(text);
    item.addEventListener('mouseenter', function () { item.focus(); });
    item.addEventListener('mouseleave', function () { item.blur(); });
    item.addEventListener('focus', function () {
      item.classList.add('hover-list-item-focus');
    });
    item.addEventListener('blur', function () {
      if (!item.dataset.selected) item.classList.remove('hover-list-item-focus');
    });
    return item;
  }

  function fetchWithTimeout(url, options, timeoutMs) {
    var limit = Number(timeoutMs) > 0 ? Number(timeoutMs) : 12000;
    var controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
    var requestOptions = {};
    Object.keys(options || {}).forEach(function (key) { requestOptions[key] = options[key]; });
    if (controller) requestOptions.signal = controller.signal;
    var timer;
    var request = fetch(url, requestOptions);
    var timeout = new Promise(function (_, reject) {
      timer = window.setTimeout(function () {
        if (controller) controller.abort();
        reject(new Error('Request timed out after ' + limit + ' ms: ' + url));
      }, limit);
    });
    return Promise.race([request, timeout]).then(function (response) {
      window.clearTimeout(timer);
      return response;
    }, function (error) {
      window.clearTimeout(timer);
      throw error;
    });
  }
  function requestJson(url, options, timeoutMs) {
    return fetchWithTimeout(url, options, timeoutMs || 17000).then(function (response) {
      return response.json().catch(function () {
        throw new Error('NvContainer returned an invalid response.');
      }).then(function (body) {
        if (!response.ok) throw new Error(body.error || 'Request failed.');
        return body;
      });
    });
  }

  function showEntry() {
    if (!onPreferencesHome()) return;
    gameProfilesReturnState = 'main.preferences';
    var container = document.querySelector('.preferences-item-container');
    if (!container || !window.angular) return;

    var scope = window.angular.element(container).scope();
    while (scope && !scope.preferencesMenu) scope = scope.$parent;
    if (!scope || !Array.isArray(scope.preferencesMenu.tiles)) return;

    var menu = scope.preferencesMenu;
    function ensureTile() {
      if (!Array.isArray(menu.tiles)) return null;
      var tile = menu.tiles.filter(function (item) { return item && item[tileMarker]; })[0];
      if (tile) return tile;
      tile = {
        name: 'nv-gpu-profiles-tile',
        title: 'Game profiles',
        subtitle: 'Add and manage games',
        icon: 'icon-edit_tune',
        state: 'main.gpu-profiles',
        visible: true
      };
      tile[tileMarker] = true;
      var hudIndex = menu.tiles.findIndex(function (item) {
        return item && item.name === 'overlays-tile';
      });
      menu.tiles.splice(hudIndex >= 0 ? hudIndex + 1 : menu.tiles.length, 0, tile);
      scope.$applyAsync();
      return tile;
    }

    if (hookedMenus.indexOf(menu) === -1 &&
        typeof menu.initializePreferencesMenu === 'function') {
      var initialize = menu.initializePreferencesMenu;
      menu.initializePreferencesMenu = function () {
        var result = initialize.apply(this, arguments);
        ensureTile();
        return result;
      };
      hookedMenus.push(menu);
    }
    var tile = ensureTile();
    if (!tile) {
      return;
    }
    if (!readinessListenerInstalled) {
      window.addEventListener('nvgraphics-readiness-change', function (event) {
        tile.subtitle = event.detail && event.detail.ready
          ? 'Add and manage games'
          : 'Waiting for NvGraphics to be ready';
        scope.$applyAsync();
      });
      readinessListenerInstalled = true;
    }

    Array.prototype.forEach.call(container.children, function (item) {
      var itemScope = window.angular.element(item).scope();
      while (itemScope && itemScope.tile !== tile) itemScope = itemScope.$parent;
      if (!itemScope || itemScope.tile !== tile) return;
      item.id = entryId;
      item.dataset.nvGpuProfilesTile = 'true';
    });
  }

  function makePage() {
    var page = document.createElement('div');
    page.id = pageId;
    page.className = 'osc-content layout-row nv-gpu-profiles-page';
    setLayout(page, 'row');

    var center = document.createElement('div');
    center.className = 'osc-central-div layout-column nv-gpu-profiles-view';
    setLayout(center, 'column');
    var panel = document.createElement('div');
    panel.className = 'preferences-root-panel nv-gpu-profiles-panel';
    var heading = document.createElement('div');
    heading.className = 'preferences-root-label';
    var headingText = document.createElement('p');
    headingText.textContent = 'Game profiles';
    heading.appendChild(headingText);
    var description = document.createElement('div');
    description.className = 'preferences-instructional-text';
    description.textContent = 'Games added here are shared with NvGraphics. Adding a game does not change its Windows GPU preference.';
    var savedHeading = document.createElement('div');
    savedHeading.className = 'broadcast-lvl1-label';
    savedHeading.textContent = 'Game profiles';
    var gameList = document.createElement('div');
    gameList.className = 'preferences-item-container nv-gpu-profile-list';
    gameList.setAttribute('aria-live', 'polite');
    var runningHeading = document.createElement('div');
    runningHeading.className = 'broadcast-lvl1-label';
    runningHeading.textContent = 'Running games';
    var runningList = document.createElement('div');
    runningList.className = 'preferences-item-container nv-gpu-running-list';
    runningList.setAttribute('aria-live', 'polite');
    var listsRow = document.createElement('div');
    listsRow.className = 'nv-gpu-lists-row';
    setLayout(listsRow, 'row');
    var profilesColumn = document.createElement('div');
    profilesColumn.className = 'nv-gpu-list-column nv-gpu-profiles-column';
    var runningColumn = document.createElement('div');
    runningColumn.className = 'nv-gpu-list-column nv-gpu-running-column';
    var removeProfileButton = document.createElement('button');
    removeProfileButton.className = 'osc-button osc-button-gray nv-gpu-list-action';
    removeProfileButton.textContent = 'Remove selected profile';
    removeProfileButton.disabled = true;
    var addButton = document.createElement('button');
    addButton.className = 'osc-button osc-button-green nv-gpu-list-action';
    addButton.textContent = 'Add selected game as profile';
    addButton.disabled = true;
    profilesColumn.appendChild(savedHeading);
    profilesColumn.appendChild(gameList);
    profilesColumn.appendChild(removeProfileButton);
    runningColumn.appendChild(runningHeading);
    runningColumn.appendChild(runningList);
    runningColumn.appendChild(addButton);
    listsRow.appendChild(profilesColumn);
    listsRow.appendChild(runningColumn);
    var detailHeading = document.createElement('div');
    detailHeading.className = 'broadcast-lvl1-label';
    detailHeading.textContent = 'Profile settings';
    var graphicsHeading = document.createElement('div');
    graphicsHeading.className = 'broadcast-lvl1-label';
    graphicsHeading.textContent = 'Graphics for this profile';
    var graphicsHelp = document.createElement('div');
    graphicsHelp.className = 'preferences-instructional-text';
    graphicsHelp.textContent = 'Apply changes saves this profile and sets the Windows GPU preference. System default removes any Windows GPU override.';
    function makeTextField(labelText, input) {
      var field = document.createElement('div');
      field.className = 'broadcast-items-spacer layout-row';
      setLayout(field, 'row');
      var label = document.createElement('div');
      label.className = 'broadcast-lvl2-label nv-gpu-field-label';
      label.textContent = labelText;
      var inputBox = document.createElement('div');
      inputBox.className = 'broadcast-overlay-placeholder layout-row nv-gpu-input-box';
      setLayout(inputBox, 'row');
      inputBox.appendChild(input);
      field.appendChild(label);
      field.appendChild(inputBox);
      return field;
    }
    var nameInput = document.createElement('input');
    nameInput.className = 'broadcast-overlay-text';
    nameInput.type = 'text';
    nameInput.maxLength = 128;
    nameInput.placeholder = 'Game name';
    var pathInput = document.createElement('input');
    pathInput.className = 'recordings-paths';
    pathInput.type = 'text';
    pathInput.readOnly = true;
    pathInput.placeholder = 'Select an executable file';
    var pathField = document.createElement('div');
    pathField.className = 'broadcast-items-spacer layout-row';
    setLayout(pathField, 'row');
    var pathLabel = document.createElement('div');
    pathLabel.className = 'broadcast-lvl2-label nv-gpu-field-label';
    pathLabel.textContent = 'Executable path';
    var pathRow = document.createElement('div');
    pathRow.className = 'layout-row nv-gpu-path-row';
    setLayout(pathRow, 'row');
    var pathOuter = document.createElement('div');
    pathOuter.className = 'recordings-paths-outer';
    pathOuter.setAttribute('flex', '');
    var pathInner = document.createElement('div');
    pathInner.className = 'recordings-paths-inner';
    pathInner.appendChild(pathInput);
    pathOuter.appendChild(pathInner);
    var browseButton = document.createElement('div');
    browseButton.className = 'recordings-ellipsis-outer hover-list-item';
    browseButton.setAttribute('hover-focus', '');
    browseButton.tabIndex = 0;
    browseButton.setAttribute('role', 'button');
    browseButton.setAttribute('aria-label', 'Browse for executable');
    browseButton.setAttribute('title', 'Browse for executable');
    var browseButtonContent = document.createElement('div');
    browseButtonContent.className = 'recordings-ellipsis-nested';
    var browseButtonText = document.createElement('span');
    browseButtonText.textContent = '...';
    browseButtonContent.appendChild(browseButtonText);
    browseButton.appendChild(browseButtonContent);
    pathRow.appendChild(pathOuter);
    pathRow.appendChild(browseButton);
    pathField.appendChild(pathLabel);
    pathField.appendChild(pathRow);
    var gpuSelect = document.createElement('md-select');
    gpuSelect.className = 'osc-select broadcast-ingest-select';
    gpuSelect.setAttribute('hover-focus', '');
    gpuSelect.setAttribute('ng-model', 'gpuProfiles.adapterId');
    gpuSelect.setAttribute('ng-change', 'gpuProfiles.adapterChanged()');
    gpuSelect.setAttribute('aria-label', 'Graphics processor');
    var noAdapter = document.createElement('md-option');
    noAdapter.setAttribute('ng-value', "''");
    noAdapter.setAttribute('hover-focus', '');
    noAdapter.textContent = '(system default)';
    gpuSelect.appendChild(noAdapter);
    var adapterOption = document.createElement('md-option');
    adapterOption.setAttribute('ng-repeat', 'adapter in gpuProfiles.adapters');
    adapterOption.setAttribute('ng-value', 'adapter.adapterId');
    adapterOption.setAttribute('hover-focus', '');
    adapterOption.textContent = '{{adapter.name}}';
    gpuSelect.appendChild(adapterOption);
    var detail = document.createElement('div');
    detail.className = 'nv-gpu-profile-fields';
    detail.appendChild(makeTextField('Game name', nameInput));
    detail.appendChild(pathField);
    var gpuField = document.createElement('div');
    gpuField.className = 'broadcast-items-spacer layout-row';
    setLayout(gpuField, 'row');
    var gpuLabel = document.createElement('div');
    gpuLabel.className = 'broadcast-lvl2-label';
    gpuLabel.className = 'broadcast-lvl2-label nv-gpu-field-label';
    gpuLabel.textContent = 'Graphics processor';
    gpuField.appendChild(gpuLabel);
    var gpuSelectContainer = document.createElement('md-input-container');
    gpuSelectContainer.className = 'broadcast-ingest-container';
    gpuSelectContainer.setAttribute('flex', '');
    gpuSelectContainer.classList.add('nv-gpu-select-container');
    gpuSelectContainer.appendChild(gpuSelect);
    gpuField.appendChild(gpuSelectContainer);
    var graphicsSection = document.createElement('div');
    graphicsSection.className = 'nv-gpu-graphics-section';
    graphicsSection.appendChild(graphicsHeading);
    graphicsSection.appendChild(graphicsHelp);
    graphicsSection.appendChild(gpuField);
    var status = document.createElement('div');
    status.className = 'preferences-instructional-text nv-gpu-status';
    status.setAttribute('role', 'status');
    var applyButton = document.createElement('button');
    applyButton.className = 'osc-button osc-button-green nv-gpu-profile-apply';
    applyButton.textContent = 'Apply changes';
    applyButton.disabled = true;
    panel.appendChild(heading);
    panel.appendChild(description);
    panel.appendChild(status);
    panel.appendChild(listsRow);
    panel.appendChild(detailHeading);
    panel.appendChild(detail);
    panel.appendChild(graphicsSection);
    panel.appendChild(applyButton);
    center.appendChild(panel);

    var buttons = document.createElement('div');
    buttons.className = 'osc-side-buttons layout-column nv-gpu-actions';
    setLayout(buttons, 'column');
    var fullUiButton = document.createElement('button');
    fullUiButton.className = 'osc-button osc-button-green';
    fullUiButton.textContent = 'Open full UI';
    var refreshButton = document.createElement('button');
    refreshButton.className = 'osc-button osc-button-green';
    refreshButton.textContent = 'Refresh Running Games';
    var backButton = document.createElement('button');
    backButton.className = 'osc-button osc-button-gray';
    backButton.textContent = 'Back';
    buttons.appendChild(fullUiButton);
    buttons.appendChild(refreshButton);
    buttons.appendChild(backButton);
    Array.prototype.forEach.call(page.querySelectorAll('button'), function (button) {
      button.type = 'button';
      button.setAttribute('hover-focus', '');
      button.addEventListener('focus', function () { button.classList.add('hover-focus'); });
      button.addEventListener('blur', function () {
        if (!button.matches(':hover')) button.classList.remove('hover-focus');
      });
      button.addEventListener('mouseenter', function () { button.classList.add('hover-focus'); });
      button.addEventListener('mouseleave', function () {
        if (document.activeElement !== button) button.classList.remove('hover-focus');
      });
    });
    Array.prototype.forEach.call(page.querySelectorAll('button'), function (button) {
      button.addEventListener('keydown', function (event) {
        var all = Array.prototype.filter.call(page.querySelectorAll('button'), function (item) {
          return item.tabIndex === 0 && !item.disabled;
        });
        var index = all.indexOf(button);
        if (index < 0) return;
        var next = event.key === 'ArrowDown' || event.key === 'ArrowRight' ? index + 1 :
          (event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? index - 1 : -1);
        if (event.key === 'Home') next = 0;
        if (event.key === 'End') next = all.length - 1;
        if (next < 0 || next >= all.length) return;
        event.preventDefault();
        all[next].focus();
      });
    });
    page.appendChild(center);
    page.appendChild(buttons);

    var selectedGame = null;
    var selectedProfile = null;
    var games = [];
    var profiles = [];
    var profileApplying = false;
    var angularScope = null;
    function selectedAdapterId() {
      return angularScope ? angularScope.gpuProfiles.adapterId || '' : '';
    }

    function displayAdapterName(name) {
      return String(name || '').replace(/\s*\[GPU\s*\d+\]\s*/gi, ' ').trim();
    }
    function updateAngularModel(update) {
      if (!angularScope || angularScope.$$destroyed) return;
      update(angularScope.gpuProfiles);
      angularScope.$evalAsync(updateProfileButtons);
    }
    function compilePage() {
      var injector = window.angular.element(document.documentElement).injector();
      if (!injector) throw new Error('NVIDIA OSC Angular injector is unavailable.');
      angularScope = injector.get('$rootScope').$new();
      angularScope.gpuProfiles = {
        adapterId: '',
        adapters: [],
        adapterChanged: updateProfileButtons,
      };
      injector.get('$compile')(page)(angularScope);
      angularScope.$digest();
    }
    function setStatus(message, error) {
      status.textContent = message || '';
      status.classList.toggle('highlights-item-disabled', Boolean(error));
    }

    function updateProfileButtons() {
      var enabled = Boolean(selectedProfile);
      var matchesSelected = enabled && pathInput.value.trim().toLowerCase() ===
        String(selectedProfile.exePath || '').toLowerCase();
      var hasPath = Boolean(pathInput.value.trim());
      applyButton.disabled = !hasPath || profileApplying;
      applyButton.textContent = 'Apply changes';
      removeProfileButton.disabled = !matchesSelected;
      removeProfileButton.textContent = selectedProfile && selectedProfile.windowsGraphicsOnly
        ? 'Remove Windows entry'
        : 'Remove selected profile';
      applyButton.title = hasPath ? '' : 'Choose an executable before applying profile changes.';
      removeProfileButton.title = matchesSelected ? '' :
        'Select an application from the list first.';
    }

    function renderProfiles() {
      gameList.textContent = '';
      if (!profiles.length) {
        gameList.appendChild(makeNativeItem('No applications found',
          'Windows Graphics applications will appear here.'));
        return;
      }
      profiles.forEach(function (profile) {
        var profilePath = String(profile.exePath || '');
        var preferenceText = profile.windowsGraphicsEntry
          ? (profile.lastAppliedAdapterId ? 'Windows GPU preference set' : 'System default')
          : profile.lastAppliedAdapterId
            ? 'Windows GPU preference set'
            : 'System default';
        var row = makeNativeItem(
          profile.name || profilePath,
          profilePath + ' Â· ' + preferenceText,
          Boolean(selectedProfile && String(selectedProfile.exePath || '').toLowerCase() ===
            profilePath.toLowerCase()),
          profile.iconDataUri
        );
        function selectProfile() {
          selectedProfile = profile;
          nameInput.value = profile.name || '';
          pathInput.value = profilePath;
          updateAngularModel(function (model) {
            model.adapterId = profile.selectedAdapterId || '';
          });
          renderProfiles();
          updateProfileButtons();
        }
        row.addEventListener('click', selectProfile);
        row.addEventListener('keydown', function (event) {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            selectProfile();
          }
        });
        gameList.appendChild(row);
      });
    }

    function renderManager(state) {
      profiles = Array.isArray(state.profiles) ? state.profiles : [];
      profiles.forEach(function (profile) {
        profile.windowsGraphicsEntry = false;
        profile.windowsPreference = 'System default';
        profile.lastAppliedAdapterId = '';
      });
      var windowsApps = Array.isArray(state.windowsApps) ? state.windowsApps.filter(function (app) {
        return app && typeof app.exePath === 'string' &&
          (app.exePath.indexOf('!') !== -1 || /\.exe$/i.test(app.exePath));
      }) : [];
      windowsApps.sort(function (left, right) {
        return String(left.name || left.exePath || '').localeCompare(
          String(right.name || right.exePath || ''));
      });
      windowsApps.forEach(function (app) {
        var existing = profiles.filter(function (profile) {
          return String(profile.exePath || '').toLowerCase() === String(app.exePath || '').toLowerCase();
        })[0];
        if (existing) {
          existing.windowsGraphicsEntry = true;
          existing.windowsPreference = app.windowsPreference;
          existing.iconDataUri = existing.iconDataUri || app.iconDataUri || '';
          if (app.selectedAdapterId) existing.selectedAdapterId = app.selectedAdapterId;
          if (app.lastAppliedAdapterId) existing.lastAppliedAdapterId = app.lastAppliedAdapterId;
          return;
        }
        profiles.push({
          name: app.name || app.exePath,
          exePath: app.exePath,
          selectedAdapterId: app.selectedAdapterId || '',
          lastAppliedAdapterId: app.lastAppliedAdapterId || '',
          windowsPreference: app.windowsPreference || '',
          iconDataUri: app.iconDataUri || '',
          windowsGraphicsEntry: true,
          windowsGraphicsOnly: true,
          statusMessage: app.registryValueKind === 'String'
            ? 'Imported from Windows Graphics settings.'
            : 'Unsupported registry value type: ' + app.registryValueKind
        });
      });
      games = Array.isArray(state.games) ? state.games : [];
      var pickerState = pickerReturnState;
      pickerReturnState = null;
      if (selectedProfile) {
        selectedProfile = profiles.filter(function (profile) {
          return profile.exePath === selectedProfile.exePath;
        })[0] || null;
      }
      if (pickerState) {
        selectedProfile = profiles.filter(function (profile) {
          return String(profile.exePath || '').toLowerCase() === pickerState.exePath.toLowerCase();
        })[0] || null;
        pathInput.value = pickerState.exePath;
        nameInput.value = selectedProfile
          ? selectedProfile.name || ''
          : pickerState.name || pickerState.exePath.split(/[\\/]/).pop().replace(/\.exe$/i, '');
      }
      if (!pickerState && !selectedProfile) {
        nameInput.value = '';
        pathInput.value = '';
      } else if (!pickerState) {
        nameInput.value = selectedProfile.name || '';
        pathInput.value = selectedProfile.exePath || '';
      }
      var oldGpu = selectedAdapterId();
      updateAngularModel(function (model) {
        model.adapters = (state.adapters || []).filter(function (adapter) {
          return Boolean(adapter.adapterId);
        }).map(function (adapter) {
          return Object.assign({}, adapter, { name: displayAdapterName(adapter.name) });
        });
        model.adapterId = selectedProfile
          ? selectedProfile.selectedAdapterId || ''
          : pickerState ? pickerState.adapterId : oldGpu;
      });
      renderProfiles();
      updateProfileButtons();
      if (pickerReturnStatus) {
        setStatus(pickerReturnStatus, pickerReturnStatus !== '');
        pickerReturnStatus = '';
      }
    }

    function load() {
      addButton.disabled = true;
      addButton.title = 'Select a running game first.';
      refreshButton.disabled = true;
      refreshButton.setAttribute('aria-busy', 'true');
      selectedGame = null;
      runningList.textContent = '';
      setStatus('Loading GPU managerâ€¦');
      return Promise.all([
        requestJson(managerUrl),
        requestJson(runningGamesUrl)
      ]).then(function (results) {
        renderManager(results[0]);
        games = Array.isArray(results[1].games) ? results[1].games : [];
        runningList.textContent = '';
        if (!games.length) {
          runningList.appendChild(makeNativeItem('No new running games found', 'Start a game, then refresh.'));
        }
        games.forEach(function (game, index) {
          var row = makeNativeItem(game.name || game.exePath, game.exePath, false, game.iconDataUri);
          row.addEventListener('click', function () {
            Array.prototype.forEach.call(runningList.querySelectorAll('.nv-gpu-running-game'), function (child) {
              child.classList.remove('hover-list-item-focus');
              delete child.dataset.selected;
            });
            selectedGame = games[index];
            row.dataset.selected = 'true';
            row.classList.add('hover-list-item-focus');
            addButton.disabled = false;
            addButton.title = '';
          });
          row.classList.add('nv-gpu-running-game');
          row.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              row.click();
            }
          });
          runningList.appendChild(row);
        });
        if (!results[0].telemetryError) setStatus('');
      }).catch(function (error) {
        setStatus(error.message, true);
      }).then(function () {
        refreshButton.disabled = false;
        refreshButton.removeAttribute('aria-busy');
      });
    }

    addButton.addEventListener('click', function () {
      if (!selectedGame) return;
      addButton.disabled = true;
      addButton.title = 'Adding gameâ€¦';
      setStatus('Adding gameâ€¦');
      postJson(addGameUrl, { exePath: selectedGame.exePath }).then(function () {
        selectedGame = null;
        return load();
      }).catch(function (error) {
        setStatus(error.message, true);
        addButton.disabled = !selectedGame;
        addButton.title = selectedGame ? '' : 'Select a running game first.';
      });
    });
    nameInput.addEventListener('input', updateProfileButtons);
    pathInput.addEventListener('input', updateProfileButtons);
    applyButton.addEventListener('click', function () {
      var exePath = pathInput.value.trim();
      if (!exePath) return;
      var name = nameInput.value.trim();
      if (!name) name = exePath.split(/[\\/]/).pop().replace(/\.exe$/i, '');
      var adapterId = selectedAdapterId();
      var profileSaved = false;
      profileApplying = true;
      updateProfileButtons();
      applyButton.setAttribute('aria-busy', 'true');
      var isWindowsGraphicsOnly = selectedProfile && selectedProfile.windowsGraphicsOnly &&
        String(selectedProfile.exePath || '').toLowerCase() === exePath.toLowerCase();
      setStatus(isWindowsGraphicsOnly
        ? 'Saving game name and updating the Windows GPU preferenceâ€¦'
        : 'Saving profile and updating the Windows GPU preferenceâ€¦');
      var saveRequest = postJson(saveUrl, {
        exePath: exePath,
        name: name,
        selectedAdapterId: adapterId
      });
      saveRequest.then(function () {
        profileSaved = true;
        selectedProfile = { exePath: exePath };
        if (adapterId) {
          return postJson(applyUrl, { exePath: exePath, selectedAdapterId: adapterId }).then(function (result) {
            return {
              message: result.message,
              error: !result.preferenceEventPublished,
              suffix: result.preferenceEventPublished ? '' : ' Preference event could not be delivered.'
            };
          });
        }
        return postJson(removePreferenceUrl, { exePath: exePath }).then(function (result) {
          return {
            message: result.message || 'Windows GPU preference reset to system default.',
            error: result.preferenceEventPublished === false,
            suffix: result.preferenceEventPublished === false
              ? ' The preference was reset, but its notification could not be delivered.'
              : ''
          };
        });
      }).then(function (result) {
        return load().then(function () {
          setStatus((isWindowsGraphicsOnly ? 'Game name saved. ' : 'Profile saved. ') +
            result.message + result.suffix, result.error);
        });
      }).catch(function (error) {
        var message = profileSaved
          ? 'Profile saved, but the Windows GPU preference could not be updated: ' + error.message
          : error.message;
        return load().then(function () { setStatus(message, true); });
      }).then(function () {
        profileApplying = false;
        applyButton.removeAttribute('aria-busy');
        updateProfileButtons();
      });
    });
    removeProfileButton.addEventListener('click', function () {
      if (!selectedProfile) return;
      var windowsGraphicsOnly = selectedProfile.windowsGraphicsOnly;
      setStatus(windowsGraphicsOnly
        ? 'Removing Windows Graphics entryâ€¦'
        : 'Removing game profile and Windows GPU preferenceâ€¦');
      var removeRequest = windowsGraphicsOnly
        ? postJson(removePreferenceUrl, { exePath: selectedProfile.exePath })
        : postJson(removeUrl, { exePath: selectedProfile.exePath });
      removeRequest
        .then(function (result) {
          selectedProfile = null;
          setStatus(windowsGraphicsOnly
            ? (result.message || 'Windows GPU preference reset to system default.') +
              (result.preferenceEventPublished === false
                ? ' Its notification could not be delivered.' : '')
            : (result.message || 'Game profile removed.') +
              (result.preferenceEventPublished === false
                ? ' Its notification could not be delivered.' : ''),
          windowsGraphicsOnly && result.preferenceEventPublished === false);
          return load();
        })
        .catch(function (error) { setStatus(error.message, true); });
    });
    refreshButton.addEventListener('click', load);
    fullUiButton.addEventListener('click', function () {
      fullUiButton.disabled = true;
      fullUiButton.setAttribute('aria-busy', 'true');
      closeOverlay().then(waitForOverlayClosed).then(function () {
        return requestJson(graphicsOpenUrl, { method: 'POST' });
      }).catch(function (error) {
        return reopenGameProfiles().then(function () {
          setStatus('Could not open NvGraphics: ' + error.message, true);
        }, function (reopenError) {
          setStatus('Could not open NvGraphics: ' + error.message +
            ' Game Profiles could not be restored: ' + reopenError.message, true);
        });
      }).then(function () {
        fullUiButton.disabled = false;
        fullUiButton.removeAttribute('aria-busy');
      });
    });
    function browseForExecutable() {
      if (pickerInProgress) return;
      pickerInProgress = true;
      browseButton.disabled = true;
      browseButton.setAttribute('aria-disabled', 'true');
      pickerReturnState = {
        exePath: pathInput.value.trim(),
        name: nameInput.value.trim(),
        adapterId: selectedAdapterId()
      };
      pickerReturnStatus = '';
      var pickerModeActive = false;
      setStatus('Closing overlay before opening executable pickerâ€¦');
      closeOverlay().then(function () {
        return waitForOverlayClosed();
      }).then(function () {
        pickerModeActive = true;
        return setPickerMode(true);
      }).then(function () {
        return new Promise(function (resolve) { window.setTimeout(resolve, 100); });
      }).then(function () {
        return requestJson(executablePickerUrl, null, 120000);
      }).then(function (result) {
        if (!result.cancelled) pickerReturnState.exePath = result.exePath;
      }).catch(function (error) {
        pickerReturnStatus = error.message;
      }).then(function () {
        if (!pickerModeActive) return;
        return setPickerMode(false).catch(function (error) {
          pickerReturnStatus = (pickerReturnStatus ? pickerReturnStatus + ' ' : '') +
            'Could not restore OSC z-order: ' + error.message;
        });
      }).then(function () {
        return reopenGameProfiles();
      }).catch(function (error) {
        pickerReturnStatus = 'Could not reopen Game Profiles: ' + error.message;
        setStatus(pickerReturnStatus, true);
      }).then(function () {
        pickerInProgress = false;
        browseButton.disabled = false;
        browseButton.removeAttribute('aria-disabled');
      });
    }
    browseButton.addEventListener('click', browseForExecutable);
    browseButton.addEventListener('keydown', function (event) {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        browseForExecutable();
      }
    });
    backButton.addEventListener('click', function () {
      backButton.disabled = true;
      closePage().catch(function (error) {
        backButton.disabled = false;
        setStatus('Could not return to the previous page: ' + error.message, true);
      });
    });
    page.addEventListener('keydown', function (event) {
      if (event.key === 'Escape') backButton.click();
    });
    return {
      page: page,
      compile: compilePage,
      load: load,
      dispose: function () {
        if (angularScope && !angularScope.$$destroyed) angularScope.$destroy();
        angularScope = null;
      }
    };
  }

  function getOverlayControls() {
    var injector = window.angular.element(document.documentElement).injector();
    if (!injector) throw new Error('NVIDIA OSC Angular injector is unavailable.');
    var displayService = injector.get('oscDisplayService');
    var rootScope = injector.get('$rootScope');
    var state = injector.get('$state');
    if (!displayService || typeof displayService.closeOSC !== 'function' ||
        typeof displayService.openOSC !== 'function') {
      throw new Error('NVIDIA OSC display controls are unavailable.');
    }
    return { display: displayService, rootScope: rootScope, state: state };
  }

  function inAngular(rootScope, callback) {
    return new Promise(function (resolve, reject) {
      rootScope.$evalAsync(function () {
        try { resolve(callback()); }
        catch (error) { reject(error); }
      });
    });
  }

  function closeOverlay() {
    var controls;
    try { controls = getOverlayControls(); }
    catch (error) { return Promise.reject(error); }
    return inAngular(controls.rootScope, function () {
      controls.display.closeOSC();
    });
  }

  function waitForOverlayClosed() {
    return waitForOverlayState(false);
  }

  function waitForOverlayOpen() {
    return waitForOverlayState(true);
  }

  function waitForOverlayState(expectedOpen) {
    var startedAt = Date.now();
    var timeoutMs = 8000;
    function check() {
      return new Promise(function (resolve, reject) {
        if (typeof window.cefQuery !== 'function') {
          reject(new Error('NVIDIA OSC native status bridge is unavailable.'));
          return;
        }
        var settled = false;
        var timer = window.setTimeout(function () {
          if (settled) return;
          settled = true;
          reject(new Error('Timed out reading NVIDIA OSC visibility state.'));
        }, 1500);
        window.cefQuery({
          request: JSON.stringify({ command: 'QUERY_OSC_GET_OPEN_STATE' }),
          onSuccess: function (response) {
            if (settled) return;
            settled = true;
            window.clearTimeout(timer);
            try {
              var open = JSON.parse(response);
              if (typeof open !== 'boolean') throw new Error('Expected a boolean.');
              resolve(open === expectedOpen);
            } catch (error) {
              reject(new Error('NVIDIA OSC returned an invalid visibility state.'));
            }
          },
          onFailure: function () {
            if (settled) return;
            settled = true;
            window.clearTimeout(timer);
            reject(new Error('Could not read NVIDIA OSC visibility state.'));
          }
        });
      }).then(function (matchesExpectedState) {
        if (matchesExpectedState) return;
        if (Date.now() - startedAt >= timeoutMs) {
          throw new Error('Timed out waiting for NVIDIA OSC to ' +
            (expectedOpen ? 'open.' : 'close.'));
        }
        return new Promise(function (resolve) { window.setTimeout(resolve, 100); }).then(check);
      });
    }
    return check();
  }

  function setPickerMode(active) {
    return new Promise(function (resolve, reject) {
      if (typeof window.cefQuery !== 'function') {
        reject(new Error('NVIDIA OSC native status bridge is unavailable.'));
        return;
      }
      var settled = false;
      var timer = window.setTimeout(function () {
        if (settled) return;
        settled = true;
        reject(new Error('Timed out changing OSC z-order for the executable picker.'));
      }, 2500);
      window.cefQuery({
        request: JSON.stringify({ command: 'QUERY_OSC_SET_PICKER_MODE', active: active }),
        onSuccess: function (response) {
          if (settled) return;
          settled = true;
          window.clearTimeout(timer);
          try {
            var result = JSON.parse(response);
            if (result.error) reject(new Error(result.error));
            else resolve();
          } catch (error) {
            reject(new Error('NVIDIA OSC returned an invalid picker z-order response.'));
          }
        },
        onFailure: function () {
          if (settled) return;
          settled = true;
          window.clearTimeout(timer);
          reject(new Error('Could not change OSC z-order for the executable picker.'));
        }
      });
    });
  }

  function reopenGameProfiles() {
    var controls;
    try { controls = getOverlayControls(); }
    catch (error) { return Promise.reject(error); }
    return inAngular(controls.rootScope, function () {
      controls.display.openOSC();
    }).then(function () {
      return waitForOverlayOpen();
    }).then(function () {
      return controls.state.go('main.gpu-profiles');
    }).then(function () {
      if (controls.state.current.name !== 'main.gpu-profiles') {
        throw new Error('Game Profiles could not be restored after opening NVIDIA OSC.');
      }
    });
  }

  function disposeActiveRoute() {
    if (!activeRoute) return;
    activeRoute.view.dispose();
    activeRoute = null;
  }

  function closePage() {
    var injector = window.angular.element(document.documentElement).injector();
    if (!injector) throw new Error('NVIDIA OSC Angular injector is unavailable.');
    return injector.get('$state').go(gameProfilesReturnState);
  }

  function syncRoutePage() {
    var routeSlot = /#\/base\/preferences\/game-profiles(?:[?#]|$)/.test(window.location.hash) ?
      document.querySelector('.nv-gpu-profiles-route-slot') : null;
    if (!routeSlot) {
      disposeActiveRoute();
      return;
    }
    if (activeRoute && activeRoute.slot === routeSlot) return;
    disposeActiveRoute();
    Array.prototype.forEach.call(routeSlot.querySelectorAll('#' + pageId), function (previousPage) {
      if (previousPage.__nvGpuProfilesDispose) previousPage.__nvGpuProfilesDispose();
      if (previousPage.parentNode) previousPage.parentNode.removeChild(previousPage);
    });
    var view = makePage();
    activeRoute = { slot: routeSlot, view: view };
    view.page.__nvGpuProfilesDispose = view.dispose;
    routeSlot.appendChild(view.page);
    view.compile();
    view.load();
  }

  observer = new MutationObserver(function () {
    registerPerformanceSupportListener();
    updateMainMenuPerformance();
    showEntry();
    syncRoutePage();
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('hashchange', function () {
    updateMainMenuPerformance();
    showEntry();
    syncRoutePage();
  });
  setInterval(function () {
    registerPerformanceSupportListener();
    updateMainMenuPerformance();
    showEntry();
    syncRoutePage();
  }, 500);
  registerPerformanceSupportListener();
  updateMainMenuPerformance();
  showEntry();
  syncRoutePage();
})();
