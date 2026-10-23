/* Background adapted from Web Github/index.html, CSS/index/style.css and
 * JS/index/script.js. It owns no Main-menu controls or layout data. CEF 73 safe. */
(function(window,document){
  'use strict';
  var storageKey='DulukaOscBackground';
  var defaults={mode:'website',opacity:50,motion:true,particles:80,links:true,react:true,speed:1};
  var current,error='',subscribers=[];
  function copy(value){return JSON.parse(JSON.stringify(value));}
  function number(value,min,max,fallback){return typeof value==='number'&&isFinite(value)?Math.max(min,Math.min(max,value)):fallback;}
  function bool(value,fallback){return typeof value==='boolean'?value:fallback;}
  function normalize(value){value=value&&typeof value==='object'?value:{};return {
    mode:value.mode==='classic'?'classic':'website',opacity:number(value.opacity,0,100,50),motion:bool(value.motion,true),particles:Math.round(number(value.particles,0,120,80)),links:bool(value.links,true),react:bool(value.react,true),speed:number(value.speed,.25,2,1)
  };}
  try{current=normalize(JSON.parse(window.localStorage.getItem(storageKey)||'null'));}catch(ignore){current=copy(defaults);}
  var api=window.__dulukaOscBackground={defaults:copy(defaults),normalize:normalize,get:function(){return copy(current);},set:function(value){var next=normalize(value);try{window.localStorage.setItem(storageKey,JSON.stringify(next));}catch(ignore){error='Could not save background settings. Previous settings are still active.';return false;}current=next;error='';subscribers.slice().forEach(function(fn){fn(copy(current));});return true;},reset:function(){return api.set(defaults);},subscribe:function(fn){subscribers.push(fn);return function(){subscribers=subscribers.filter(function(listener){return listener!==fn;});};}};
  Object.defineProperty(api,'error',{get:function(){return error;}});

  function start(){
    if(document.getElementById('nv-osc-background'))return;
    var root=document.createElement('div');root.id='nv-osc-background';root.setAttribute('aria-hidden','true');
    var glow=document.createElement('div');glow.className='nv-osc-bg-glow';root.appendChild(glow);
    var specks=document.createElement('div');specks.className='nv-osc-bg-specks';root.appendChild(specks);
    var canvas=document.createElement('canvas');root.appendChild(canvas);document.body.insertBefore(root,document.body.firstChild);
    var ctx=null,gpu=null,renderChoice='auto',particles=[],width=0,height=0,frame=null,lastTime=0,visible=false;
    function selectRenderer(choice){
      if(choice!=='gpu'&&choice!=='cpu')choice='auto';
      if(renderChoice===choice&&(ctx||gpu))return;
      // Keep the original canvas instance on first boot (and in mock DOMs).
      if(!ctx&&!gpu&&choice!=='gpu'){
        renderChoice=choice;ctx=canvas.getContext('2d');return;
      }
      renderChoice=choice;
      var replacement=document.createElement('canvas');
      if(canvas.parentNode)canvas.parentNode.replaceChild(replacement,canvas);
      canvas=replacement;ctx=null;gpu=null;
      if(choice==='gpu'&&window.__dulukaBgWebgl)
        gpu=window.__dulukaBgWebgl.create(canvas);
      if(!gpu)ctx=canvas.getContext('2d');
      if(width&&height){canvas.width=width;canvas.height=height;draw(0);}
    }
    api.setRenderer=selectRenderer;
    api.getRenderer=function(){return {requested:renderChoice,active:gpu?'webgl':'canvas2d'};};
    selectRenderer('auto');
    var mouse={x:null,y:null},reduced=window.matchMedia?window.matchMedia('(prefers-reduced-motion: reduce)'):null;
    function initialize(){particles=[];for(var i=0;i<current.particles;i++)particles.push({x:Math.random()*width,y:Math.random()*height,size:Math.random()*.5+1,speedX:Math.random()*1.2-.5,speedY:Math.random()*1.2-.5,opacity:Math.random()*.9+.4});}
    function resize(){width=Math.max(1,window.innerWidth);height=Math.max(1,window.innerHeight);canvas.width=width;canvas.height=height;initialize();draw(0);}
    function draw(delta){if(!ctx&&!gpu)return;if(ctx)ctx.clearRect(0,0,width,height);
      particles.forEach(function(p){
        if(delta){p.x+=p.speedX*delta;p.y+=p.speedY*delta;if(p.x>width||p.x<0){p.x=Math.max(0,Math.min(width,p.x));p.speedX*=-1;}if(p.y>height||p.y<0){p.y=Math.max(0,Math.min(height,p.y));p.speedY*=-1;}
          if(current.react&&mouse.x!==null&&mouse.y!==null){var dx=p.x-mouse.x,dy=p.y-mouse.y,squared=dx*dx+dy*dy;if(squared<14400){var d=Math.sqrt(squared);if(d<120){var force=(120-d)/120,angle=Math.atan2(dy,dx);p.x+=Math.cos(angle)*force*3*delta;p.y+=Math.sin(angle)*force*3*delta;}}}
        }
        if(ctx){ctx.fillStyle='rgba(118,185,0,'+Math.min(1,p.opacity)+')';ctx.beginPath();ctx.arc(p.x,p.y,p.size,0,Math.PI*2);ctx.fill();}
      });
      if(gpu){if(!gpu.healthy()){selectRenderer('cpu');return;}gpu.draw(particles,width,height,current.links);return;}
      if(ctx&&current.links)for(var a=0;a<particles.length;a++)for(var b=a+1;b<particles.length;b++){var dx=particles[a].x-particles[b].x,dy=particles[a].y-particles[b].y,squared=dx*dx+dy*dy;if(squared>=12100)continue;var distance=Math.sqrt(squared);if(distance<110){ctx.strokeStyle='rgba(118,185,0,'+((110-distance)/120*.95)+')';ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(particles[a].x,particles[a].y);ctx.lineTo(particles[b].x,particles[b].y);ctx.stroke();}}
    }
    function animated(){return visible&&current.mode==='website'&&current.opacity>0&&current.motion&&!(reduced&&reduced.matches)&&!document.hidden;}
    function tick(now){frame=null;if(!animated())return;if(!lastTime||now-lastTime>=32){var delta=lastTime?Math.min(3,(now-lastTime)/16.667)*current.speed:1;lastTime=now;draw(delta);}frame=window.requestAnimationFrame(tick);}
    function update(){
      visible=document.documentElement.hasAttribute('data-osc-bg-preview')||!!(window.location.hash&&window.location.hash!=='#/base'&&window.location.hash!=='#/base/');
      var website=visible&&current.mode==='website';
      // Both Classic and Website must paint the same fixed full-viewport
      // surface. The native .nv-osc content begins below the top bar and
      // cannot serve as the fullscreen Classic backdrop.
      root.style.display=visible?'block':'none';root.style.opacity=String(current.opacity/100);
      document.documentElement.classList.toggle('nv-osc-bg-website',website);document.documentElement.classList.toggle('nv-osc-bg-classic',visible&&!website);
      document.documentElement.style.setProperty('--nv-osc-bg-opacity',String(current.opacity/100));
      root.style.setProperty('--nv-osc-bg-gradient-duration',(15/current.speed)+'s');root.style.setProperty('--nv-osc-bg-float-duration',(20/current.speed)+'s');
      root.classList.toggle('nv-osc-bg-paused',!animated());
      if(frame!==null&&!animated()){window.cancelAnimationFrame(frame);frame=null;}lastTime=0;
      if(particles.length!==current.particles)initialize();if(website)draw(0);
      if(animated()&&frame===null)frame=window.requestAnimationFrame(tick);
    }
    function pointer(event){mouse.x=event.clientX;mouse.y=event.clientY;}
    function clearPointer(){mouse.x=null;mouse.y=null;}
    resize();update();api.subscribe(update);
    // Load BG preference on first boot even when the settings page is unopened.
    if(typeof window.cefQuery==='function')try{
      window.cefQuery({request:JSON.stringify({command:'QUERY_OSC_GET_RENDERERS'}),
        onSuccess:function(raw){try{var state=JSON.parse(raw);if(state&&state.background)selectRenderer(state.background);}catch(ignore){}},
        onFailure:function(){}});
    }catch(ignore){}
    window.addEventListener('resize',function(){resize();update();});window.addEventListener('hashchange',update);window.addEventListener('mousemove',pointer);window.addEventListener('blur',clearPointer);document.addEventListener('mouseleave',clearPointer);document.addEventListener('visibilitychange',update);
    if(reduced&&reduced.addListener)reduced.addListener(update);
  }
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});else start();

  if(!window.angular)return;
  var template=[
    '<section class="nv-osc-background-settings" aria-label="Overlay background">',
    '<div class="nv-osc-bg-heading"><div><h3 translate="l10n.oscBackgroundTitle">Overlay background</h3><p translate="l10n.oscBackgroundInfo">Website particles with the original translucent overlay.</p></div><button class="osc-button osc-button-gray" ng-click="background.reset()" translate="l10n.oscResetBackground">Reset background</button></div>',
    '<div class="nv-osc-bg-settings-grid">',
    '<div class="nv-osc-bg-field"><label translate="l10n.oscBackgroundStyle">Background style</label><md-select aria-label="Background style" ng-model="background.settings.mode" ng-change="background.save()"><md-option ng-value="\'website\'" translate="l10n.oscWebsiteParticles">Website — particles</md-option><md-option ng-value="\'classic\'" translate="l10n.oscNvidiaClassic">NVIDIA classic</md-option></md-select></div>',
    '<div class="nv-osc-bg-field"><label><span translate="l10n.oscBackgroundOpacity">Background opacity</span> <output>{{background.settings.opacity | number:0}}%</output></label><md-slider aria-label="Background opacity" min="0" max="100" step="1" ng-model="background.settings.opacity" ng-change="background.save()"></md-slider></div>',
    '<div class="nv-osc-bg-field"><label><span translate="l10n.oscParticles">Particles</span> <output>{{background.settings.particles}}</output></label><md-slider aria-label="Particle count" min="0" max="120" step="10" ng-model="background.settings.particles" ng-change="background.save()" ng-disabled="background.settings.mode===\'classic\'"></md-slider></div>',
    '<div class="nv-osc-bg-field"><label><span translate="l10n.oscAnimationSpeed">Animation speed</span> <output>{{background.settings.speed | number:2}}×</output></label><md-slider aria-label="Animation speed" min="0.25" max="2" step="0.25" ng-model="background.settings.speed" ng-change="background.save()" ng-disabled="!background.settings.motion || background.settings.mode===\'classic\'"></md-slider></div>',
    '</div><div class="nv-osc-bg-switches"><md-switch aria-label="Background animation" ng-model="background.settings.motion" ng-change="background.save()"><span translate="l10n.oscAnimation">Animation</span></md-switch><md-switch aria-label="Connecting lines" ng-model="background.settings.links" ng-change="background.save()"><span translate="l10n.oscConnectingLines">Connecting lines</span></md-switch><md-switch aria-label="React to pointer" ng-model="background.settings.react" ng-change="background.save()"><span translate="l10n.oscReactPointer">React to pointer</span></md-switch></div>',
    '<p class="nv-osc-bg-error" role="alert" ng-if="background.error">{{background.error}}</p></section>'
  ].join('');
  window.angular.module('main').directive('nvOscBackgroundSettings',[function(){return {restrict:'E',scope:true,template:template,controller:['$scope',function($scope){var self=this;self.settings=api.get();self.error='';self.save=function(){if(!api.set(self.settings)){self.settings=api.get();self.error=api.error;}else self.error='';};self.reset=function(){self.error=api.reset()?'':api.error;};$scope.background=self;var unsubscribe=api.subscribe(function(settings){if(!$scope.$$destroyed)$scope.$evalAsync(function(){self.settings=settings;});});$scope.$on('$destroy',unsubscribe);} ]};}]);
  // Additive companion directive: never modifies the other chat's editor controller.
  window.angular.module('main').directive('nvMainMenuLiveEditor',['$compile','$timeout',function($compile,$timeout){return {restrict:'A',require:'nvMainMenuLiveEditor',link:function(scope,element){
    var child=null,node=null,pending=null;
    function clear(){if(pending)$timeout.cancel(pending);pending=null;if(child&&!child.$$destroyed)child.$destroy();child=null;if(node&&node.parentNode)node.parentNode.removeChild(node);node=null;}
    var unwatch=scope.$watch('flexMenu.editing',function(editing){clear();if(!editing)return;pending=$timeout(function(){pending=null;if(scope.$$destroyed)return;var panel=element[0].querySelector('.nv-live-editor');if(!panel)return;child=scope.$new();node=document.createElement('nv-osc-background-settings');panel.appendChild(node);$compile(node)(child);},0);});
    scope.$on('$destroy',function(){unwatch();clear();});
  }};}]);
})(window,document);
