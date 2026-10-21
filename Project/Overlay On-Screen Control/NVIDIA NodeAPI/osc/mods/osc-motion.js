/* Native OSC adapter. Layout and control identities remain owned by NVIDIA/live editor. */
(function(window,document){
  'use strict';
  var root=document.documentElement,media=window.matchMedia?window.matchMedia('(prefers-reduced-motion:reduce)'):null;
  var controller=null,prepared=false,wanted='closed',pendingClose=null,intent=0,linkFrame=null,linkAttempts=0,commitQueued=false;
  var records=[],group=null,blocks=[],header=null,geometry={enter:1,exit:1},paintCanvas=null,paintContext=null,paintTick=0;
  var suppressRender=false,forceInstant=false,dispatch=function(callback){callback();};
  var timing=window.__dulukaOscMotionTiming||{open:{duration:500,bezier:[0,.9524242401123044,.28,1]},closed:{duration:240,bezier:[.6681970923837989,.04272720336914062,.005,1]}};
  function reduced(){return forceInstant||!!document.hidden||!!(media&&media.matches);}
  function stopPaint(){if(paintCanvas&&paintCanvas.parentNode)paintCanvas.parentNode.removeChild(paintCanvas);paintCanvas=paintContext=null;}
  function touchPaint(){
    if(!document.body||!document.createElement)return;
    if(!paintCanvas){paintCanvas=document.createElement('canvas');paintCanvas.width=paintCanvas.height=1;paintCanvas.setAttribute('aria-hidden','true');paintCanvas.setAttribute('data-nv-motion-repaint','');paintCanvas.style.cssText='position:fixed;left:0;top:0;width:1px;height:1px;pointer-events:none;opacity:.01;z-index:2147483647';document.body.appendChild(paintCanvas);paintContext=paintCanvas.getContext('2d');}
    if(paintContext){paintContext.fillStyle=paintTick++%2?'#fff':'#000';paintContext.fillRect(0,0,1,1);}
  }
  function clearClasses(){['nv-motion-preparing','nv-motion-active','nv-motion-opening','nv-motion-closing'].forEach(function(name){root.classList.remove(name);});}
  function releaseVisual(){
    records.forEach(function(record){Object.keys(record.saved).forEach(function(key){var old=record.saved[key];if(old.value)record.node.style.setProperty(key,old.value,old.priority);else record.node.style.removeProperty(key);});if(record.had)record.node.setAttribute(record.attribute,record.value);else record.node.removeAttribute(record.attribute);});records=[];
    clearClasses();root.style.removeProperty('--nv-motion-bg-alpha');stopPaint();
  }
  function mark(node,type){
    if(!node)return null;var attribute='data-nv-motion-'+type;
    var record={node:node,attribute:attribute,had:node.hasAttribute(attribute),value:node.getAttribute(attribute),saved:{}};
    var style=window.getComputedStyle?window.getComputedStyle(node):null;record.base=style&&style.transform&&style.transform!=='none'?style.transform:'translate3d(0,0,0)';
    node.setAttribute(attribute,'');records.push(record);write(record,'--nv-motion-base',record.base);return record;
  }
  function write(record,key,value){
    if(!record)return;var style=record.node.style;
    if(!Object.prototype.hasOwnProperty.call(record.saved,key))record.saved[key]={value:style.getPropertyValue(key),priority:style.getPropertyPriority?style.getPropertyPriority(key):''};
    value=String(value);if(style.getPropertyValue(key)!==value)style.setProperty(key,value);
  }
  var groupRecord=null,blockRecords=[],headerRecord=null;
  function collect(){
    releaseVisual();groupRecord=headerRecord=null;blockRecords=[];
    group=document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container > md-toolbar.list');
    blocks=group?Array.prototype.slice.call(group.querySelectorAll('md-menu')).filter(function(node){var box=node.getBoundingClientRect();return box.width>0&&box.height>0;}):[];
    if(!group){var candidates=Array.prototype.slice.call(document.querySelectorAll('.nv-osc .osc-content'));group=candidates.filter(function(node){var box=node.getBoundingClientRect();return box.width>0&&box.height>0;})[0]||null;}
    header=document.querySelector('nv-osc-top-bar .top-bar');
    if(!group&&!header)return false;
    var bounds=blocks.length?blocks.map(function(node){return node.getBoundingClientRect();}):(group?[group.getBoundingClientRect()]:[]);
    var left=bounds.length?Math.min.apply(null,bounds.map(function(box){return box.left;})):0,right=bounds.length?Math.max.apply(null,bounds.map(function(box){return box.right;})):window.innerWidth||1;
    var box=group?group.getBoundingClientRect():null,scale=group&&group.offsetWidth&&box.width?box.width/group.offsetWidth:1;
    if(!isFinite(scale)||scale<=0)scale=1;
    geometry={enter:Math.max(1,(right+2)/scale),exit:Math.max(1,((window.innerWidth||root.clientWidth||1)-left+2)/scale)};
    groupRecord=mark(group,'group');blockRecords=blocks.map(function(node){return mark(node,'block');});headerRecord=mark(header,'top');return true;
  }
  function backgroundOpacity(){try{var settings=window.__dulukaOscBackground&&window.__dulukaOscBackground.get();if(settings&&typeof settings.opacity==='number'&&isFinite(settings.opacity))return Math.max(0,Math.min(1,settings.opacity/100));}catch(ignore){}return .5;}
  function render(pose,state){
    if(suppressRender)return;
    prepared=false;root.classList.remove('nv-motion-preparing');
    if(state.phase==='idle'&&state.target==='open'&&state.requestedTarget==='open'){releaseVisual();return;}
    root.classList.add('nv-motion-active');root.classList.toggle('nv-motion-opening',state.phase==='opening');root.classList.toggle('nv-motion-closing',state.phase==='closing');
    // Move the complete band and scale its native blocks without fading them.
    // Only the background blends; block opacity belongs to Customize edits.
    write(groupRecord,'--nv-motion-x',(pose.x*(pose.x<0?geometry.enter:geometry.exit))+'px');
    blockRecords.forEach(function(record){write(record,'--nv-motion-scale',pose.scale);});
    write(headerRecord,'--nv-motion-y',pose.barY+'px');
    root.style.setProperty('--nv-motion-bg-alpha',String(backgroundOpacity()*pose.bgProgress));
    if(state.phase==='idle')stopPaint();else touchPaint();
  }
  function state(){return controller?controller.getState():{phase:prepared?'preparing':'idle',target:wanted,requestedTarget:wanted};}
  function commitClose(){
    if(commitQueued||!pendingClose||wanted!=='closed')return;commitQueued=true;var token=intent;
    dispatch(function(){commitQueued=false;if(token!==intent||wanted!=='closed'||!pendingClose)return;var current=controller&&controller.getState();if(current&&(current.phase!=='idle'||current.requestedTarget!=='closed'))return;var finish=pendingClose;pendingClose=null;finish();});
  }
  function makeController(initiallyOpen){
    if(typeof window.createMotionController!=='function')return false;
    suppressRender=!!initiallyOpen;
    controller=window.createMotionController({requestFrame:window.requestAnimationFrame.bind(window),cancelFrame:window.cancelAnimationFrame.bind(window),now:function(){return window.performance.now();},startX:-1,exitX:1,loopInputs:true,reducedMotion:reduced,render:render,onFinish:function(target){if(!suppressRender&&target==='closed')commitClose();}});
    controller.setTiming(timing);
    if(initiallyOpen){forceInstant=true;controller.open();forceInstant=false;}
    suppressRender=false;return true;
  }
  function clearLinkFrame(){if(linkFrame!==null)window.cancelAnimationFrame(linkFrame);linkFrame=null;}
  function cancel(){intent++;pendingClose=null;commitQueued=false;clearLinkFrame();prepared=false;wanted='closed';suppressRender=true;if(controller)controller.cancel();controller=null;suppressRender=false;releaseVisual();group=null;blocks=[];header=null;}
  function prepareOpen(){
    if(prepared)return;intent++;pendingClose=null;wanted='open';
    if(controller&&controller.getState().phase!=='idle'){controller.open();return;}
    prepared=true;root.classList.add('nv-motion-preparing');root.style.setProperty('--nv-motion-bg-alpha','0');
  }
  function playOpen(){
    if(controller&&!prepared){open();return;}if(linkFrame!==null)return;if(!prepared)prepareOpen();linkAttempts=0;
    function start(){
      linkFrame=null;
      if(!prepared)return;
      // V4 needs to settle the native md-menu geometry BEFORE collect()
      // captures screen rectangles for the opening animation. Otherwise
      // controls use their old V2 coordinates during opening and jump at idle.
      // This is a one-off preflight, never a reposition on animation frames.
      var unified=window.__dulukaUnifiedMainMenu;
      if(unified&&unified.enabled&&unified.enabled()&&unified.prepareForMotion){
        var nativeRoot=document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container');
        if(nativeRoot)unified.prepareForMotion(nativeRoot);
      }
      if(!collect()){if(++linkAttempts<4){linkFrame=window.requestAnimationFrame(start);return;}prepared=false;releaseVisual();if(wanted==='closed')commitClose();return;}
      if(!makeController(false)){prepared=false;releaseVisual();if(wanted==='closed')commitClose();return;}
      var requested=wanted;controller.open();if(requested==='closed')controller.close();
    }
    // Native view linking/allowOSCPainting completes before these two frames.
    linkFrame=window.requestAnimationFrame(function(){linkFrame=window.requestAnimationFrame(start);});
  }
  function open(){
    intent++;pendingClose=null;wanted='open';
    if(controller){controller.open();return;}
    if(!prepared)prepareOpen();playOpen();
  }
  function close(callback){
    if(pendingClose&&wanted==='closed')return;intent++;wanted='closed';pendingClose=callback;
    if(prepared&&!controller){playOpen();return;}
    if(!controller){if(!collect()||!makeController(true)){releaseVisual();commitClose();return;}}
    else if(controller.getState().phase==='idle'&&controller.getState().target==='open')collect();
    controller.close();var current=controller.getState();if(current.phase==='idle'&&current.requestedTarget==='closed')commitClose();
  }
  function settle(){
    if(!controller){if(prepared){prepared=false;clearLinkFrame();releaseVisual();if(wanted==='closed')commitClose();}return;}
    var requested=controller.getState().requestedTarget;suppressRender=true;controller.cancel();suppressRender=false;forceInstant=true;
    if(requested==='open')controller.open();else controller.close();forceInstant=false;
  }
  var api=window.__dulukaOscMotion={prepareOpen:prepareOpen,playOpen:playOpen,open:open,close:close,cancel:cancel,getState:state,timing:timing};
  Object.defineProperty(api,'phase',{get:function(){return prepared?'preparing':(controller?controller.getState().phase:'idle');}});
  function guardActivation(event){if(api.phase==='idle'||(event.type==='keydown'&&event.key!=='Enter'&&event.key!==' '&&event.keyCode!==13&&event.keyCode!==32))return;var target=event.target;if(target&&target.closest&&(target.closest('.nv-osc md-menu,.nv-osc .osc-button')||target.closest('md-menu-content'))){event.preventDefault();event.stopImmediatePropagation();}}
  document.addEventListener('click',guardActivation,true);document.addEventListener('keydown',guardActivation,true);document.addEventListener('visibilitychange',function(){if(document.hidden)settle();});window.addEventListener('pagehide',cancel);
  if(media&&media.addListener)media.addListener(function(){if(media.matches)settle();});
  if(!window.angular)return;
  window.angular.module('main').config(['$provide',function($provide){$provide.decorator('oscDisplayService',['$delegate','$rootScope',function($delegate,$rootScope){
    if($delegate.__dulukaMotionWrapped)return $delegate;var originalClose=$delegate.closeOSC,originalOpen=$delegate.openOSC;
    dispatch=function(callback){$rootScope.$evalAsync(callback);};
    $delegate.closeOSC=function(){var context=this,args=arguments;api.close(function(){originalClose.apply(context,args);});};
    $delegate.openOSC=function(){var context=this,args=arguments,reopening=!!pendingClose||wanted==='closed'&&controller&&controller.getState().phase!=='idle';if(reopening){api.open();if(!args.length||!args[0])return;}return originalOpen.apply(context,args);};
    $delegate.__dulukaMotionWrapped=true;return $delegate;
  }]);}]).run(['$rootScope','$timeout',function($rootScope,$timeout){
    var pending=null;function isOpen(value){return value&&value.name&&value.name!=='base';}
    function schedule(){if(pending)$timeout.cancel(pending);pending=$timeout(function(){pending=null;if(prepared)api.playOpen();},0,false);}
    $rootScope.$on('$stateChangeStart',function(event,to,params,from){if(isOpen(to)&&!isOpen(from))api.prepareOpen();else if(isOpen(to)&&isOpen(from)&&to.name!==from.name&&api.phase!=='idle')api.cancel();});
    $rootScope.$on('$stateChangeSuccess',function(event,to){if(!isOpen(to)){if(pending)$timeout.cancel(pending);pending=null;api.cancel();}else if(prepared)schedule();});
    $rootScope.$on('$viewContentLoaded',function(){if(prepared)schedule();});
    $rootScope.$on('$stateChangeError',function(){if(pending)$timeout.cancel(pending);pending=null;api.cancel();});
  }]);
})(window,document);
