(function(){
 var s=angular.element(document.documentElement).injector().get('$state');
 function snap(at){
 var root=document.querySelector('nv-main-menu .main-menu-container'),bar=root&&root.querySelector('md-menu-bar');
 function box(el){if(!el)return null;var r=el.getBoundingClientRect(),c=getComputedStyle(el);return {x:r.x,y:r.y,w:r.width,h:r.height,transform:c.transform,inline:el.getAttribute('style')};}
 var m=window.__dulukaOscMotion;
 return {at:at,route:s.current.name,phase:m&&m.phase,root:box(root),bar:box(bar),tile:box(root&&root.querySelector('md-menu.screenshot-tile'))};
 }
 function pause(ms){return new Promise(function(ok){setTimeout(ok,ms)})}
 var results=[];
 return s.go('main.main-menu').then(function(){results.push(snap(0));return pause(250)})
 .then(function(){results.push(snap(250));return pause(300)})
 .then(function(){results.push(snap(550));return pause(350)})
 .then(function(){results.push(snap(900));return pause(400)})
 .then(function(){results.push(snap(1300));return {results:results}})
})()