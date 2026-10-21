'use strict';
// Live V4 test build served into the EXISTING authorized OSC CEF tab.
// Replaces only six static requests through Chrome DevTools Fetch; no
// second NodeAPI/injector instance and no changes to the production Build.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const root=__dirname,media=path.resolve(root,'../../Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc'),mode=process.argv.includes('--smoke')?'smoke':'live';
const stopFile=path.join(root,'STOP_PREVIEW'),statusFile=path.join(root,'preview-status.json');
const stateBackup=path.join(root,'preview-localstorage-backup.json'),reportPath=path.join(root,'preview-checks.json');
const screenshotPath=path.join(root,'preview-actual-cef.png');
const STOP_KEYS=['DulukaMainMenuUnifiedV4Enabled','DulukaMainMenuUnifiedV4'];
const allowed={
  '/index.html':'index.html',
  '/mods/main-menu-live.js':'mods/main-menu-live.js',
  '/mods/gpu-profiles.js':'mods/gpu-profiles.js',
  '/mods/main-menu-unified-model.js':'mods/main-menu-unified-model.js',
  '/mods/main-menu-unified-native.js':'mods/main-menu-unified-native.js',
  '/mods/main-menu-unified-native.css':'mods/main-menu-unified-native.css'
};
const capture={};
for(const [uri,rel] of Object.entries(allowed)){
 const file=path.join(media,rel);
 assert(fs.existsSync(file),rel+' not in staged Test Build');
 capture[uri]={file,mime:/\.css$/.test(rel)?'text/css':/\.html$/.test(rel)?'text/html':'text/javascript'};
}
let ws,seq=0,terminating=false,restored=false,enabled=false,done=false;
let previous,original,jobTimer,interceptionMode='none',pendingReload=false,hotVersion=0,lastChange=null;const pending=new Map(),seen={},exceptions=[];
const hashes={};function fingerprint(f){return crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex')};
for(const [uri,info] of Object.entries(capture))hashes[uri]=fingerprint(info.file);
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
function status(data){fs.writeFileSync(statusFile,JSON.stringify({...data,at:new Date().toISOString(),mode},null,2))}
function send(method,params={},time=10000){return new Promise((resolve,reject)=>{
 if(!ws||ws.readyState!==1)return reject(Error('CEF connection lost'));
 const id=++seq,timeout=setTimeout(()=>{pending.delete(id);reject(Error('Timeout: '+method))},time);
 pending.set(id,(v)=>{clearTimeout(timeout);v.error?reject(Error(JSON.stringify(v.error))):resolve(v.result||{})});
 ws.send(JSON.stringify({id,method,params}));
})}
async function js(expression,awaitPromise=false){
 const r=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise},12000);
 if(r.exceptionDetails)throw Error('CEF JS error '+JSON.stringify(r.exceptionDetails));
 return r.result?.value;
}
async function onPaused(v){
 const event=v.params||{},url=event.request?.url||'',u=new URL(url),p=u.pathname;
 try{
   const f=capture[p];
   if(f&&u.port==='59011'){
     seen[p]=(seen[p]||0)+1;
     await send('Fetch.fulfillRequest',{requestId:event.requestId,responseCode:200,
       responseHeaders:[{name:'Content-Type',value:f.mime+'; charset=utf-8'},{name:'Cache-Control',value:'no-store'}],
       body:fs.readFileSync(f.file).toString('base64')},12000);
   }else await send('Fetch.continueRequest',{requestId:event.requestId},12000);
 }catch(e){exceptions.push('Fetch: '+e.message);
   try{await send('Fetch.continueRequest',{requestId:event.requestId},2000)}catch(ignore){}
 }
}
async function onLegacyIntercept(v){
 const event=v.params||{},url=event.request?.url||'',key=new URL(url).pathname;
 try{
   const f=capture[key];
   if(f&&new URL(url).port==='59011'){
     const body=fs.readFileSync(f.file);
     const header=Buffer.from(['HTTP/1.1 200 OK','Content-Type: '+f.mime+'; charset=utf-8','Cache-Control: no-store','Content-Length: '+body.length,'',''].join(String.fromCharCode(13,10)),'utf8');
     seen[key]=(seen[key]||0)+1;
     await send('Network.continueInterceptedRequest',{interceptionId:event.interceptionId,
       rawResponse:Buffer.concat([header,body]).toString('base64')});
   }else await send('Network.continueInterceptedRequest',{interceptionId:event.interceptionId});
 }catch(e){
   exceptions.push('LegacyNetwork: '+e.message);
   try{await send('Network.continueInterceptedRequest',{interceptionId:event.interceptionId},2000)}catch(ignore){}
 }
}
async function open(){
 const tabs=await(await fetch('http://127.0.0.1:59099/json/list',{signal:AbortSignal.timeout(3500)})).json();
 const tab=tabs.find(t=>t.type==='page'&&String(t.url).includes(':59011/'));
 assert(tab,'No OSC CEF debugging tab on port 59099');
 ws=new WebSocket(tab.webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject});
 ws.onmessage=(e)=>{
   try{
     const m=JSON.parse(e.data);
     if(m.id&&pending.has(m.id)){pending.get(m.id)(m);pending.delete(m.id);return}
     if(m.method==='Fetch.requestPaused'){void onPaused(m);return}
     if(m.method==='Network.requestIntercepted'){void onLegacyIntercept(m);return}
     if(m.method==='Runtime.exceptionThrown')exceptions.push(JSON.stringify(m.params).slice(0,500));
   }catch(e){exceptions.push('WS: '+e.message)}
 };
 ws.onclose=()=>{if(!done)console.error('CEF_SOCKET_CLOSED')};
}
async function expectBoot(){
 for(let tries=0;tries<65;tries++){
   try{if(await js('!!window.__dulukaUnifiedMainMenu&&!!window.NvUnifiedLayoutV4'))return}catch(e){}
   await sleep(100);
 }
 throw Error('V4 did not bootstrap from staged static assets: '+JSON.stringify(seen));
}
async function samples(){
 return js(`(function(){
 var r=document.querySelector('nv-main-menu .main-menu-container'),a=window.__dulukaUnifiedMainMenu;
 var e=document.querySelector('.nv-live-editor'),b=r&&r.querySelector('md-menu-bar');
 var g=a&&r&&a.nativeGeometry(r),rects=[];
 if(g)g.cells.forEach(function(cell){
   var c=a.model.CONTROLS.filter(function(z){return z.key===cell.key})[0];
   var node=r.querySelector('md-menu.'+c.name),bb=node&&node.getBoundingClientRect();
   if(bb)rects.push({key:cell.key,x:bb.x,y:bb.y,w:bb.width,h:bb.height,native:!!node.querySelector('button.menu-button')});
 });
 var bar=b&&b.getBoundingClientRect();
 return {url:location.href,route:location.hash,phase:window.__dulukaOscMotion&&window.__dulukaOscMotion.phase,
 motionState:window.__dulukaOscMotion&&window.__dulukaOscMotion.getState&&window.__dulukaOscMotion.getState(),htmlClass:document.documentElement.className,
 v4:a&&a.enabled(),tiles:rects.length,allNative:rects.every(function(x){return x.native}),
 fits:rects.length>0&&rects.every(function(x){return x.x>=-1&&x.y>=-1&&x.x+x.w<=innerWidth+1&&x.y+x.h<=innerHeight+1}),
 grouped:a&&a.get().panels.center[0]&&a.get().panels.center[0].controls.length,
 bar:bar?{x:bar.x,y:bar.y,w:bar.width,h:bar.height}:null,
 root:(function(){var rr=r&&r.getBoundingClientRect();return rr&&{x:rr.x,w:rr.width,offsetWidth:r.offsetWidth};})(),
 railStyle:b&&b.getAttribute('style'),railTransform:b&&getComputedStyle(b).transform,
 railParent:(function(){if(!b||!b.parentElement)return null;var br=b.parentElement.getBoundingClientRect();return {x:br.x,w:br.width,offsetLeft:b.offsetLeft,parentTag:b.parentElement.tagName}})(),
 editor:!!e,panels:e&&e.querySelectorAll('.nv-unified-panel').length,
 iconCells:e&&e.querySelectorAll('.nv-unified-member-cell').length,
 faceInfo:(function(){
   var tile=r&&r.querySelector('md-menu.screenshot-tile'),face=tile&&tile.querySelector('.nv-flex-face'),icon=face&&face.querySelector('md-icon'),button=tile&&tile.querySelector('button.menu-button');
   function entry(node){if(!node)return null;var rect=node.getBoundingClientRect(),s=getComputedStyle(node);return {tag:node.tagName,cls:node.className,rect:{x:rect.x,y:rect.y,w:rect.width,h:rect.height},font:s.fontSize,transform:s.transform,display:s.display,opacity:s.opacity};}
   return {tile:entry(tile),button:entry(button),face:entry(face),icon:entry(icon),inline:tile&&tile.getAttribute('style'),scale:tile&&getComputedStyle(tile).getPropertyValue('--nv-motion-scale')};
 })(),
 bgSettings:!!(e&&e.querySelector('nv-osc-background-settings')),
 settingsDelete:!!(e&&e.querySelector('[data-v4-remove="Settings"]')),screen:{w:innerWidth,h:innerHeight}};
})()`);
}
async function captureScreen(){const p=await send('Page.captureScreenshot',{format:'png'},15000);if(!p.data)throw Error('Screenshot unavailable');fs.writeFileSync(screenshotPath,Buffer.from(p.data,'base64'))}
async function updateIndicator(label,allowApply){
 const code="(function(){var editor=document.querySelector('.nv-unified-editor'),badge=document.getElementById('nv-v4-live-status');"+
 "if(!editor){if(badge)badge.remove();return false;}"+
 "if(!badge){badge=document.createElement('div');badge.id='nv-v4-live-status';badge.style.cssText='display:flex;gap:10px;align-items:center;padding:6px 10px;margin:4px 0 9px;background:#223127;color:#d5edca;border-left:3px solid #76b900;font:12px Arial';editor.insertBefore(badge,editor.firstChild);}"+
 "badge.textContent="+JSON.stringify(label)+";"+
 "if("+Boolean(allowApply)+"){var btn=document.createElement('button');btn.type='button';btn.textContent='Apply JS';btn.style.cssText='background:#76b900;color:#152100;border:0;padding:5px 9px;cursor:pointer';btn.addEventListener('click',function(){window.__nvV4ApplyRequested=true;});badge.appendChild(btn);}return true;})()";
 try{return await js(code)}catch(e){exceptions.push('Indicator: '+e.message);return false}
}
async function watchUpdates(){
 var dirty=[];
 for(const [uri,v] of Object.entries(capture)){
   const sum=fingerprint(v.file);
   if(sum!==hashes[uri]){hashes[uri]=sum;dirty.push(uri);}
 }
 if(dirty.length){
   hotVersion++;lastChange={files:dirty,at:new Date().toISOString()};
   console.log('V4_UI_CHANGE_DETECTED',JSON.stringify(lastChange));
   if(dirty.length===1&&dirty[0].endsWith('.css')){
     const css=fs.readFileSync(capture[dirty[0]].file,'utf8');
     await js("(function(){var tag=document.getElementById('nv-v4-css-hot');if(!tag){tag=document.createElement('style');tag.id='nv-v4-css-hot';document.head.appendChild(tag);}tag.textContent="+JSON.stringify(css)+";return true;})()");
     await updateIndicator('✓ CSS updated live · v'+hotVersion,false);
     await captureScreen().catch(()=>{});
   }else{
     pendingReload=true;
     await updateIndicator('JS changed · Apply when ready · v'+hotVersion,true);
   }
   status({phase:'live',hotVersion,lastChange,pendingReload,assetOverrides:seen,screenshot:screenshotPath});
 }
 if(pendingReload){
   const accepted=await js("!!window.__nvV4ApplyRequested");
   if(accepted){
     const safe=await js("(function(){var i=window.angular&&angular.element(document.documentElement).injector();"+
       "var route=i&&i.get('$state').current.name;"+
       "var node=document.querySelector('[nv-main-menu-live-editor]'),s=node&&angular.element(node).scope();"+
       "while(s&&!s.mainMenu)s=s.$parent;"+
       "var t=s&&s.mainMenu&&s.mainMenu.tiles&&s.mainMenu.tiles.ManualRecord;"+
       "return {route:route,record:t&&t.currentState||'unknown'};})()");
     if(safe.record==='NotRecording'&&['main.main-menu','base'].includes(safe.route)){
       await js("window.__nvV4ApplyRequested=false;true");
       await send('Page.reload',{ignoreCache:true},15000);
       await expectBoot();pendingReload=false;
       console.log('V4_JS_UPDATE_APPLIED',hotVersion);
       status({phase:'armed',hotVersion,pendingReload:false,assetOverrides:seen});
     }else await updateIndicator('Update deferred: recording state '+safe.record,true);
   }
 }
}
async function restore(){
 if(restored)return;restored=true;
 console.log('RESTORE_STARTED');
 try{
   // Back up the user's temporary V4 edits separately before exiting.
   const latest=await js("(function(){return {flag:localStorage.getItem('DulukaMainMenuUnifiedV4Enabled'),layout:localStorage.getItem('DulukaMainMenuUnifiedV4')}})()");
   fs.writeFileSync(path.join(root,'preview-last-layout.json'),JSON.stringify(latest,null,2));
   if(previous)await js("(function(){var x="+JSON.stringify(previous)+
     ";for(var key in x){if(x[key]===null)localStorage.removeItem(key);else localStorage.setItem(key,x[key]);}return true;})()");
   if(original)await js("angular.element(document.documentElement).injector().get('$state').go("+JSON.stringify(original.route)+").then(function(){return true})",true).catch(()=>{});
   if(interceptionMode==='fetch')await send('Fetch.disable');
   else if(interceptionMode==='network')await send('Network.setRequestInterception',{patterns:[]});
   if(enabled)await send('Page.reload',{ignoreCache:true},14000);
   console.log('PREVIEW_STOPPED_V2_RESTORED');
   status({phase:'stopped',productionRestored:true,assetOverrides:seen});
 }catch(e){console.error('RESTORE_ERROR',e.stack||e);status({phase:'restore-error',error:e.message});process.exitCode=1}
 finally{done=true;if(ws)ws.close();}
}
async function shutdown(){if(terminating)return;terminating=true;clearInterval(jobTimer);await restore()}
process.on('SIGINT',()=>{void shutdown()});process.on('SIGTERM',()=>{void shutdown()});
(async()=>{
 fs.rmSync(stopFile,{force:true});status({phase:'starting'});
 await open();
 original=await js("(function(){var r=angular.element(document.documentElement).injector().get('$state');return {route:r.current.name,record:window.__nvRecordedTestState||null,v4Flag:localStorage.getItem('DulukaMainMenuUnifiedV4Enabled'),v4Data:localStorage.getItem('DulukaMainMenuUnifiedV4')}})()");
 if(['base','main.main-menu','main.preferences'].indexOf(original.route)<0)throw Error('OSC is in another feature ('+original.route+'); refusing preview');
 previous={DulukaMainMenuUnifiedV4Enabled:original.v4Flag,DulukaMainMenuUnifiedV4:original.v4Data};
 fs.writeFileSync(stateBackup,JSON.stringify({at:new Date().toISOString(),...original},null,2));
 // Limit interception to the exact six static resources.
 const patterns=Object.keys(allowed).map(p=>({urlPattern:'*://localhost:59011'+p+'*',requestStage:'Request'}));
 try{await send('Fetch.enable',{patterns});interceptionMode='fetch';}
 catch(e){
   console.log('FETCH_UNAVAILABLE_TRY_LEGACY_NETWORK',e.message);
   await send('Network.enable');
   await send('Network.setRequestInterception',{patterns:patterns.map(x=>({urlPattern:x.urlPattern,interceptionStage:'Request'}))});
   interceptionMode='network';
 }
 enabled=true;
 await send('Page.reload',{ignoreCache:true},14000);
 await expectBoot();
 await js("(function(){__dulukaUnifiedMainMenu.setEnabled(true);return true;})()");
 // A real OSC hotkey must open the native overlay. Changing $state alone
 // creates a visible route with OSC Motion still physically CLOSED.
 if(mode==='live'||mode==='smoke'){
   const demonstration=await js(`(function(){
     var a=__dulukaUnifiedMainMenu,M=a.model,s=a.get();
     // Never replace the user's existing layout during a live updater session.
     // A disposable sample 3x3 arrangement is permitted in --smoke only.
     if(${mode==='smoke'}){
       s=M.toBlock(s,'Settings','center',0,'big');
       M.CONTROLS.filter(function(c){return c.key!=='Settings'}).slice(0,8).forEach(function(c){s=M.join(s,c.key,'center',0)});
       a.set(s);
     }
     return {total:M.PANELS.reduce(function(n,p){return n+s.panels[p].reduce(function(k,b){return k+b.controls.length;},0);},0),
       center:s.panels.center[0]&&s.panels.center[0].controls.length,settings:M.locate(s,'Settings').panel};
   })()`);
   if(mode==='smoke'){assert.equal(demonstration.total,12);assert.equal(demonstration.center,9);}
   console.log('V4_CEF_BOOTSTRAP_PASS',JSON.stringify({assetOverrides:seen,demonstration}));
   if(mode==='smoke'){
     status({phase:'smoke-pass',assetOverrides:seen,demonstration,note:'Native opening must be tested with the OSC hotkey'});
     await shutdown();return;
   }
   status({phase:'armed',assetOverrides:seen,demonstration,
     instruction:'Press Alt+Z to open OSC normally. Main-menu Customize uses V4 during this preview.',
     stop:'Create STOP_PREVIEW in the Test Build folder'});
   console.log('V4_TEST_BUILD_ARMED_PRESS_ALT_Z');
   let pollBusy=false,captured=false;
   jobTimer=setInterval(async()=>{
     if(terminating||pollBusy)return;
     if(fs.existsSync(stopFile)||!ws||ws.readyState!==1){await shutdown();return}
     pollBusy=true;
     try{
       await watchUpdates();
       const info=await js(`(function(){
          var m=window.__dulukaOscMotion,st=m&&m.getState&&m.getState(),s=angular.element(document.documentElement).injector().get('$state');
          return {route:s.current.name,motionPhase:m&&m.phase,motionTarget:st&&st.target,
            editor:!!document.querySelector('.nv-live-editor')};
       })()`);
       if(!captured&&info.motionTarget==='open'&&info.motionPhase==='idle'&&info.route==='main.main-menu'){
         if(!info.editor){
           await js("window.__dulukaStartMainMenuEditor();true");
           await sleep(300);
         }
         const report=await samples();
         if(report.tiles===12&&report.allNative&&report.fits&&report.faceInfo&&report.faceInfo.icon&&report.faceInfo.icon.rect.w>=25){
           await captureScreen();
           fs.writeFileSync(reportPath,JSON.stringify({report,assets:seen,exceptions},null,2));
           status({phase:'live',report,assetOverrides:seen,screenshot:screenshotPath,
             stop:'Create STOP_PREVIEW file in this folder'});
           captured=true;
           console.log('V4_REAL_OVERLAY_OPEN_PASS',JSON.stringify({tiles:report.tiles,fit:report.fits,iconWidth:report.faceInfo.icon.rect.w}));
         }else status({phase:'armed',nativeCheckPending:report,assetOverrides:seen,
             instruction:'Open real OSC with Alt+Z and wait for menu animation to finish'});
       }
     }catch(e){exceptions.push('Watch: '+e.message);}
     finally{pollBusy=false}
   },1200);
   return;
 }
 await js("(function(){angular.element(document.documentElement).injector().get('oscDisplayService').openOSC();return true;})()");
 await sleep(180);
 await js("angular.element(document.documentElement).injector().get('$state').go('main.main-menu').then(function(){return true})",true);
 await sleep(1050);
 // A demonstrative layout: 9 Controls in one Center Big block, 3 in Right.
 await js(`(function(){
   var a=__dulukaUnifiedMainMenu,M=a.model,s=a.get();
   s=M.toBlock(s,'Settings','center',0,'big');
   M.CONTROLS.filter(function(c){return c.key!=='Settings'}).slice(0,8).forEach(function(c){s=M.join(s,c.key,'center',0)});
   a.set(s);return true;
 })()`);
 await sleep(200);
 await js("window.__dulukaStartMainMenuEditor();true");
 await sleep(560);
 const report=await samples();
 fs.writeFileSync(reportPath,JSON.stringify({report,assets:seen,exceptions},null,2));
 console.log('LIVE_PREVIEW_CHECK',JSON.stringify(report));
 if(report.tiles!==12||!report.allNative||!report.fits||report.grouped!==9||report.panels!==3||report.settingsDelete||!report.faceInfo||!report.faceInfo.icon||report.faceInfo.icon.rect.w<25)
   throw Error('Native preview validation failed: '+JSON.stringify(report));
 await captureScreen();
 status({phase:'live',report,assetOverrides:seen,screenshot:screenshotPath,stop:'Create STOP_PREVIEW file in this folder'});
 console.log('LIVE_PREVIEW_READY',screenshotPath);
 if(mode==='smoke'){await shutdown();return}
 jobTimer=setInterval(async()=>{
   if(terminating)return;
   if(fs.existsSync(stopFile)){await shutdown();return}
   if(ws.readyState!==1){await shutdown();return}
 },1000);
})().catch(async e=>{console.error('PREVIEW_FAILED',e.stack||e);status({phase:'error',error:String(e),assetOverrides:seen,exceptions});process.exitCode=1;await shutdown()});
