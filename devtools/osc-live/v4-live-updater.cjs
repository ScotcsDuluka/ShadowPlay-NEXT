'use strict';
// V4 live CEF updater. Reconnects across NVIDIA CEF tab/process restarts.
// Does not alter the native injector, and never claims successful recovery
// without reading back the original localStorage values.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=__dirname,media=path.resolve(root,'../../Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc');
const smoke=process.argv.includes('--smoke'),testDrop=process.argv.includes('--test-drop');
const statusPath=path.join(root,'preview-status.json'),stopPath=path.join(root,'STOP_PREVIEW');
const backupPath=path.join(root,'preview-localstorage-backup.json'),lastPath=path.join(root,'preview-last-layout.json');
const reportPath=path.join(root,'preview-checks.json'),screenPath=path.join(root,'preview-actual-cef.png');
const resources={
 '/index.html':'index.html',
 '/mods/main-menu-live.js':'mods/main-menu-live.js',
 '/mods/gpu-profiles.js':'mods/gpu-profiles.js',
 '/mods/main-menu-unified-model.js':'mods/main-menu-unified-model.js',
 '/mods/main-menu-unified-native.js':'mods/main-menu-unified-native.js',
 '/mods/main-menu-unified-native.css':'mods/main-menu-unified-native.css'
};
const assets=Object.fromEntries(Object.entries(resources).map(([url,rel])=>{
 const file=path.join(media,rel);
 if(!fs.existsSync(file))throw Error('Missing V4 source asset '+file);
 return [url,{file,mime:/\.css$/.test(rel)?'text/css':/\.html$/.test(rel)?'text/html':'text/javascript'}];
}));
function sha(file){return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')}
let hashes=Object.fromEntries(Object.entries(assets).map(([key,x])=>[key,sha(x.file)]));
let session=null,initial=null,stopRequested=false,done=false,connectedCount=0,reconnectCount=0,hotVersion=0,pendingApply=false,changed=null,lastCapture=false,dropPerformed=false;
let uiPhase='starting',lastError=null,watchBusy=false;
const requests={},errors=[],sleep=ms=>new Promise(r=>setTimeout(r,ms));
function state(extra={}){
 fs.writeFileSync(statusPath,JSON.stringify({phase:uiPhase,mode:smoke?'smoke':'live',
 connected:!!session?.alive,reconnectCount,connectedCount,hotVersion,pendingApply,
 lastChange:changed,stopRequested,requests,errors:errors.slice(-8),error:lastError,
 updatedAt:new Date().toISOString(),...extra},null,2));
}
function log(...a){console.log(new Date().toISOString(),...a)}
function fail(e){lastError=String(e?.message||e);errors.push(lastError);log('WARNING',lastError)}
class Connection {
 constructor(url){
   this.url=url;this.seq=0;this.pending=new Map();this.alive=false;this.intercept=null;this.ws=null;
 }
 async start(){
   const sock=new WebSocket(this.url);this.ws=sock;
   await new Promise((resolve,reject)=>{
     const timer=setTimeout(()=>reject(Error('CEF WebSocket opening timed out')),4200);
     sock.onopen=()=>{clearTimeout(timer);this.alive=true;resolve()};
     sock.onerror=()=>{clearTimeout(timer);reject(Error('CEF socket unavailable'))};
   });
   sock.onmessage=e=>{
     let m;try{m=JSON.parse(e.data)}catch{return}
     if(m.id&&this.pending.has(m.id)){
       const p=this.pending.get(m.id);this.pending.delete(m.id);clearTimeout(p.timer);
       m.error?p.reject(Error(JSON.stringify(m.error))):p.resolve(m.result||{});return;
     }
     if(m.method==='Network.requestIntercepted'){this.intercepted(m.params);return}
     if(m.method==='Fetch.requestPaused'){this.paused(m.params);return}
   };
   sock.onclose=()=>{
     this.alive=false;
     for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(Error('CEF socket disconnected'))}
     this.pending.clear();
   };
   return this;
 }
 send(method,params={},ms=9000){
   if(!this.alive||this.ws.readyState!==WebSocket.OPEN)return Promise.reject(Error('CEF socket disconnected'));
   return new Promise((resolve,reject)=>{
     const id=++this.seq;
     const timer=setTimeout(()=>{this.pending.delete(id);reject(Error('CEF timeout '+method))},ms);
     this.pending.set(id,{resolve,reject,timer});
     try{this.ws.send(JSON.stringify({id,method,params}))}
     catch(e){clearTimeout(timer);this.pending.delete(id);reject(e)}
   });
 }
 async js(expression,awaitPromise=false){
   const response=await this.send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise},12000);
   if(response.exceptionDetails)throw Error('CEF JavaScript '+JSON.stringify(response.exceptionDetails));
   return response.result?.value;
 }
 async interceptStart(){
   const patterns=Object.keys(assets).map(u=>({urlPattern:'*://localhost:59011'+u+'*',requestStage:'Request'}));
   try{await this.send('Fetch.enable',{patterns});this.intercept='fetch'}
   catch(e){
     await this.send('Network.enable');
     await this.send('Network.setRequestInterception',{patterns:patterns.map(p=>({urlPattern:p.urlPattern,interceptionStage:'Request'}))});
     this.intercept='legacy';
   }
 }
 async intercepted(e){
   try{
     const uri=new URL(e.request.url),asset=assets[uri.pathname];
     if(!asset||uri.port!=='59011'){await this.send('Network.continueInterceptedRequest',{interceptionId:e.interceptionId});return}
     const content=fs.readFileSync(asset.file);
     const header=['HTTP/1.1 200 OK','Content-Type: '+asset.mime+'; charset=utf-8','Cache-Control: no-store','Content-Length: '+content.length,'',''].join('\r\n');
     await this.send('Network.continueInterceptedRequest',{interceptionId:e.interceptionId,rawResponse:Buffer.concat([Buffer.from(header),content]).toString('base64')});
     requests[uri.pathname]=(requests[uri.pathname]||0)+1;
   }catch(error){fail(error)}
 }
 async paused(e){
   try{
     const u=new URL(e.request.url),a=assets[u.pathname];
     if(a&&u.port==='59011'){
       await this.send('Fetch.fulfillRequest',{requestId:e.requestId,responseCode:200,
         responseHeaders:[{name:'Content-Type',value:a.mime+'; charset=utf-8'},{name:'Cache-Control',value:'no-store'}],
         body:fs.readFileSync(a.file).toString('base64')});
       requests[u.pathname]=(requests[u.pathname]||0)+1;
     }else await this.send('Fetch.continueRequest',{requestId:e.requestId});
   }catch(error){fail(error)}
 }
 async interceptStop(){
   if(!this.alive)return;
   if(this.intercept==='fetch')await this.send('Fetch.disable');
   if(this.intercept==='legacy')await this.send('Network.setRequestInterception',{patterns:[]});
   this.intercept=null;
 }
 close(){if(this.ws){try{this.ws.close()}catch{}}this.alive=false}
}
const getBackup=()=>({flag:initial?.flag??null,data:initial?.data??null});
const snapshot='(function(){var r=angular.element(document.documentElement).injector().get("$state");return {route:r.current.name,flag:localStorage.getItem("DulukaMainMenuUnifiedV4Enabled"),data:localStorage.getItem("DulukaMainMenuUnifiedV4")};})()';
async function findCEF(){
 const response=await fetch('http://127.0.0.1:59099/json/list',{signal:AbortSignal.timeout(2800)});
 const list=await response.json();
 const tab=list.find(x=>x.type==='page'&&String(x.url).includes(':59011/'));
 if(!tab?.webSocketDebuggerUrl)throw Error('NVIDIA OSC CEF not ready');
 return tab.webSocketDebuggerUrl;
}
async function waitAngular(c){
 for(let tries=0;tries<45;tries++){
   if(!c.alive)throw Error('CEF disconnected before Angular startup');
   try{if(await c.js('typeof angular!=="undefined"&&!!angular.element(document.documentElement).injector()'))return true}catch{}
   await sleep(250);
 }
 throw Error('NVIDIA OSC Angular application not ready');
}
async function waitBoot(c){
 for(let tries=0;tries<30;tries++){
   if(!c.alive)throw Error('CEF closed while booting');
   try{if(await c.js('typeof angular!=="undefined"&&!!angular.element(document.documentElement).injector()&&!!window.NvUnifiedLayoutV4&&!!window.__dulukaUnifiedMainMenu'))return}catch{}
   await sleep(180);
 }
 throw Error('V4 bootstrap incomplete');
}
async function sessionInit(){
 const url=await findCEF(),c=await new Connection(url).start();
 try{
   // Read initial user preferences once. A reconnect must never take a new backup
   // of the temporary V4 flag and mistake it for the user's original value.
   await waitAngular(c);
   const before=await c.js(snapshot);
   if(!initial){
     if(!['base','main.main-menu','main.preferences'].includes(before.route))throw Error('OSC busy in '+before.route);
     initial={flag:before.flag,data:before.data,route:before.route};
     fs.writeFileSync(backupPath,JSON.stringify({...initial,time:new Date().toISOString()},null,2));
   }
   await c.interceptStart();
   await c.send('Page.reload',{ignoreCache:true},15000);
   await waitBoot(c);
   await c.js('window.__dulukaUnifiedMainMenu.setEnabled(true)');
   const verified=await c.js('window.__dulukaUnifiedMainMenu.enabled()');
   if(!verified)throw Error('V4 did not enable');
   if(session&&session!==c)session.close();
   session=c;connectedCount++;reconnectCount=Math.max(0,connectedCount-1);
   uiPhase='armed';state();log('CEF_CONNECTED',connectedCount,'reconnected',reconnectCount);
   return c;
 }catch(e){c.close();throw e}
}
async function indicate(c,label,withApply){
 const expr='(function(){var e=document.querySelector(".nv-unified-editor"),bar=document.getElementById("nv-v4-updater");'+
 'if(!e)return false;if(!bar){bar=document.createElement("div");bar.id="nv-v4-updater";'+
 'bar.style.cssText="padding:8px;border-left:3px solid #76b900;background:#222;color:#bcedad;font:12px Arial;display:flex;align-items:center;gap:10px";e.insertBefore(bar,e.firstChild)}'+
 'bar.textContent='+JSON.stringify(label)+';'+
 (withApply?'var b=document.createElement("button");b.textContent="Apply JS";b.setAttribute("type","button");b.style.cssText="border:1px solid #76b900;color:#fff;background:#36422c;padding:5px 10px";b.onclick=function(){window.__nvV4ApplyRequested=true};bar.appendChild(b);':'')+
 'return true})()';
 return c.js(expr);
}
async function nativeCheck(c){
 return c.js(`(function(){
 var a=window.__dulukaUnifiedMainMenu,e=document.querySelector(".nv-unified-editor"),
 r=document.querySelector("nv-main-menu .main-menu-container"),m=window.__dulukaOscMotion;
 var st=m&&m.getState&&m.getState(),count=0;
 if(!a||!r||!a.enabled())return {ready:false,reason:"No V4 main menu"};
 var geometry=a.nativeGeometry(r);
 if(!geometry)return {ready:false,reason:"Motion not idle"};
 var good=geometry.cells.every(function(cell){
   var meta=a.model.CONTROLS.filter(function(z){return z.key===cell.key})[0];
   var el=r.querySelector("md-menu."+meta.name);
   if(!el||!el.querySelector("button.menu-button"))return false;
   var box=el.getBoundingClientRect();count++;
   return box.left>=-1&&box.top>=-1&&box.right<=innerWidth+1&&box.bottom<=innerHeight+1;
 });
 return {ready:true,fit:good,tiles:count,panels:e&&e.querySelectorAll(".nv-unified-panel").length,
   target:st&&st.target,phase:m&&m.phase,route:location.hash,settingsRemove:!!(e&&e.querySelector("[data-v4-remove=Settings]"))};
})()`);
}
async function updateTick(c){
 const dirty=[];
 for(const [name,a] of Object.entries(assets)){
   const next=sha(a.file);
   if(next!==hashes[name]){hashes[name]=next;dirty.push(name)}
 }
 if(dirty.length){
   changed={at:new Date().toISOString(),files:dirty};hotVersion++;
   log('UPDATED',JSON.stringify(changed));
   if(dirty.length===1&&dirty[0].endsWith('.css')){
     const css=fs.readFileSync(assets[dirty[0]].file,'utf8');
     await c.js('(function(){var s=document.getElementById("nv-v4-hot-css");if(!s){s=document.createElement("style");s.id="nv-v4-hot-css";document.head.appendChild(s)}s.textContent='+JSON.stringify(css)+';return true})()');
     await indicate(c,'CSS updated live · v'+hotVersion,false).catch(()=>{});
   }else pendingApply=true;
   state();
 }
 if(!pendingApply)return;
 await indicate(c,'New JS/HTML · Apply when safe · v'+hotVersion,true).catch(()=>{});
 const accepted=await c.js('!!window.__nvV4ApplyRequested');
 if(!accepted)return;
 const safety=await c.js(`(function(){
   var route=angular.element(document.documentElement).injector().get("$state").current.name;
   var e=document.querySelector("[nv-main-menu-live-editor]"),s=e&&angular.element(e).scope();
   while(s&&!s.mainMenu)s=s.$parent;
   var t=s&&s.mainMenu&&s.mainMenu.tiles&&s.mainMenu.tiles.ManualRecord;
   return {route:route,record:t&&t.currentState||"unknown"};
 })()`);
 if(safety.record!=='NotRecording'||!['base','main.main-menu'].includes(safety.route)){
   await indicate(c,'Reload blocked: '+safety.record+' · '+safety.route,true).catch(()=>{});
   return;
 }
 await c.js('window.__nvV4ApplyRequested=false');
 await c.send('Page.reload',{ignoreCache:true},15000);
 await waitBoot(c);
 await c.js('window.__dulukaUnifiedMainMenu.setEnabled(true)');
 pendingApply=false;uiPhase='armed';state();log('JS_APPLIED',hotVersion);
}
async function restore(c){
 uiPhase='restoring';state();log('RESTORING');
 const latest=await c.js(snapshot);
 fs.writeFileSync(lastPath,JSON.stringify({flag:latest.flag,layout:latest.data,time:new Date().toISOString()},null,2));
 const orig=getBackup();
 const restoreScript='(function(){var b='+JSON.stringify(orig)+';'+
 'if(b.flag===null)localStorage.removeItem("DulukaMainMenuUnifiedV4Enabled");else localStorage.setItem("DulukaMainMenuUnifiedV4Enabled",b.flag);'+
 'if(b.data===null)localStorage.removeItem("DulukaMainMenuUnifiedV4");else localStorage.setItem("DulukaMainMenuUnifiedV4",b.data);'+
 'return {flag:localStorage.getItem("DulukaMainMenuUnifiedV4Enabled"),data:localStorage.getItem("DulukaMainMenuUnifiedV4")}})()';
 const result=await c.js(restoreScript);
 if(result.flag!==orig.flag||result.data!==orig.data)throw Error('V4 keys failed verification');
 await c.interceptStop();
 await c.send('Page.reload',{ignoreCache:true},15000);
 // Success is grounded in a read-back after original asset reload.
 await sleep(220);
 await waitAngular(c);
 const confirm=await c.js('(function(){return {flag:localStorage.getItem("DulukaMainMenuUnifiedV4Enabled"),data:localStorage.getItem("DulukaMainMenuUnifiedV4"),v4:!!window.__dulukaUnifiedMainMenu}})()');
 if(confirm.flag!==orig.flag||confirm.data!==orig.data)throw Error('V4 preference restore not durable');
 done=true;uiPhase='stopped';state({productionRestored:true});
 log('RESTORED',JSON.stringify({flag:confirm.flag,originalAssets:!confirm.v4}));c.close();
}
function stop(){stopRequested=true;uiPhase='restore-pending';state();log('STOP_REQUESTED')}
process.on('SIGINT',stop);process.on('SIGTERM',stop);
async function tick(){
 if(fs.existsSync(stopPath))stopRequested=true;
 if(stopRequested){
   if(!session?.alive){uiPhase='restore-pending';state();return}
   try{await restore(session)}catch(e){fail(e);uiPhase='restore-pending';state()}
   return;
 }
 if(!session?.alive){
   uiPhase='reconnecting';state();
   try{await sessionInit()}catch(e){fail(e);state()}
   return;
 }
 try{
   await updateTick(session);
   const info=await session.js(`(function(){var m=window.__dulukaOscMotion,s=angular.element(document.documentElement).injector().get("$state");return {phase:m&&m.phase,target:m&&m.getState&&m.getState().target,route:s.current.name}})()`);
   if(info.target==='open'&&info.phase==='idle'&&info.route==='main.main-menu'){
     // Customize must be a USER action. A live updater may observe and
     // refresh the editor, but must never open it on behalf of the user.
     const editing=await session.js('document.documentElement.classList.contains("nv-live-layout-editing")');
     if(!editing){uiPhase='armed';state({note:'Waiting for manual Customize action'});return}
     const e=await session.js('!!document.querySelector(".nv-live-editor")');
     if(!e){uiPhase='armed';state({note:'Customize is opening'});return}
     const a=await nativeCheck(session);
     if(a.ready&&a.fit&&a.panels===3&&!a.settingsRemove){
       uiPhase='live';
       fs.writeFileSync(reportPath,JSON.stringify(a,null,2));
       if(!lastCapture){const img=await session.send('Page.captureScreenshot',{format:'png'},15000);
         if(img.data)fs.writeFileSync(screenPath,Buffer.from(img.data,'base64'));lastCapture=true}
       state({nativeCheck:a,screenshot:lastCapture?screenPath:null});
     }else{uiPhase='armed';state({nativeCheck:a})}
   }else{uiPhase='armed';state()}
 }catch(e){fail(e);session?.close();session=null;uiPhase='reconnecting';state()}
}
async function main(){
 fs.rmSync(stopPath,{force:true});
 state();
 // --smoke exercises load, reconnect and recovery, without needing Alt+Z.
 for(let iteration=0;!done;iteration++){
   if(!watchBusy){watchBusy=true;try{await tick()}finally{watchBusy=false}}
   if(smoke&&connectedCount===1&&!testDrop){
     const c=session;
     await c.js('window.__dulukaUnifiedMainMenu.enabled()');stopRequested=true;
   }
   if(testDrop&&!dropPerformed&&connectedCount===1&&session?.alive){
     dropPerformed=true;log('SIMULATED_CEF_SOCKET_DROP');
     session.close();session=null;
   }
   if(testDrop&&connectedCount>=2&&!stopRequested){log('RECONNECT_TEST_OK',connectedCount);stopRequested=true}
   await sleep(800);
 }
}
main().catch(e=>{fail(e);uiPhase='error';state();process.exitCode=1});
