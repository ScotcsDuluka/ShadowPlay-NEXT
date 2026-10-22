(function(angular){
  'use strict';
  var storageKey='DulukaMainMenuLayoutV2';
  var items=[
    {key:'Screenshot',name:'screenshot-tile',label:'Screenshot',icon:'icon-screenshot'},
    {key:'Ansel',name:'ansel-tile',label:'Photo mode',icon:'icon-camera'},
    {key:'Mods',name:'mods-tile',label:'Game filter',icon:'icon-freestyle'},
    {key:'InstantReplay',name:'instant-replay-tile',label:'Instant Replay',icon:'icon-replay'},
    {key:'ManualRecord',name:'manual-record-tile',label:'Record',icon:'icon-record'},
    {key:'Stream',name:'stream-tile',label:'Stream',icon:'icon-stream'},
    {key:'Broadcast',name:'broadcast-tile',label:'Broadcast LIVE',icon:'icon-broadcast'}
  ];
  var defaults={version:2,left:['Screenshot','Ansel','Mods'],center:['InstantReplay','ManualRecord','Stream','Broadcast']};
  var listeners=[],current,error='',sideRows;
  var sideStorageKey='DulukaMainMenuSideRowsV1';
  // Additive Block Type state: original V2 layout and Side rows remain untouched.
  var typeStorageKey='DulukaMainMenuBlockTypesV1',centerTypes={};
  var nativeSideKeys=['Performance','Mic','Camera','Gallery','Settings'];
  var sideItems=[{key:'Performance',label:'Performance',icon:'icon-performance'},{key:'Mic',label:'Microphone',icon:'icon-mic_off'},{key:'Camera',label:'Camera',icon:'icon-camera'},{key:'Gallery',label:'Gallery',icon:'icon-gallery'},{key:'Settings',label:'Settings',icon:'icon-settings'}];
  var nativeLabelKeys={Screenshot:'l10n.screenshot',Ansel:'l10n.openAnsel',Mods:'l10n.mods',InstantReplay:'l10n.instantReplay',ManualRecord:'l10n.manualRecord',Stream:'l10n.stream',Broadcast:'l10n.broadcastLive',Performance:'l10n.perfmonoc.performance',Mic:'l10n.microphone',Camera:'l10n.camera',Gallery:'l10n.gallery',Settings:'l10n.settings'};
  function copy(value){return JSON.parse(JSON.stringify(value));}
  function known(key){return items.some(function(item){return item.key===key;});}
  function normalizeBlockTypes(raw){
    var result={};if(!raw||typeof raw!=='object')return result;
    items.forEach(function(item){if(raw[item.key]==='side')result[item.key]='side';});
    return result;
  }
  function loadBlockTypes(){try{return normalizeBlockTypes(JSON.parse(window.localStorage.getItem(typeStorageKey)||'null'));}catch(ignore){return {};}}
  centerTypes=loadBlockTypes();
  function centerGeometry(layout){
    var result=[],column=0,row=0;
    (layout.center||[]).forEach(function(key,index){
      var side=centerTypes[key]==='side';
      if(!side&&row>0){column++;row=0;}
      result.push({key:key,group:'center',index:index,x:column*240,y:side?row*80:0,width:240,height:side?80:240,variant:side?'row':'big'});
      if(side){row++;if(row===3){column++;row=0;}}
      else column++;
    });
    return {columns:column+(row?1:0),cells:result};
  }
  function normalize(raw){
    if(!raw||!Array.isArray(raw.left)||!Array.isArray(raw.center))return copy(defaults);
    var result={version:2,left:[],center:[]},seen={};
    ['left','center'].forEach(function(group){raw[group].forEach(function(key){if(known(key)&&!seen[key]&&result[group].length<(group==='left'?3:4)){result[group].push(key);seen[key]=true;}});});return result;
  }
  function load(){
    try{var raw=JSON.parse(window.localStorage.getItem(storageKey)||'null');if(raw)return normalize(raw);}catch(ignore){}
    try{var legacy=JSON.parse(window.localStorage.getItem('DulukaMainMenuLayout')||'null');if(legacy)return normalize(legacy);}catch(ignore){}
    var fallback=copy(defaults);try{var visibility=JSON.parse(window.localStorage.getItem('DulukaMainMenuVisibility')||'{}');['left','center'].forEach(function(group){fallback[group]=fallback[group].filter(function(key){return visibility[key]!==false;});});}catch(ignore){}return fallback;
  }
  function publish(){
    window.__dulukaMainMenuLayout={version:2,left:current.left.slice(),center:current.center.slice(),right:['Performance','Mic','Camera'],gallery:'Gallery'};
    var rightKeys=sideRows?flattenSides(sideRows.right):nativeSideKeys;
    var visible={Performance:rightKeys.indexOf('Performance')>=0,Mic:rightKeys.indexOf('Mic')>=0,Camera:rightKeys.indexOf('Camera')>=0,Gallery:rightKeys.indexOf('Gallery')>=0,Preferences:true};
    items.forEach(function(item){visible[item.key]=current.left.indexOf(item.key)>=0||current.center.indexOf(item.key)>=0||rightKeys.indexOf(item.key)>=0;});
    window.__dulukaMainMenuVisibility=visible;
  }
  function cells(layout){
    var result=[],sides=normalizeSides(sideRows,layout);
    sides.left.forEach(function(row,r){row.forEach(function(key,i){
      result.push({key:key,group:'left',index:r,slotIndex:i,x:-280+i*240/row.length,y:r*80,width:240/row.length,height:80,variant:row.length>1?'small':'row'});
    });});
    centerGeometry(layout).cells.forEach(function(cell){result.push(cell);});
    return result;
  }
  function set(layout,requestedSides){
    var clean=normalize(layout);
    if(!layout||!Array.isArray(layout.left)||!Array.isArray(layout.center)||JSON.stringify(layout.left)!==JSON.stringify(clean.left)||JSON.stringify(layout.center)!==JSON.stringify(clean.center)||
      (layout.right!==undefined&&JSON.stringify(layout.right)!=='["Performance","Mic","Camera"]')||(layout.gallery!==undefined&&layout.gallery!=='Gallery')){error='Choose up to 3 controls on the left and 4 in the center. Each control can be used once.';return false;}
    var nextSides=normalizeSides(requestedSides||sideRows,clean);
    try{window.localStorage.setItem(storageKey,JSON.stringify(clean));window.localStorage.setItem(sideStorageKey,JSON.stringify(nextSides));}catch(ignore){error='Could not save changes. Your previous layout is still active.';return false;}
    current=clean;sideRows=nextSides;error='';publish();listeners.slice().forEach(function(fn){fn(copy(current));});return true;
  }
  function move(key,group,index){
    if(!known(key)||(group!=='left'&&group!=='center'))return false;
    var next=copy(current),from=null,fromIndex=-1;
    ['left','center'].forEach(function(g){var i=next[g].indexOf(key);if(i>=0){from=g;fromIndex=i;}});
    index=Math.max(0,Math.min(Number(index)||0,next[group].length));
    if(from===group){next[group].splice(fromIndex,1);next[group].splice(Math.min(index,next[group].length),0,key);}
    else if(next[group].length>=(group==='left'?3:4)){
      if(!from){error='This panel is full. Drag onto a control to swap it, or remove one first.';return false;}
      index=Math.min(index,next[group].length-1);next[from][fromIndex]=next[group][index];next[group][index]=key;
    }else{if(from)next[from].splice(fromIndex,1);next[group].splice(index,0,key);}
    return set(next);
  }

  function flattenSides(rows){return [].concat.apply([],rows||[]);}
  function defaultSides(layout){
    return {left:layout.left.map(function(key){return [key];}),right:[['Performance','Mic','Camera'],['Gallery'],['Settings']]};
  }
  function normalizeSides(raw,layout){
    var fallback=defaultSides(layout),used={},result={left:[],right:[]};
    var leftAllowed=layout.left;
    var rightAllowed=nativeSideKeys.concat(items.map(function(x){return x.key;}));
    ['left','right'].forEach(function(group){
      var rows=raw&&Array.isArray(raw[group])?raw[group]:fallback[group];
      rows.forEach(function(row){
        if(!Array.isArray(row))return;
        var clean=[];
        row.forEach(function(key){
          var allowed=group==='left'?leftAllowed:rightAllowed;
          if(allowed.indexOf(key)<0||used[key]||clean.length>=3)return;
          if(group==='right'&&(layout.left.indexOf(key)>=0||layout.center.indexOf(key)>=0))return;
          clean.push(key);used[key]=true;
        });
        if(clean.length)result[group].push(clean);
      });
    });
    layout.left.forEach(function(key){
      if(!used[key]){result.left.push([key]);used[key]=true;}
    });
    if(!used.Settings)result.right.push(['Settings']);
    return result;
  }
  function loadSides(layout){
    var raw=null;
    try{raw=JSON.parse(window.localStorage.getItem(sideStorageKey)||'null');}catch(ignore){}
    return normalizeSides(raw||defaultSides(layout),layout);
  }
  function sideMove(key,group,rowIndex,join,slotIndex){
    if((group!=='left'&&group!=='right')||!known(key)&&nativeSideKeys.indexOf(key)<0)return false;
    if(nativeSideKeys.indexOf(key)>=0&&group!=='right'){error='Native right controls stay in the right panel.';return false;}
    var targetKey=join&&sideRows[group]&&sideRows[group][rowIndex]&&sideRows[group][rowIndex][slotIndex];
    if(targetKey===key)return true;
    var next=copy(current),sides=copy(sideRows),rows=sides[group];
    if(group==='left'&&next.left.indexOf(key)<0&&next.left.length>=3){error='Left panel is full (3 controls).';return false;}
    ['left','right'].forEach(function(g){sides[g]=sides[g].map(function(row){return row.filter(function(x){return x!==key;});}).filter(function(row){return row.length;});});
    ['left','center'].forEach(function(g){next[g]=next[g].filter(function(x){return x!==key;});});
    rows=sides[group];
    rowIndex=Math.max(0,Math.min(Number(rowIndex)||0,rows.length));
    if(join){
      if(targetKey){rows.some(function(row,i){var k=row.indexOf(targetKey);if(k<0)return false;rowIndex=i;slotIndex=k;return true;});}
      if(!rows[rowIndex])join=false;
    }
    if(join){
      if(rows[rowIndex].length>=3){error='A Side Slot supports up to 3 icons.';return false;}
      if(targetKey&&typeof slotIndex==='number')rows[rowIndex].splice(slotIndex,0,key);
      else rows[rowIndex].push(key);
    }else{
      if(rows.length>=(group==='left'?3:6)){error='This panel has too many Side rows.';return false;}
      rows.splice(rowIndex,0,[key]);
    }
    if(group==='left')next.left=flattenSides(rows);
    return set(next,sides);
  }
  function sideRemove(key){
    if(key==='Settings'){error='Settings must always be present.';return false;}
    if(!known(key)&&nativeSideKeys.indexOf(key)<0)return false;
    var next=copy(current),sides=copy(sideRows);
    ['left','right'].forEach(function(g){sides[g]=sides[g].map(function(row){return row.filter(function(x){return x!==key;});}).filter(function(row){return row.length;});});
    ['left','center'].forEach(function(g){next[g]=next[g].filter(function(x){return x!==key;});});
    return set(next,sides);
  }

  current=load();sideRows=loadSides(current);publish();
  var api=window.__dulukaMenuLayout={items:items,defaults:copy(defaults),get:function(){return copy(current);},normalize:normalize,cells:cells,set:set,move:move,
    remove:function(key){return sideRemove(key);},
    getBlockTypes:function(){var result={};items.forEach(function(item){result[item.key]=centerTypes[item.key]||'big';});return result;},centerColumns:function(layout){return centerGeometry(layout||current).columns;},
    setBlockType:function(key,type){
      if(!known(key)||(type!=='big'&&type!=='side')){error='Select Big or Side for a center control.';return false;}
      var next=copy(centerTypes);if(type==='big')delete next[key];else next[key]='side';
      try{window.localStorage.setItem(typeStorageKey,JSON.stringify(next));}catch(ignore){error='Could not save Block Type. Previous layout is unchanged.';return false;}
      centerTypes=next;error='';publish();listeners.slice().forEach(function(fn){fn(copy(current));});return true;
    },
    reset:function(){return set(defaults,defaultSides(defaults));},getSides:function(){return copy(sideRows);},sideMove:sideMove,sideRemove:sideRemove,sideItems:sideItems,subscribe:function(fn){listeners.push(fn);return function(){listeners=listeners.filter(function(f){return f!==fn;});};}};
  Object.defineProperty(api,'error',{get:function(){return error;}});
  var activeEditor=null,wantsEditing=false;
  window.__dulukaStartMainMenuEditor=function(){wantsEditing=true;if(activeEditor){activeEditor.start();return;}var injector=angular.element(document.documentElement).injector();if(injector)injector.get('$state').go('main.main-menu');};
  function sidePanelMarkup(group){
    return '<div class="nv-live-panel nv-live-side-panel nv-live-drop" data-side-group="'+group+'">'+
      '<h3>'+(group==='left'?'<span translate="l10n.oscLeftPanel">Left panel</span>':'<span translate="l10n.oscRightPanel">Right panel</span>')+' <span>{{flexMenu.sides.'+group+'.length}} <span translate="l10n.oscSideRows">Side rows</span></span></h3>'+
      '<div class="nv-side-rows" ng-repeat="row in flexMenu.sides.'+group+' track by $index" data-side-group="'+group+'" data-side-row="{{$index}}">'+
      '<div class="nv-side-row" ng-class="{\'nv-side-slot\':row.length>1}">'+
      '<div class="nv-live-chip" ng-repeat="key in row track by key" data-menu-key="{{key}}" data-side-group="'+group+'" data-side-index="{{$parent.$index}}" data-side-cell-index="{{$index}}">'+
      '<button type="button" title="{{flexMenu.label(key)}}" ng-click="flexMenu.select(key)" ng-class="{selected:flexMenu.selected===key}" aria-label="{{flexMenu.label(key)}}"><md-icon class="share-icon {{flexMenu.iconFor(key)}}"></md-icon><span ng-if="row.length===1"><strong>{{flexMenu.label(key)}}</strong><small>{{flexMenu.subtitle(key)}}</small></span></button>'+
      '<button type="button" class="nv-live-remove" ng-if="key!==\'Settings\'" ng-click="flexMenu.remove(key)" aria-label="Remove {{flexMenu.label(key)}}"><md-icon class="share-icon icon-app_close" aria-hidden="true"></md-icon></button>'+
      '</div></div>'+
      '<button type="button" class="nv-side-join" ng-if="row.length<3" ng-click="flexMenu.addToSideRow(\''+group+'\',$index)" translate="l10n.oscAddSideSlot">+ Side Slot</button>'+
      '</div>'+
      '<p ng-if="!flexMenu.sides.'+group+'.length" translate="l10n.oscDropControl">Drop a control here</p>'+
      '<button class="nv-live-add" ng-click="flexMenu.addSelected(\''+group+'\')" translate="l10n.oscAddSideBlock">+ Block Side</button></div>';
  }
  var toolbar=[
    '<section class="nv-live-editor" ng-if="flexMenu.editing" ng-style="{\'margin-top\':flexMenu.extraRows()+\'px\',\'max-height\':\'calc(100vh - \'+(412+flexMenu.extraRows())+\'px)\'}" aria-label="Customize Main-menu">',
    '<header><div><h2 translate="l10n.oscCustomizeTitle">Customize Main-menu</h2><p translate="l10n.oscCustomizeInfo">Drag controls across three panels. Add 2–3 icons into a Side Slot. Settings cannot be removed.</p></div><button class="osc-button osc-button-gray" ng-click="flexMenu.reset()" translate="l10n.oscRestoreLayout">Restore NVIDIA layout</button><button class="osc-button osc-button-green" ng-click="flexMenu.done()" translate="l10n.oscDone">Done</button></header>',
    '<div class="nv-live-workspace">',
    sidePanelMarkup('left'),
    '<div class="nv-live-panel nv-live-drop" data-menu-group="center"><h3><span translate="l10n.oscCenterPanel">Center panel</span> <span>{{flexMenu.layout.center.length}} / 4 <span translate="l10n.oscBig">Big</span></span></h3>',
    '<div class="nv-live-chips"><div class="nv-live-chip" ng-repeat="key in flexMenu.layout.center track by key" data-menu-key="{{key}}" data-menu-group="center" data-menu-index="{{$index}}"><button ng-click="flexMenu.select(key)" aria-label="Select {{flexMenu.label(key)}}" ng-class="{selected:flexMenu.selected===key}"><md-icon class="share-icon {{flexMenu.iconFor(key)}}"></md-icon>{{flexMenu.label(key)}}</button><select class="nv-center-block-type" aria-label="Block Type for {{flexMenu.label(key)}}" ng-model="flexMenu.blockTypes[key]" ng-change="flexMenu.changeBlockType(key,flexMenu.blockTypes[key])"><option value="big">Big</option><option value="side">Side</option></select><button class="nv-live-remove" ng-click="flexMenu.remove(key)" aria-label="Remove {{flexMenu.label(key)}}"><md-icon class="share-icon icon-app_close" aria-hidden="true"></md-icon></button></div><p ng-if="!flexMenu.layout.center.length" translate="l10n.oscDropControl">Drop a control here</p></div>',
    '<button class="nv-live-add" ng-click="flexMenu.addSelected(\'center\')" translate="l10n.oscPlaceBig">Place selected Big block here</button></div>',
    sidePanelMarkup('right'),
    '<div class="nv-live-library"><h3><span translate="l10n.oscControls">Controls</span> <span translate="l10n.oscScrollHorizontally">Scroll horizontally</span><button type="button" class="nv-live-library-toggle" data-controls-toggle aria-label="Toggle Controls list" aria-expanded="{{!flexMenu.libraryCollapsed}}"><md-icon class="share-icon icon-normal icon-chevron_down" ng-class="{collapsed:flexMenu.libraryCollapsed}" aria-hidden="true"></md-icon></button></h3><input class="nv-live-search" ng-show="!flexMenu.libraryCollapsed" type="search" ng-model="flexMenu.search" placeholder="Search controls..." translate-attr-placeholder="l10n.oscSearchControls" aria-label="Search controls"><div class="nv-live-palette" ng-show="!flexMenu.libraryCollapsed"><button ng-repeat="item in flexMenu.items | filter:{label:flexMenu.search} track by item.key" data-menu-key="{{item.key}}" ng-click="flexMenu.select(item.key)" ng-class="{selected:flexMenu.selected===item.key,assigned:flexMenu.assigned(item.key)}"><md-icon class="share-icon {{flexMenu.iconFor(item.key)}}"></md-icon><span>{{flexMenu.label(item.key)}}</span><span ng-if="item.key===\'Settings\'" class="nv-locked-label" translate="l10n.oscLocked">Locked</span></button></div><p translate="l10n.oscLibraryInfo">Drag onto a Side row to group 2–3 icon slots. Settings stays available.</p></div></div>',
    '<footer><label translate="l10n.oscDesktopDisplay">Desktop Mode display</label><md-select aria-label="Desktop Mode display" ng-model="flexMenu.desktopMonitor" ng-change="flexMenu.saveDesktopMonitor()" ng-disabled="flexMenu.desktopBusy"><md-option ng-value="\'\'" translate="l10n.oscPrimaryDisplay">Primary display (automatic)</md-option><md-option ng-repeat="display in flexMenu.desktopDisplays" ng-value="display.device">{{display.label}}</md-option><md-option ng-if="flexMenu.desktopDisconnected" ng-value="flexMenu.desktopMonitor" disabled>Disconnected display — using primary</md-option></md-select><p role="alert" ng-if="flexMenu.error">{{flexMenu.error}}</p></footer>',
    '</section>'
  ].join('');
  angular.module('main').config(['$stateProvider','$provide',function($stateProvider,$provide){
    $stateProvider.state('main.customize-main-menu',{url:'/preferences/customize-main-menu',onEnter:['$state',function($state){wantsEditing=true;$state.go('main.main-menu');}]});
    $provide.decorator('nvMainMenuDirective',['$delegate',function($delegate){
      $delegate.forEach(function(definition){if(typeof definition.template!=='string')return;
        var branch='<div layout=row class="gallery-tile-align three-right"><md-icon class="share-icon icon48 icon-normal icon-settings"></md-icon><p class="gallery" translate="l10n.settings">Settings</p></div>';
        var template=definition.template.replace(/<div ng-switch-when=(?:"preferences-tile"|'preferences-tile'|preferences-tile)>\s*<md-icon[\s\S]*?<\/md-icon>\s*<\/div>/,'<div ng-switch-when=preferences-tile>'+branch+'</div><div ng-switch-when=nv-settings-menu>'+branch+'</div>');
        var visual='<div ng-if="flexMenu.isMovable(tile)" class="nv-flex-face" ng-class="flexMenu.variant(tile)"><div class="nv-flex-title" translate={{tile.title}}></div><md-icon class="share-icon icon-normal nv-flex-icon {{flexMenu.icon(tile)}}"></md-icon><div class="nv-flex-detail" ng-class="tile.state[tile.currentState].statusBrush" translate={{flexMenu.detail(tile)}}></div></div>';
        template=template.replace(/<div ng-switch on="[^"]*tile.name[^"]*">/,function(match){return visual+match.replace('<div ','<div ng-if="!flexMenu.isMovable(tile)" ');});
        template=template.replace('ng-disabled=!tile.enabled','ng-disabled="!tile.enabled && !(flexMenu.editing && flexMenu.isMovable(tile))"');
        template=template.replace('class="{{tile.name}} {{tile.extraclass}}"','data-nv-control="{{flexMenu.key(tile)}}" class="{{tile.name}} {{tile.extraclass}}"');
        definition.template='<div nv-main-menu-live-editor>'+template+toolbar+'</div>';
      });return $delegate;
    }]);
  }]).directive('nvMainMenuLiveEditor',['$window','$document',function($window,$document){
    return {restrict:'A',controller:['$scope','$filter',function($scope,$filter){
      var self=this;self.editing=false;self.items=items.concat(sideItems);self.layout=api.get();self.sides=api.getSides();self.blockTypes=api.getBlockTypes();self.selected='';self.search='';self.libraryCollapsed=false;self.error='';
      self.extraRows=function(){return window.__dulukaUnifiedMainMenu&&window.__dulukaUnifiedMainMenu.enabled()?0:Math.max(0,self.sides.right.length-3)*80;};
      self.key=function(tile){var match=items.filter(function(item){return tile&&item.name===tile.name;})[0];return match?match.key:'';};
      self.isMovable=function(tile){return !!self.key(tile);};self.item=function(key){return self.items.filter(function(item){return item.key===key;})[0]||{};};self.label=function(key){var code=nativeLabelKeys[key],fallback=self.item(key).label||key;if(!code)return fallback;var translated=$filter('translate')(code);return typeof translated==='string'&&translated&&translated!==code?translated:fallback;};
      self.subtitle=function(key){var menu=$scope.mainMenu||{},tile=(menu.tiles||{})[key];if(key==='Screenshot')return menu.screenshotHotkey||'';if(key==='Ansel')return menu.anselHotkey||'';if(key==='Settings')return 'Preferences';if(key==='Gallery')return 'Saved captures';return tile?(tile.shortcut||(tile.state&&tile.state[tile.currentState]||{}).status||tile.currentState||''):'';};
      self.iconFor=function(key){var item=self.item(key),tile=($scope.mainMenu&&$scope.mainMenu.tiles||{})[key];return tile&&(tile.state&&tile.state[tile.currentState]||{}).icon||item.icon;};
      self.variant=function(tile){var key=self.key(tile),v4=window.__dulukaUnifiedMainMenu;if(v4&&v4.enabled()){var cell=v4.cell(key);return cell?cell.variant:'row';}var groups=self.sides;for(var g in groups){for(var i=0;i<groups[g].length;i++)if(groups[g][i].indexOf(key)>=0)return groups[g][i].length>1?'small':'row';}return self.blockTypes[key]==='side'?'row':'big';};
      self.icon=function(tile){return tile.icon||(tile.state&&tile.state[tile.currentState]||{}).icon||self.item(self.key(tile)).icon;};
      self.detail=function(tile){var key=self.key(tile),menu=$scope.mainMenu||{};return key==='Screenshot'?menu.screenshotHotkey:key==='Ansel'?menu.anselHotkey:tile.shortcut||(tile.state&&tile.state[tile.currentState]||{}).status||'';};
      function update(){if(!$scope.$$destroyed)$scope.$evalAsync(function(){self.layout=api.get();self.sides=api.getSides();self.blockTypes=api.getBlockTypes();self.error=api.error;});}
      self.changeBlockType=function(key,type){if(!api.setBlockType(key,type))self.error=api.error;update();};
      function editingMotion(enabled){var doc=$document&&$document[0],classes=doc&&doc.documentElement&&doc.documentElement.classList;if(classes)classes[enabled?'add':'remove']('nv-live-layout-editing');}
      self.start=function(){self.editing=true;editingMotion(true);wantsEditing=false;self.loadDesktopMonitors();update();if(window.__dulukaUnifiedMainMenu&&window.__dulukaUnifiedMainMenu.enabled())$window.setTimeout(function(){window.__dulukaUnifiedMainMenu.openEditor();},0);};
      self.done=function(){self.editing=false;editingMotion(false);var key=self.selected;self.selected='';self.error='';update();$window.setTimeout(function(){if($scope.$$destroyed)return;var tile=document.querySelector('md-menu[data-nv-control="'+key+'"]>button.menu-button')||document.querySelector('md-menu.nv-flex-tile>button.menu-button:not([disabled])');if(tile)tile.focus();},0);};
      self.select=function(key){self.selected=key;self.error='';};self.assigned=function(key){return self.layout.left.indexOf(key)>=0||self.layout.center.indexOf(key)>=0||flattenSides(self.sides.right).indexOf(key)>=0;};
      self.remove=function(key){api.remove(key);update();};self.reset=function(){api.reset();update();};
      self.addToSideRow=function(group,row){if(!self.selected){self.error='Select a control first.';return;}api.sideMove(self.selected,group,row,true);update();};
      self.addSelected=function(group){if(!self.selected){self.error='Select a control first.';return;}if(group==='center'){if(!known(self.selected)){self.error='Select a Big control for Center.';return;}api.move(self.selected,group,api.get().center.length);}else{api.sideMove(self.selected,group,api.getSides()[group].length,false);}update();};
      self.move=function(key,group,index){api.move(key,group,index);update();};self.sideMove=function(key,group,index,join,slot){api.sideMove(key,group,index,join,slot);update();};
      self.desktopDisplays=[];self.desktopMonitor='';self.desktopBusy=false;self.desktopDisconnected=false;var savedDesktop='';
      function query(command,device,callback){if(typeof $window.cefQuery!=='function'){callback(null);return;}$window.cefQuery({request:JSON.stringify({command:command,device:device}),onSuccess:function(raw){var result;try{result=JSON.parse(raw);}catch(ignore){}if(!$scope.$$destroyed)$scope.$evalAsync(function(){callback(result);});},onFailure:function(){if(!$scope.$$destroyed)$scope.$evalAsync(function(){callback(null);});}});}
      self.loadDesktopMonitors=function(){self.desktopBusy=true;query('QUERY_OSC_DESKTOP_MONITORS','',function(data){self.desktopBusy=false;if(!data||!Array.isArray(data.displays)){self.error='Could not load displays.';return;}self.desktopDisplays=data.displays.map(function(d,i){var number=/DISPLAY(\d+)$/i.exec(d.device);d.label='Display '+(number?number[1]:i+1)+' — '+d.name+' ('+d.width+' × '+d.height+')'+(d.primary?' — Primary':'');return d;});self.desktopMonitor=data.selected||'';savedDesktop=self.desktopMonitor;self.desktopDisconnected=!!savedDesktop&&!data.displays.some(function(d){return d.device===savedDesktop;});});};
      self.saveDesktopMonitor=function(){self.desktopBusy=true;self.error='';var requested=self.desktopMonitor;query('QUERY_OSC_SET_DESKTOP_MONITOR',requested,function(data){self.desktopBusy=false;if(!data||data.saved!==true){self.desktopMonitor=savedDesktop;self.error='Could not change display. Check that it is connected.';}else{savedDesktop=requested;self.desktopDisconnected=false;}});};
      $scope.flexMenu=self;activeEditor=self;var unsubscribe=api.subscribe(update);
      $scope.$on('$destroy',function(){unsubscribe();if(activeEditor===self){editingMotion(false);activeEditor=null;}});if(wantsEditing)self.start();
    }],link:function(scope,element){
      var root=element[0],doc=$document[0],drag=null,ghost=null,target=null,clickSuppressed=false,suppressTimer=null;
      function editor(){return scope.flexMenu;}
      // V4 owns dragging and panel capacity when enabled. V2 listeners stay
      // installed for rollback, but must never edit V2 or show V2 limits.
      function usingUnified(){var u=$window.__dulukaUnifiedMainMenu;return !!(u&&u.enabled&&u.enabled());}
      // The Controls tray is one horizontal row. Support an ordinary mouse
      // wheel when hovering it, while allowing page scroll at either end.
      function scrollControls(event){
        if(!editor().editing||usingUnified())return;
        var tray=event.target.closest&&event.target.closest('.nv-live-palette');
        if(!tray||tray.scrollWidth<=tray.clientWidth||Math.abs(event.deltaY)<=Math.abs(event.deltaX))return;
        var before=tray.scrollLeft;
        tray.scrollLeft+=event.deltaY;
        if(tray.scrollLeft!==before)event.preventDefault();
      }
      function closest(node,selector){return node&&node.closest?node.closest(selector):null;}
      function identify(node){var e=closest(node,'[data-menu-key],md-menu[data-nv-control]')||closest(node,'md-menu[data-nv-side-key]');return e&&(e.getAttribute('data-menu-key')||e.getAttribute('data-nv-control')||e.getAttribute('data-nv-side-key'));}
      function captureClick(event){if(!editor().editing)return;if(usingUnified()){
        var key=identify(event.target),nativeTile=closest(event.target,'md-menu');
        if(nativeTile){event.preventDefault();event.stopImmediatePropagation();if(key&&!clickSuppressed&&$window.__dulukaUnifiedMainMenu.selectControl)$window.__dulukaUnifiedMainMenu.selectControl(key);}
        if(clickSuppressed){event.preventDefault();event.stopImmediatePropagation();}
        clickSuppressed=false;return;
      }if(closest(event.target,'.nv-live-library-toggle')){event.preventDefault();event.stopImmediatePropagation();scope.$evalAsync(function(){editor().libraryCollapsed=!editor().libraryCollapsed;});return;}var key=identify(event.target);if(closest(event.target,'md-menu')){event.preventDefault();event.stopImmediatePropagation();if(key&&!clickSuppressed)scope.$evalAsync(function(){editor().select(key);});}else if(clickSuppressed){event.preventDefault();event.stopImmediatePropagation();}clickSuppressed=false;}
      function down(event){if(!editor().editing||usingUnified()||event.button!==0||closest(event.target,'.nv-live-remove'))return;var key=identify(event.target);if(!known(key)&&nativeSideKeys.indexOf(key)<0)return;drag={key:key,x:event.clientX,y:event.clientY,active:false};event.preventDefault();}
      function clearTarget(){if(target&&target.node)target.node.classList.remove('nv-live-drop-target');target=null;}
      function move(event){if(!drag||usingUnified())return;if(!drag.active&&Math.abs(event.clientX-drag.x)+Math.abs(event.clientY-drag.y)<6)return;
        if(!drag.active){drag.active=true;ghost=doc.createElement('div');ghost.className='nv-live-drag-ghost';ghost.textContent=editor().label(drag.key);doc.body.appendChild(ghost);root.classList.add('nv-live-dragging');}
        ghost.style.left=(event.clientX+14)+'px';ghost.style.top=(event.clientY+14)+'px';clearTarget();
        var node=doc.elementFromPoint(event.clientX,event.clientY),sideRow=closest(node,'[data-side-row][data-side-group]'),chip=closest(node,'[data-menu-key][data-menu-group]'),panel=closest(node,'.nv-live-drop'),tile=closest(node,'md-menu[data-nv-control]')||closest(node,'md-menu[data-nv-side-key]');
        if(sideRow){var sideCell=closest(node,'[data-side-cell-index]');target={group:sideRow.getAttribute('data-side-group'),index:Number(sideRow.getAttribute('data-side-row')),slot:sideCell?Number(sideCell.getAttribute('data-side-cell-index')):undefined,side:true,join:true,node:sideRow};}
        else if(chip)target={group:chip.getAttribute('data-menu-group'),index:Number(chip.getAttribute('data-menu-index')),node:chip};
        else if(panel){var group=panel.getAttribute('data-side-group')||panel.getAttribute('data-menu-group');target={group:group,index:group==='center'?api.get().center.length:api.getSides()[group].length,side:group!=='center',join:false,node:panel};}
        else if(tile){var key=tile.getAttribute('data-nv-side-key')||tile.getAttribute('data-nv-control'),layout=api.get(),sides=api.getSides();['left','right'].some(function(g){for(var i=0;i<sides[g].length;i++)if(sides[g][i].indexOf(key)>=0){target={group:g,index:i,slot:sides[g][i].indexOf(key),side:true,join:true,node:tile};return true;}return false;});if(!target){var i=layout.center.indexOf(key);if(i>=0)target={group:'center',index:i,node:tile};}}
        if(target)target.node.classList.add('nv-live-drop-target');event.preventDefault();
      }
      function cancel(){clearTarget();if(ghost)ghost.remove();ghost=null;drag=null;root.classList.remove('nv-live-dragging');}
      function up(){if(!drag)return;if(usingUnified()){cancel();return;}if(drag.active){clickSuppressed=true;if(suppressTimer)$window.clearTimeout(suppressTimer);suppressTimer=$window.setTimeout(function(){clickSuppressed=false;suppressTimer=null;},0);if(target){var key=drag.key,g=target.group,i=target.index,side=target.side,join=target.join,slot=target.slot;scope.$evalAsync(function(){if(side)editor().sideMove(key,g,i,join,slot);else editor().move(key,g,i);});}}cancel();}
      function keydown(event){if(usingUnified()&&drag)cancel();if(event.key==='Escape'&&drag){cancel();event.preventDefault();event.stopImmediatePropagation();return;}if(editor().editing&&closest(event.target,'md-menu')&&(event.key==='Enter'||event.key===' '||event.keyCode===13||event.keyCode===32)){captureClick(event);return;}
        if(editor().editing||!/^Arrow/.test(event.key)||closest(event.target,'md-menu-content')||doc.querySelector('.md-open-menu-container.md-active'))return;
        var button=closest(event.target,'button.menu-button');if(!button)return;var r=button.getBoundingClientRect(),cx=r.left+r.width/2,cy=r.top+r.height/2,best=null,score=Infinity;
        Array.prototype.forEach.call(root.querySelectorAll('md-menu>button.menu-button:not([disabled])'),function(b){if(b===button)return;var q=b.getBoundingClientRect();if(!q.width||!q.height)return;var dx=q.left+q.width/2-cx,dy=q.top+q.height/2-cy;var primary=event.key==='ArrowLeft'?-dx:event.key==='ArrowRight'?dx:event.key==='ArrowUp'?-dy:dy,secondary=/Left|Right/.test(event.key)?Math.abs(dy):Math.abs(dx);if(primary>1&&primary+secondary*3<score){score=primary+secondary*3;best=b;}});
        if(best){event.preventDefault();event.stopImmediatePropagation();best.focus();}
      }
      root.addEventListener('wheel',scrollControls,{passive:false});root.addEventListener('click',captureClick,true);root.addEventListener('mousedown',down,true);root.addEventListener('keydown',keydown,true);doc.addEventListener('mousemove',move,true);doc.addEventListener('mouseup',up,true);$window.addEventListener('blur',cancel);
      scope.$on('$destroy',function(){cancel();if(suppressTimer)$window.clearTimeout(suppressTimer);root.removeEventListener('wheel',scrollControls,false);root.removeEventListener('click',captureClick,true);root.removeEventListener('mousedown',down,true);root.removeEventListener('keydown',keydown,true);doc.removeEventListener('mousemove',move,true);doc.removeEventListener('mouseup',up,true);$window.removeEventListener('blur',cancel);});
    }};
  }]);
})(window.angular);
