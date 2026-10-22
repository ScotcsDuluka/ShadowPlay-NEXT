'use strict';
const fs=require('fs');
(async()=>{
 const tab=(await(await fetch('http://127.0.0.1:59099/json/list')).json()).find(x=>x.type==='page'&&x.url.includes('59011'));
 if(!tab)throw Error('CEF OSC page unavailable');const ws=new WebSocket(tab.webSocketDebuggerUrl);
 await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j});let seq=0,pend=new Map();
 ws.onmessage=e=>{const m=JSON.parse(e.data),f=pend.get(m.id);if(f){pend.delete(m.id);f(m)}};
 const call=(method,params={})=>new Promise((r,j)=>{const id=++seq,t=setTimeout(()=>{pend.delete(id);j(Error('CDP timeout'))},9000);pend.set(id,m=>{clearTimeout(t);m.error?j(Error(JSON.stringify(m.error))):r(m.result)});ws.send(JSON.stringify({id,method,params}))});
 async function evaluate(expression){const x=await call('Runtime.evaluate',{expression,returnByValue:true});if(x.exceptionDetails)throw Error(JSON.stringify(x.exceptionDetails));return x.result.value}
 const started=await evaluate("({route:location.hash,editing:document.documentElement.classList.contains('nv-live-layout-editing')})");if(!started.editing)throw Error('Customize not active: '+JSON.stringify(started));
 console.log('BEFORE',JSON.stringify(started));
 await evaluate("(function(){var link=Array.prototype.filter.call(document.querySelectorAll('link[rel=stylesheet]'),function(x){return x.href.indexOf('main-menu-live.css')>=0})[0];if(!link)throw Error('Live stylesheet not found');link.setAttribute('href','/mods/main-menu-live.css?v=20261023-native-topbar');return link.href;})()");
 await new Promise(r=>setTimeout(r,1200));
 const inspect=`(function(){
  var sheets=Array.prototype.filter.call(document.styleSheets,function(s){return (s.href||'').indexOf('main-menu-live.css')>=0});
  var loaded=sheets.some(function(s){try{return Array.prototype.some.call(s.cssRules,function(r){return r.selectorText==='html.nv-live-layout-editing nv-osc-top-bar .top-bar'&&(r.style.visibility==='visible')})}catch(e){return false}});
  if(loaded){var t=document.getElementById('nv-topbar-temporary-test');if(t)t.parentNode.removeChild(t)}
  var b=document.querySelector('nv-osc-top-bar .top-bar'),close=b&&b.querySelector('.top-bar-close-button'),logo=b&&b.querySelector('img'),s=b&&getComputedStyle(b),r=close&&close.getBoundingClientRect(),at=r&&document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);
  var tiles=Array.prototype.slice.call(document.querySelectorAll('md-menu[data-nv-unified-key]')),clickable=tiles.filter(function(x){var k=x.getBoundingClientRect(),h=document.elementFromPoint(k.left+k.width/2,k.top+k.height/2);return h&&x.contains(h)}).length;
  return {route:location.hash,loaded:loaded,temporary:!!document.getElementById('nv-topbar-temporary-test'),linked:sheets.map(function(a){return a.href}),bar:s&&{visible:s.visibility,pointer:s.pointerEvents,z:s.zIndex,position:s.position},
   closeHit:!!(close&&at&&close.contains(at)),closeHitName:at&&at.tagName,logoVisible:logo&&getComputedStyle(logo).visibility,tiles:tiles.length,clickableTiles:clickable};
 })()`;
 const state=await evaluate(inspect);console.log('VERIFIED',JSON.stringify(state));
 if(!state.loaded||state.temporary||!state.closeHit||state.bar.visible!=='visible'||state.bar.pointer!=='auto')throw Error('Permanent topbar CSS verification FAILED');
 const pic=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});
 const dest='C:/My Project/NVIDIA-Shadowplay/build_test/NVIDIA OSC TopBar Restore 20261023/live-after-native-topbar.png';
 fs.writeFileSync(dest,Buffer.from(pic.data,'base64'));console.log('EVIDENCE',dest);
 ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
