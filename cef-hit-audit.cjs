'use strict';
const fs=require('fs');
(async function(){
 const tabs=await (await fetch('http://127.0.0.1:59099/json/list')).json();
 const tab=tabs.find(t=>t.type==='page'&&t.url.includes('59011'));
 if(!tab)throw Error('OSC tab is not available');
 const ws=new WebSocket(tab.webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
 let id=0;const jobs=new Map();
 ws.onmessage=e=>{const x=JSON.parse(e.data),fn=jobs.get(x.id);if(fn){jobs.delete(x.id);fn(x);}};
 const call=(method,params)=>new Promise((res,rej)=>{
  const n=++id,t=setTimeout(()=>{jobs.delete(n);rej(Error('Timeout '+method));},7000);
  jobs.set(n,v=>{clearTimeout(t);v.error?rej(Error(JSON.stringify(v.error))):res(v.result)});
  ws.send(JSON.stringify({id:n,method,params:params||{}}));
 });
 const expression=`(function(){
 function rect(el){if(!el)return null;var a=el.getBoundingClientRect();return [a.left,a.top,a.width,a.height].map(function(v){return Math.round(v*10)/10})}
 function brief(el){if(!el)return null;var st=getComputedStyle(el);return {tag:el.tagName.toLowerCase(),className:String(el.className&&el.className.baseVal||el.className||'').slice(0,120),name:el.getAttribute('data-nv-unified-key')||el.getAttribute('data-g-control')||el.getAttribute('data-nv-control'),rect:rect(el),pe:st.pointerEvents,z:st.zIndex,display:st.display,visibility:st.visibility,opacity:st.opacity,background:st.backgroundColor}}
 function ancestry(el){var out=[];while(el&&out.length<6){out.push(brief(el));el=el.parentElement}return out}
 function hit(x,y){if(x<0||x>=innerWidth||y<0||y>=innerHeight)return null;return ancestry(document.elementFromPoint(x,y))}
 var root=document.querySelector('nv-main-menu:not([data-customize-preview]) .main-menu-container');
 var editor=document.querySelector('.nv-live-editor');
 var v4=window.__dulukaUnifiedMainMenu,v5=window.__dulukaGroupLayoutA;
 var data={url:location.href,viewport:[innerWidth,innerHeight],time:Date.now(),htmlClasses:document.documentElement.className,
 flags:{v4:localStorage.getItem('DulukaMainMenuUnifiedV4Enabled'),v5:localStorage.getItem('DulukaMainMenuGroupLayoutAEnabled')},
 root:brief(root),editor:brief(editor),style:editor?{background:getComputedStyle(editor).backgroundImage,topPreview:getComputedStyle(editor).getPropertyValue('--nv-top-preview-height'),paddingTop:getComputedStyle(editor).paddingTop}:null,
 nativeTiles:[],editorButtons:[],spots:{}};
 Array.prototype.forEach.call(document.querySelectorAll('md-menu[data-nv-unified-key]'),function(el){
  var r=el.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
  data.nativeTiles.push({key:el.getAttribute('data-nv-unified-key'),element:brief(el),button:brief(el.querySelector('button.menu-button')),center:[Math.round(x),Math.round(y)],hit:hit(x,y)});
 });
 Array.prototype.forEach.call(document.querySelectorAll('.nv-group-layout-editor button[data-g-slot],.nv-group-layout-editor button[data-g-add],.nv-group-layout-editor button[data-g-undo]'),function(el){
  if(data.editorButtons.length>=12)return;
  var r=el.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
  data.editorButtons.push({key:el.getAttribute('data-g-slot')||el.getAttribute('data-g-add')||'undo',element:brief(el),hit:hit(x,y)});
 });
 [[1200,115],[1200,310],[1110,290],[1400,175],[170,165],[610,230],[980,225],[1280,260],[750,375]].forEach(function(p){data.spots[p.join(',')]=hit(p[0],p[1]);});
 return data;
 })()`;
 const r=await call('Runtime.evaluate',{expression,returnByValue:true});
 if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));
 const data=r.result.value;
 fs.writeFileSync(__dirname+'/cef-hit-audit.json',JSON.stringify(data,null,2));
 console.log('INFO',JSON.stringify({route:data.url,viewport:data.viewport,htmlClasses:data.htmlClasses,editor:data.editor,style:data.style,tiles:data.nativeTiles.length,buttons:data.editorButtons.length}));
 for(const t of data.nativeTiles){const h=t.hit&&t.hit[0];console.log('TILE',t.key,'rect',JSON.stringify(t.element.rect),'center',JSON.stringify(t.center),'topHit',h&&h.tag,h&&h.className,h&&h.pe,'button',JSON.stringify(t.button&&t.button.rect));}
 for(const t of data.editorButtons){const h=t.hit&&t.hit[0];console.log('EDITOR',t.key,'topHit',h&&h.tag,h&&h.className,'rect',JSON.stringify(t.element.rect));}
 for(const [k,a] of Object.entries(data.spots)){console.log('SPOT',k,JSON.stringify(a&&a.slice(0,3)));}
 ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
