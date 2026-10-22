'use strict';
(async()=>{
 const tabs=await(await fetch('http://127.0.0.1:59099/json/list')).json(),t=tabs.find(x=>x.type==='page'&&x.url.includes('59011'));
 if(!t)throw Error('OSC CEF unavailable'); const ws=new WebSocket(t.webSocketDebuggerUrl);
 await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j});let id=0;const pending=new Map();
 ws.onmessage=e=>{const m=JSON.parse(e.data),f=pending.get(m.id);if(f){pending.delete(m.id);f(m)}};
 const call=(method,params={})=>new Promise((r,j)=>{const n=++id,tm=setTimeout(()=>{pending.delete(n);j(Error('timeout '+method))},9000);pending.set(n,m=>{clearTimeout(tm);m.error?j(Error(JSON.stringify(m.error))):r(m.result)});ws.send(JSON.stringify({id:n,method,params}))});
 const evaljs=async expr=>{const r=await call('Runtime.evaluate',{expression:expr,returnByValue:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value};
 const begin=await evaljs("({route:location.hash,open:typeof window.__dulukaStartMainMenuEditor,editing:document.documentElement.classList.contains('nv-live-layout-editing')})");
 console.log('BEGIN',JSON.stringify(begin)); if(!begin.editing&&begin.open==='function'){await evaljs("window.__dulukaStartMainMenuEditor();'opened'");await new Promise(r=>setTimeout(r,1300))}
 const expr=`(function(){
 function info(el){if(!el)return null;var s=getComputedStyle(el),r=el.getBoundingClientRect();return {tag:el.tagName,cls:String(el.className||''),rect:[r.left,r.top,r.width,r.height].map(x=>Math.round(x)),display:s.display,visibility:s.visibility,pointer:s.pointerEvents,position:s.position,z:s.zIndex,opacity:s.opacity,transform:s.transform}}
 function hit(x,y){var e=document.elementFromPoint(x,y);return e?{hit:info(e),ancestors:(function(){var a=[],n=e;while(n&&a.length<6){a.push((n.tagName||'')+' '+String(n.className||'').slice(0,60));n=n.parentElement}return a})()}:null}
 var bar=document.querySelector('nv-osc-top-bar'),el=bar&&bar.querySelector('.top-bar');
 var controls=el&&Array.prototype.slice.call(el.querySelectorAll('button,[ng-click],[role=button],a')).slice(0,18).map(function(c){var r=c.getBoundingClientRect();return {el:info(c),hit:hit(r.left+r.width/2,r.top+r.height/2)}});
 return {route:location.hash,htmlClasses:document.documentElement.className,topbar:info(bar),bar:info(el),parent:info(bar&&bar.parentElement),editor:info(document.querySelector('.nv-live-editor')),nativeRoot:info(document.querySelector('nv-main-menu')),controls:controls,hits:{logo:hit(35,35),center:hit(innerWidth/2,35),exit:hit(innerWidth-34,35),below:hit(innerWidth/2,84)}};
 })()`;
 const out=await evaljs(expr);console.log('AUDIT',JSON.stringify(out,null,2));ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
