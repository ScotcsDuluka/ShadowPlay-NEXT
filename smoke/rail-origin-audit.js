(function(){
 var r=angular.element(document.documentElement).injector().get('$state');
 function rect(e){if(!e)return null;var b=e.getBoundingClientRect(),s=getComputedStyle(e);return {tag:e.tagName,className:e.className,box:{x:b.x,y:b.y,w:b.width,h:b.height},offsetLeft:e.offsetLeft,offsetTop:e.offsetTop,left:s.left,top:s.top,transform:s.transform,inline:e.getAttribute('style'),position:s.position};}
 return r.go('main.main-menu').then(function(){return new Promise(function(done){setTimeout(function(){
 var root=document.querySelector('nv-main-menu .main-menu-container'),bar=root&&root.querySelector('md-menu-bar'),p=root;
 var ancestors=[];for(var i=0;p&&i<8;i++,p=p.parentElement)ancestors.push(rect(p));
 done({route:location.hash,screen:{w:innerWidth,h:innerHeight},ancestors:ancestors,bar:rect(bar),firstTile:rect(root.querySelector('md-menu.screenshot-tile'))});
 },360);});});
})()