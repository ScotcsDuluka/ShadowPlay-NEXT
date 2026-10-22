'use strict';
const fs=require('fs');
(async()=>{
 const tabs=await(await fetch('http://127.0.0.1:59099/json/list')).json();
 const tab=tabs.find(t=>t.type==='page'&&t.url.includes('59011'));
 if(!tab)throw Error('OSC CEF page missing');
 const ws=new WebSocket(tab.webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
 let id=0;const pending=new Map();
 ws.onmessage=ev=>{const m=JSON.parse(ev.data),fn=pending.get(m.id);if(fn){pending.delete(m.id);fn(m)}};
 function call(method,params){
   return new Promise((resolve,reject)=>{
     const num=++id,t=setTimeout(()=>{pending.delete(num);reject(Error('Timeout '+method));},8000);
     pending.set(num,m=>{clearTimeout(t);m.error?reject(Error(JSON.stringify(m.error))):resolve(m.result)});
     ws.send(JSON.stringify({id:num,method,params}));
   });
 }
 const expr=`(function(){
  function info(el){
   if(!el)return null;
   var r=el.getBoundingClientRect(),s=getComputedStyle(el);
   return {tag:el.tagName,id:el.id,cls:String(el.className&&el.className.baseVal||el.className||'').slice(0,170),
   rect:[r.x,r.y,r.width,r.height].map(function(v){return Math.round(v*10)/10}),
   display:s.display,visibility:s.visibility,opacity:s.opacity,z:s.zIndex,pointer:s.pointerEvents,
   transform:s.transform,overflow:s.overflow,position:s.position};
  }
  var names=['.nv-live-editor','.nv-live-workspace','.nv-unified-editor','.nv-group-layout-editor',
    '.nv-g-panels','.nv-g-toolbar','nv-main-menu','.main-menu-container','md-menu-bar',
    '[data-customize-preview]','.nv-customize-screen','.nv-customize-preview'];
  var out={url:location.href,viewport:[innerWidth,innerHeight],flags:{v4:localStorage.getItem('DulukaMainMenuUnifiedV4Enabled'),
    v5:localStorage.getItem('DulukaMainMenuGroupLayoutAEnabled')},route:location.hash,
    cssState:document.documentElement.className,
    v4:!!window.__dulukaUnifiedMainMenu,
    v5:!!window.__dulukaGroupLayoutA,els:{},sampleHits:[]};
  names.forEach(function(n){out.els[n]=Array.prototype.slice.call(document.querySelectorAll(n),0,5).map(info)});
  var area=document.querySelector('.nv-live-editor')||document.querySelector('nv-main-menu');
  if(area){var r=area.getBoundingClientRect();[[.2,.15],[.5,.15],[.8,.15],[.2,.5],[.5,.5],[.8,.5],[.5,.85]].forEach(function(x){
   var px=r.left+r.width*x[0],py=r.top+r.height*x[1],hit=document.elementFromPoint(px,py);
   out.sampleHits.push({at:[Math.round(px),Math.round(py)],hit:info(hit),
     parents:hit?Array.prototype.slice.call((function(){var a=[],e=hit;while(e&&a.length<6){a.push(e);e=e.parentElement}return a})()).map(function(x){return x.tagName+'.'+String(x.className||'').slice(0,60)}):[]});
  })}
  out.previewCandidates=Array.prototype.slice.call(document.querySelectorAll('[class*="preview"],[class*="view"],[class*="workspace"]')).slice(0,60).map(info);
  return out;
 })()`;
 const r=await call('Runtime.evaluate',{expression:expr,returnByValue:true,awaitPromise:false});
 if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));
 fs.writeFileSync(__dirname+'/cef-readonly-audit.json',JSON.stringify(r.result.value,null,2));
 console.log(JSON.stringify(r.result.value,null,2).slice(0,27000));
 ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
