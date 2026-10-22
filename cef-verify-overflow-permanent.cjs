'use strict';
const fs=require('fs'),path=require('path');
(async()=>{
 const tabs=await(await fetch('http://127.0.0.1:59099/json/list')).json(),tab=tabs.find(t=>t.type==='page'&&t.url.includes('59011'));if(!tab)throw Error('OSC not available');
 const ws=new WebSocket(tab.webSocketDebuggerUrl);await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j});let id=0,callbacks=new Map();
 ws.onmessage=e=>{const m=JSON.parse(e.data),f=callbacks.get(m.id);if(f){callbacks.delete(m.id);f(m)}};
 const call=(method,params={})=>new Promise((r,j)=>{const n=++id,t=setTimeout(()=>{callbacks.delete(n);j(Error('Timeout '+method))},8500);callbacks.set(n,m=>{clearTimeout(t);m.error?j(Error(JSON.stringify(m.error))):r(m.result)});ws.send(JSON.stringify({id:n,method,params}))});
 async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value}
 const before=await evaluate("({route:location.hash,open:typeof window.__dulukaStartMainMenuEditor,editing:document.documentElement.classList.contains('nv-live-layout-editing')})");
 console.log('BEFORE',JSON.stringify(before));
 if(!before.editing&&before.open==='function'){await evaluate("window.__dulukaStartMainMenuEditor();'started'");await new Promise(r=>setTimeout(r,1300))}
 const link=await evaluate(`(function(){var l=Array.prototype.filter.call(document.querySelectorAll('link[rel=stylesheet]'),function(x){return (x.href||'').indexOf('main-menu-group-editor.css')>=0})[0];if(!l)return {found:false};l.href='/mods/main-menu-group-editor.css?v=20261023-v5-overflow';return{found:true,href:l.href}})()`);
 console.log('LINK',JSON.stringify(link));if(!link.found)throw Error('Group editor stylesheet link missing');
 await new Promise(r=>setTimeout(r,1000));
 const state=await evaluate(`(function(){
 var sheets=Array.prototype.filter.call(document.styleSheets,function(s){return (s.href||'').indexOf('main-menu-group-editor.css')>=0});
 var updated=sheets.some(function(s){try{return Array.prototype.some.call(s.cssRules,function(r){return r.selectorText&&r.selectorText.indexOf('nv-live-editor.nv-group-layout-on .nv-g-panels')>=0&&r.style.gridTemplateRows==='minmax(0px, 1fr)'})}catch(e){return false}});
 if(!updated){updated=sheets.some(function(s){try{return Array.prototype.some.call(s.cssRules,function(r){return r.cssText.indexOf('grid-template-rows: minmax(0, 1fr)')>=0})}catch(e){return false}})}
 if(updated){var temp=document.getElementById('nv-v5-overflow-test');if(temp)temp.remove()}
 var host=document.querySelector('.nv-live-editor'),foot=host&&host.querySelector(':scope > footer'),bg=host&&host.querySelector('nv-osc-background-settings'),groups=host&&host.querySelector('.nv-g-group-list'),panels=host&&host.querySelector('.nv-g-panels');
 function pos(e){if(!e)return null;var r=e.getBoundingClientRect();return {y:Math.round(r.y),bottom:Math.round(r.bottom),height:Math.round(r.height),scroll:e.scrollHeight,client:e.clientHeight,overflow:getComputedStyle(e).overflowY}}
 var rr=host&&host.getBoundingClientRect(),bb=bg&&bg.getBoundingClientRect(),ff=foot&&foot.getBoundingClientRect();
 return {route:location.hash,updated,styleLink:sheets.map(function(x){return x.href}),temp:!!document.getElementById('nv-v5-overflow-test'),viewport:[innerWidth,innerHeight],
 host:pos(host),editor:pos(document.querySelector('.nv-group-layout-editor')),panels:pos(panels),group:pos(groups),footer:pos(foot),background:pos(bg),
 footerInside:!!(ff&&rr&&ff.bottom<=rr.bottom-20),backgroundInside:!!(bb&&rr&&bb.bottom<=rr.bottom-30),
 groupCanScroll:!!(groups&&groups.scrollHeight>groups.clientHeight),bgCanScroll:!!(bg&&bg.scrollHeight>bg.clientHeight),
 horizontalOverflow:host?host.scrollWidth-host.clientWidth:null};
 })()`);
 console.log('VERIFIED',JSON.stringify(state));
 if(!state.updated||state.temp||!state.footerInside||!state.backgroundInside||!state.groupCanScroll||!state.bgCanScroll)throw Error('Overflow fix validation failed');
 const image=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});
 const dest='C:/My Project/NVIDIA-Shadowplay/build_test/NVIDIA OSC Customize Overflow Fix 20261023/live-after-overflow-fix.png';
 fs.writeFileSync(dest,Buffer.from(image.data,'base64'));console.log('SCREENSHOT',dest);
 ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
