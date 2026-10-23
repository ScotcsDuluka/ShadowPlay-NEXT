'use strict';
// Read-only NVIDIA OSC CEF Customize geometry audit.
// Run: node devtools/osc-live/audit-customize-v5.cjs
// Does not open/close Settings, click NVIDIA controls, or edit CEF styles.
(async()=>{
 const endpoint=process.env.NV_OSC_CDP_URL||'http://127.0.0.1:59099/json/list';
 const tabs=await(await fetch(endpoint)).json();
 const tab=tabs.find(x=>x.type==='page'&&/59011/.test(x.url||''));
 if(!tab)throw Error('NVIDIA OSC CEF page not available');
 const ws=new WebSocket(tab.webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
 const result=await new Promise((resolve,reject)=>{
   const id=1,t=setTimeout(()=>reject(Error('CEF read timeout')),6000);
   ws.onmessage=ev=>{const m=JSON.parse(ev.data);if(m.id!==id)return;clearTimeout(t);m.error?reject(Error(JSON.stringify(m.error))):resolve(m.result);};
   const expression=`(function(){
     function info(el){if(!el)return null;var r=el.getBoundingClientRect(),s=getComputedStyle(el);
       return {x:+r.left.toFixed(1),y:+r.top.toFixed(1),right:+r.right.toFixed(1),bottom:+r.bottom.toFixed(1),
         width:+r.width.toFixed(1),height:+r.height.toFixed(1),overflow:s.overflowY,
         scrollHeight:el.scrollHeight,clientHeight:el.clientHeight,
         position:s.position,top:s.top,marginTop:s.marginTop,transform:s.transform};}
     var shell=document.querySelector('.nv-live-editor'),bar=document.querySelector('nv-osc-top-bar .top-bar'),
     footer=shell&&shell.querySelector(':scope > footer'),bg=shell&&shell.querySelector(':scope > nv-osc-background-settings');
     var groups=Array.from(document.querySelectorAll('.nv-g-group-list'));
     var close=bar&&bar.querySelector('.top-bar-close-button'),cr=close&&close.getBoundingClientRect(),
       hit=cr&&document.elementFromPoint(cr.left+cr.width/2,cr.top+cr.height/2);
     var tiles=Array.from(document.querySelectorAll('md-menu[data-nv-unified-key]'));
     var clickable=tiles.filter(function(t){var r=t.getBoundingClientRect(),h=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);return h&&t.contains(h)}).length;
     var ancestors=[],cur=shell;
     while(cur&&ancestors.length<14){var st=getComputedStyle(cur),cr=cur.getBoundingClientRect();ancestors.push({tag:cur.tagName,
       cls:String(cur.className||'').slice(0,110),y:+cr.y.toFixed(1),height:+cr.height.toFixed(1),
       position:st.position,top:st.top,transform:st.transform,filter:st.filter,perspective:st.perspective,
       contain:st.contain,willChange:st.willChange});cur=cur.parentElement;}
     return {route:location.hash,viewport:[innerWidth,innerHeight],editing:document.documentElement.classList.contains('nv-live-layout-editing'),
       ancestors:ancestors,
       shell:info(shell),bar:info(bar),footer:info(footer),background:info(bg),groups:groups.map(info),
       nativeTiles:tiles.length,hitCenters:clickable,closeHit:!!(close&&hit&&close.contains(hit)),
       scrollTop:scrollY};
   })()`;
   ws.send(JSON.stringify({id,method:'Runtime.evaluate',params:{expression,returnByValue:true}}));
 });
 ws.close();
 if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));
 const r=result.result.value;
 console.log(JSON.stringify(r,null,2));
 if(!r.editing||!r.shell){console.error('Customize is not open; read-only audit cannot assess it.');process.exitCode=2;return;}
 const inset=40,tolerance=3;
 const topOK=Math.abs(r.shell.x-inset)<=tolerance&&Math.abs(r.shell.y-inset)<=tolerance;
 const rightOK=Math.abs(r.shell.right-(r.viewport[0]-inset))<=tolerance;
 const bottomOK=Math.abs(r.shell.bottom-(r.viewport[1]-inset))<=tolerance;
 const canScroll=r.shell.scrollHeight>r.shell.clientHeight;
 const footerOK=r.footer&&(r.footer.bottom<=r.shell.bottom-1||canScroll);
 const bgOK=r.background&&(r.background.bottom<=r.shell.bottom-1||canScroll);
 const passed=topOK&&rightOK&&bottomOK&&footerOK&&bgOK&&r.closeHit&&r.hitCenters===r.nativeTiles;
 console.log('CEF_CUSTOMIZE_AUDIT='+ (passed?'PASS':'FAIL'),JSON.stringify({topOK,rightOK,bottomOK,footerOK,bgOK,closeHit:r.closeHit,hitCenters:r.hitCenters,nativeTiles:r.nativeTiles}));
 if(!passed)process.exitCode=1;
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
