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
  var listeners=[],current,error='';
  function copy(value){return JSON.parse(JSON.stringify(value));}
  function known(key){return items.some(function(item){return item.key===key;});}
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
    var visible={Performance:true,Mic:true,Camera:true,Gallery:true,Preferences:true};
    items.forEach(function(item){visible[item.key]=current.left.indexOf(item.key)>=0||current.center.indexOf(item.key)>=0;});
    window.__dulukaMainMenuVisibility=visible;
  }
  function cells(layout){
    var result=[];['left','center'].forEach(function(group){var keys=layout[group],count=keys.length;
      keys.forEach(function(key,index){result.push({key:key,group:group,index:index,x:group==='left'?-280:index*960/count,y:group==='left'?index*240/count:0,width:group==='left'?240:960/count,height:group==='left'?240/count:240,variant:group==='center'||count===1?'big':'row'});});});return result;
  }
  function set(layout){
    var clean=normalize(layout);
    if(!layout||!Array.isArray(layout.left)||!Array.isArray(layout.center)||JSON.stringify(layout.left)!==JSON.stringify(clean.left)||JSON.stringify(layout.center)!==JSON.stringify(clean.center)||
      (layout.right!==undefined&&JSON.stringify(layout.right)!=='["Performance","Mic","Camera"]')||(layout.gallery!==undefined&&layout.gallery!=='Gallery')){error='Choose up to 3 controls on the left and 4 in the center. Each control can be used once.';return false;}
    try{window.localStorage.setItem(storageKey,JSON.stringify(clean));}catch(ignore){error='Could not save changes. Your previous layout is still active.';return false;}
    current=clean;error='';publish();listeners.slice().forEach(function(fn){fn(copy(current));});return true;
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
  current=load();publish();
  var api=window.__dulukaMenuLayout={items:items,defaults:copy(defaults),get:function(){return copy(current);},normalize:normalize,cells:cells,set:set,move:move,
    remove:function(key){if(!known(key))return false;var next=copy(current);['left','center'].forEach(function(group){next[group]=next[group].filter(function(k){return k!==key;});});return set(next);},
    reset:function(){return set(defaults);},subscribe:function(fn){listeners.push(fn);return function(){listeners=listeners.filter(function(f){return f!==fn;});};}};
  Object.defineProperty(api,'error',{get:function(){return error;}});
  var activeEditor=null,wantsEditing=false;
  window.__dulukaStartMainMenuEditor=function(){wantsEditing=true;if(activeEditor){activeEditor.start();return;}var injector=angular.element(document.documentElement).injector();if(injector)injector.get('$state').go('main.main-menu');};
  var toolbar=[
    '<section class="nv-live-editor" ng-if="flexMenu.editing" aria-label="Customize Main-menu">',
    '<header><div><h2>Customize Main-menu</h2><p>Drag controls between the two panels. Changes save automatically.</p></div><button class="osc-button osc-button-gray" ng-click="flexMenu.reset()">Restore NVIDIA layout</button><button class="osc-button osc-button-green" ng-click="flexMenu.done()">Done</button></header>',
    '<div class="nv-live-workspace">',
    '<div class="nv-live-panel nv-live-drop" ng-repeat="group in [\'left\',\'center\']" data-menu-group="{{group}}"><h3>{{group === \'left\' ? \'Left panel\' : \'Center panel\'}} <span>{{flexMenu.layout[group].length}} / {{group === \'left\' ? 3 : 4}}</span></h3>',
    '<div class="nv-live-chips"><div class="nv-live-chip" ng-repeat="key in flexMenu.layout[group] track by key" data-menu-key="{{key}}" data-menu-group="{{group}}" data-menu-index="{{$index}}"><button ng-click="flexMenu.select(key)" aria-label="Select {{flexMenu.label(key)}}" ng-class="{selected:flexMenu.selected===key}"><md-icon class="share-icon {{flexMenu.item(key).icon}}"></md-icon>{{flexMenu.label(key)}}</button><button class="nv-live-remove" ng-click="flexMenu.remove(key)" aria-label="Remove {{flexMenu.label(key)}}">×</button></div><p ng-if="!flexMenu.layout[group].length">Drop a control here</p></div>',
    '<button class="nv-live-add" ng-click="flexMenu.addSelected(group)">Place selected control here</button></div>',
    '<div class="nv-live-library"><h3>Controls <span>Right panel locked</span></h3><div class="nv-live-palette"><button ng-repeat="item in flexMenu.items" data-menu-key="{{item.key}}" ng-click="flexMenu.select(item.key)" ng-class="{selected:flexMenu.selected===item.key,assigned:flexMenu.assigned(item.key)}"><md-icon class="share-icon {{item.icon}}"></md-icon><span>{{item.label}}</span></button></div><p>Select a control, then choose a panel. You can also drag to reorder or swap.</p></div></div>',
    '<footer><label>Desktop Mode display</label><md-select aria-label="Desktop Mode display" ng-model="flexMenu.desktopMonitor" ng-change="flexMenu.saveDesktopMonitor()" ng-disabled="flexMenu.desktopBusy"><md-option ng-value="\'\'">Primary display (automatic)</md-option><md-option ng-repeat="display in flexMenu.desktopDisplays" ng-value="display.device">{{display.label}}</md-option><md-option ng-if="flexMenu.desktopDisconnected" ng-value="flexMenu.desktopMonitor" disabled>Disconnected display — using primary</md-option></md-select><p role="alert" ng-if="flexMenu.error">{{flexMenu.error}}</p></footer>',
    '</section>'
  ].join('');
  angular.module('main').config(['$stateProvider','$provide',function($stateProvider,$provide){
    $stateProvider.state('main.customize-main-menu',{url:'/preferences/customize-main-menu',onEnter:['$state',function($state){wantsEditing=true;$state.go('main.main-menu');}]});
    $provide.decorator('nvMainMenuDirective',['$delegate',function($delegate){
      $delegate.forEach(function(definition){if(typeof definition.template!=='string')return;
        var branch='<div layout=row class="gallery-tile-align three-right"><md-icon class="share-icon icon48 icon-normal icon-settings"></md-icon><p class=gallery>Settings</p></div>';
        var template=definition.template.replace(/<div ng-switch-when=(?:"preferences-tile"|'preferences-tile'|preferences-tile)>\s*<md-icon[\s\S]*?<\/md-icon>\s*<\/div>/,'<div ng-switch-when=preferences-tile>'+branch+'</div><div ng-switch-when=nv-settings-menu>'+branch+'</div>');
        var visual='<div ng-if="flexMenu.isMovable(tile)" class="nv-flex-face" ng-class="flexMenu.variant(tile)"><div class="nv-flex-title" translate={{tile.title}}></div><md-icon class="share-icon icon-normal nv-flex-icon {{flexMenu.icon(tile)}}"></md-icon><div class="nv-flex-detail" ng-class="tile.state[tile.currentState].statusBrush" translate={{flexMenu.detail(tile)}}></div></div>';
        template=template.replace(/<div ng-switch on="[^"]*tile.name[^"]*">/,function(match){return visual+match.replace('<div ','<div ng-if="!flexMenu.isMovable(tile)" ');});
        template=template.replace('ng-disabled=!tile.enabled','ng-disabled="!tile.enabled && !(flexMenu.editing && flexMenu.isMovable(tile))"');
        template=template.replace('class="{{tile.name}} {{tile.extraclass}}"','data-nv-control="{{flexMenu.key(tile)}}" class="{{tile.name}} {{tile.extraclass}}"');
        definition.template='<div nv-main-menu-live-editor>'+template+toolbar+'</div>';
      });return $delegate;
    }]);
  }]).directive('nvMainMenuLiveEditor',['$window','$document',function($window,$document){
    return {restrict:'A',controller:['$scope',function($scope){
      var self=this;self.editing=false;self.items=items;self.layout=api.get();self.selected='';self.error='';
      self.key=function(tile){var match=items.filter(function(item){return tile&&item.name===tile.name;})[0];return match?match.key:'';};
      self.isMovable=function(tile){return !!self.key(tile);};self.item=function(key){return items.filter(function(item){return item.key===key;})[0]||{};};self.label=function(key){return self.item(key).label;};
      self.variant=function(tile){return self.layout.left.indexOf(self.key(tile))>=0&&self.layout.left.length>1?'row':'big';};
      self.icon=function(tile){return tile.icon||(tile.state&&tile.state[tile.currentState]||{}).icon||self.item(self.key(tile)).icon;};
      self.detail=function(tile){var key=self.key(tile),menu=$scope.mainMenu||{};return key==='Screenshot'?menu.screenshotHotkey:key==='Ansel'?menu.anselHotkey:tile.shortcut||(tile.state&&tile.state[tile.currentState]||{}).status||'';};
      function update(){if(!$scope.$$destroyed)$scope.$evalAsync(function(){self.layout=api.get();self.error=api.error;});}
      self.start=function(){self.editing=true;wantsEditing=false;self.loadDesktopMonitors();update();};
      self.done=function(){self.editing=false;var key=self.selected;self.selected='';self.error='';update();$window.setTimeout(function(){if($scope.$$destroyed)return;var tile=document.querySelector('md-menu[data-nv-control="'+key+'"]>button.menu-button')||document.querySelector('md-menu.nv-flex-tile>button.menu-button:not([disabled])');if(tile)tile.focus();},0);};
      self.select=function(key){self.selected=key;self.error='';};self.assigned=function(key){return self.layout.left.indexOf(key)>=0||self.layout.center.indexOf(key)>=0;};
      self.remove=function(key){api.remove(key);update();};self.reset=function(){api.reset();update();};
      self.addSelected=function(group){if(!self.selected){self.error='Select a control first.';return;}api.move(self.selected,group,api.get()[group].length);update();};
      self.move=function(key,group,index){api.move(key,group,index);update();};
      self.desktopDisplays=[];self.desktopMonitor='';self.desktopBusy=false;self.desktopDisconnected=false;var savedDesktop='';
      function query(command,device,callback){if(typeof $window.cefQuery!=='function'){callback(null);return;}$window.cefQuery({request:JSON.stringify({command:command,device:device}),onSuccess:function(raw){var result;try{result=JSON.parse(raw);}catch(ignore){}if(!$scope.$$destroyed)$scope.$evalAsync(function(){callback(result);});},onFailure:function(){if(!$scope.$$destroyed)$scope.$evalAsync(function(){callback(null);});}});}
      self.loadDesktopMonitors=function(){self.desktopBusy=true;query('QUERY_OSC_DESKTOP_MONITORS','',function(data){self.desktopBusy=false;if(!data||!Array.isArray(data.displays)){self.error='Could not load displays.';return;}self.desktopDisplays=data.displays.map(function(d,i){var number=/DISPLAY(\d+)$/i.exec(d.device);d.label='Display '+(number?number[1]:i+1)+' — '+d.name+' ('+d.width+' × '+d.height+')'+(d.primary?' — Primary':'');return d;});self.desktopMonitor=data.selected||'';savedDesktop=self.desktopMonitor;self.desktopDisconnected=!!savedDesktop&&!data.displays.some(function(d){return d.device===savedDesktop;});});};
      self.saveDesktopMonitor=function(){self.desktopBusy=true;self.error='';var requested=self.desktopMonitor;query('QUERY_OSC_SET_DESKTOP_MONITOR',requested,function(data){self.desktopBusy=false;if(!data||data.saved!==true){self.desktopMonitor=savedDesktop;self.error='Could not change display. Check that it is connected.';}else{savedDesktop=requested;self.desktopDisconnected=false;}});};
      $scope.flexMenu=self;activeEditor=self;var unsubscribe=api.subscribe(update);
      $scope.$on('$destroy',function(){unsubscribe();if(activeEditor===self)activeEditor=null;});if(wantsEditing)self.start();
    }],link:function(scope,element){
      var root=element[0],doc=$document[0],drag=null,ghost=null,target=null,clickSuppressed=false,suppressTimer=null;
      function editor(){return scope.flexMenu;}
      function closest(node,selector){return node&&node.closest?node.closest(selector):null;}
      function identify(node){var e=closest(node,'[data-menu-key],md-menu[data-nv-control]');return e&&(e.getAttribute('data-menu-key')||e.getAttribute('data-nv-control'));}
      function captureClick(event){if(!editor().editing)return;var key=identify(event.target);if(closest(event.target,'md-menu')){event.preventDefault();event.stopImmediatePropagation();if(key&&!clickSuppressed)scope.$evalAsync(function(){editor().select(key);});}else if(clickSuppressed){event.preventDefault();event.stopImmediatePropagation();}clickSuppressed=false;}
      function down(event){if(!editor().editing||event.button!==0||closest(event.target,'.nv-live-remove'))return;var key=identify(event.target);if(!known(key))return;drag={key:key,x:event.clientX,y:event.clientY,active:false};event.preventDefault();}
      function clearTarget(){if(target&&target.node)target.node.classList.remove('nv-live-drop-target');target=null;}
      function move(event){if(!drag)return;if(!drag.active&&Math.abs(event.clientX-drag.x)+Math.abs(event.clientY-drag.y)<6)return;
        if(!drag.active){drag.active=true;ghost=doc.createElement('div');ghost.className='nv-live-drag-ghost';ghost.textContent=editor().label(drag.key);doc.body.appendChild(ghost);root.classList.add('nv-live-dragging');}
        ghost.style.left=(event.clientX+14)+'px';ghost.style.top=(event.clientY+14)+'px';clearTarget();
        var node=doc.elementFromPoint(event.clientX,event.clientY),chip=closest(node,'[data-menu-key][data-menu-group]'),panel=closest(node,'.nv-live-drop'),tile=closest(node,'md-menu[data-nv-control]');
        if(chip)target={group:chip.getAttribute('data-menu-group'),index:Number(chip.getAttribute('data-menu-index')),node:chip};
        else if(panel){var group=panel.getAttribute('data-menu-group');target={group:group,index:api.get()[group].length,node:panel};}
        else if(tile){var key=tile.getAttribute('data-nv-control'),layout=api.get();['left','center'].some(function(g){var i=layout[g].indexOf(key);if(i<0)return false;target={group:g,index:i,node:tile};return true;});}
        if(target)target.node.classList.add('nv-live-drop-target');event.preventDefault();
      }
      function cancel(){clearTarget();if(ghost)ghost.remove();ghost=null;drag=null;root.classList.remove('nv-live-dragging');}
      function up(){if(!drag)return;if(drag.active){clickSuppressed=true;if(suppressTimer)$window.clearTimeout(suppressTimer);suppressTimer=$window.setTimeout(function(){clickSuppressed=false;suppressTimer=null;},0);if(target){var key=drag.key,g=target.group,i=target.index;scope.$evalAsync(function(){editor().move(key,g,i);});}}cancel();}
      function keydown(event){if(event.key==='Escape'&&drag){cancel();event.preventDefault();event.stopImmediatePropagation();return;}if(editor().editing&&closest(event.target,'md-menu')&&(event.key==='Enter'||event.key===' '||event.keyCode===13||event.keyCode===32)){captureClick(event);return;}
        if(editor().editing||!/^Arrow/.test(event.key)||closest(event.target,'md-menu-content')||doc.querySelector('.md-open-menu-container.md-active'))return;
        var button=closest(event.target,'button.menu-button');if(!button)return;var r=button.getBoundingClientRect(),cx=r.left+r.width/2,cy=r.top+r.height/2,best=null,score=Infinity;
        Array.prototype.forEach.call(root.querySelectorAll('md-menu>button.menu-button:not([disabled])'),function(b){if(b===button)return;var q=b.getBoundingClientRect();if(!q.width||!q.height)return;var dx=q.left+q.width/2-cx,dy=q.top+q.height/2-cy;var primary=event.key==='ArrowLeft'?-dx:event.key==='ArrowRight'?dx:event.key==='ArrowUp'?-dy:dy,secondary=/Left|Right/.test(event.key)?Math.abs(dy):Math.abs(dx);if(primary>1&&primary+secondary*3<score){score=primary+secondary*3;best=b;}});
        if(best){event.preventDefault();event.stopImmediatePropagation();best.focus();}
      }
      root.addEventListener('click',captureClick,true);root.addEventListener('mousedown',down,true);root.addEventListener('keydown',keydown,true);doc.addEventListener('mousemove',move,true);doc.addEventListener('mouseup',up,true);$window.addEventListener('blur',cancel);
      scope.$on('$destroy',function(){cancel();if(suppressTimer)$window.clearTimeout(suppressTimer);root.removeEventListener('click',captureClick,true);root.removeEventListener('mousedown',down,true);root.removeEventListener('keydown',keydown,true);doc.removeEventListener('mousemove',move,true);doc.removeEventListener('mouseup',up,true);$window.removeEventListener('blur',cancel);});
    }};
  }]);
})(window.angular);
