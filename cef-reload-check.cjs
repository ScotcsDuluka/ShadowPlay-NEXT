'use strict';
(async()=>{
 const tabs=await (await fetch('http://127.0.0.1:59099/json/list')).json();
 const tab=tabs.find(x=>x.type==='page'&&x.url.includes('59011'));
 if(!tab)throw Error('No OSC CEF page');
 const ws=new WebSocket(tab.webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
 let id=0;const callbacks=new Map();
 ws.onmessage=ev=>{const m=JSON.parse(ev.data),fn=callbacks.get(m.id);if(fn){callbacks.delete(m.id);fn(m);}};
 const call=(method,params)=>new Promise((resolve,reject)=>{
  const nr=++id,t=setTimeout(()=>{callbacks.delete(nr);reject(Error('CDP '+method+' timeout'));},10000);
  callbacks.set(nr,v=>{clearTimeout(t);v.error?reject(Error(JSON.stringify(v.error))):resolve(v.result)});
  ws.send(JSON.stringify({id:nr,method,params:params||{}}));
 });
 const before=await call('Runtime.evaluate',{expression:'location.hash',returnByValue:true});
 console.log('BEFORE',before.result.value);
 // Reload only the CEF web page; do not launch or close the game.
 await call('Page.reload',{ignoreCache:true});
 await new Promise(resolve=>setTimeout(resolve,1900));
 const state=await call('Runtime.evaluate',{expression:`(function(){
  var u=window.__dulukaUnifiedMainMenu,g=window.__dulukaGroupLayoutA;
  var links=Array.prototype.slice.call(document.querySelectorAll('link[rel=stylesheet]')).map(function(n){return n.getAttribute('href')||'';});
  return {route:location.hash,htmlClasses:document.documentElement.className,
  v4:!!u,v5:!!g,enabled:g&&g.enabled(),nativeTopBand:u&&u.nativeGeometry.toString().indexOf('--nv-top-preview-height')>=0,
  groupSideRatio:window.NvGroupLayoutA&&window.NvGroupLayoutA.PRESETS.side.detailLeft,
  css:links.filter(function(x){return x.indexOf('main-menu-live.css')>=0})};
 })()`,returnByValue:true});
 console.log('AFTER',JSON.stringify(state.result.value));
 ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
