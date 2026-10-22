'use strict';
(async()=>{
 const t=(await(await fetch('http://127.0.0.1:59099/json/list')).json()).find(x=>x.type==='page'&&x.url.includes('59011'));if(!t)throw Error('OSC page not found');
 const ws=new WebSocket(t.webSocketDebuggerUrl);await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j});
 const result=await new Promise((r,j)=>{const n=1,timeout=setTimeout(()=>j(Error('CDP timeout')),7500);
  ws.onmessage=e=>{const x=JSON.parse(e.data);if(x.id===n){clearTimeout(timeout);x.error?j(Error(JSON.stringify(x.error))):r(x.result)}};
  const expr=`(function(){
   var links=Array.prototype.slice.call(document.querySelectorAll('link[href*="main-menu-live.css"]'));
   var sheets=Array.prototype.slice.call(document.styleSheets).filter(function(x){return (x.href||'').indexOf('main-menu-live.css')>=0;});
   var cssReady=sheets.some(function(s){try{return Array.prototype.some.call(s.cssRules,function(r){return (r.cssText||'').indexOf('[nv-main-menu-live-editor]::before')>=0})}catch(e){return false}});
   var before=!!document.getElementById('nv-topview-temp-backdrop');
   if(cssReady&&before){var temp=document.getElementById('nv-topview-temp-backdrop');temp.parentNode.removeChild(temp);}
   var editor=document.querySelector('.nv-live-editor'),host=document.querySelector('[nv-main-menu-live-editor]');
   var pseudo=host&&getComputedStyle(host,'::before'),native=Array.prototype.slice.call(document.querySelectorAll('md-menu[data-nv-unified-key]'));
   return {route:location.hash,cssReady:cssReady,links:links.map(function(x){return x.getAttribute('href')}),
    stylesheetCount:sheets.length,oldTempBefore:before,tempAfter:!!document.getElementById('nv-topview-temp-backdrop'),editor:!!editor,
    backdrop:pseudo&&{content:pseudo.content,background:pseudo.backgroundColor,pointer:pseudo.pointerEvents,z:pseudo.zIndex},
    nativeTotal:native.length,nativeClickable:native.filter(function(x){var r=x.getBoundingClientRect(),h=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);return h&&x.contains(h)}).length};
   })()`;
  ws.send(JSON.stringify({id:n,method:'Runtime.evaluate',params:{expression:expr,returnByValue:true}}));
 });
 if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));
 console.log(JSON.stringify(result.result.value,null,2));ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
