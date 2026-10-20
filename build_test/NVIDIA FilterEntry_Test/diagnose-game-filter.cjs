'use strict';
// Read-only diagnostics. Does not open editor, change presets, or restart processes.
const fs = require('node:fs');
const HOST = 'http://127.0.0.1:59011';
const CDP = 'http://127.0.0.1:59099/json/list';
const WATCHER = 'http://127.0.0.1:59012/v1/game-watcher/status';
const snapshotExpression = [
  '(function(){',
  'var result={hash:location.hash,readyState:document.readyState};',
  'try{',
  'var a=window.angular,i=a&&a.element(document).injector();',
  'if(!i){result.angular="injector unavailable";return result;}',
  'var s=i.get("$state"),c=i.get("nvCameraService"),exe=c.__dulukaModsExecutable||"";',
  'var choices={};',
  'try{choices=JSON.parse(localStorage.getItem("DulukaModsSelectionStorage")||"{}");}',
  'catch(e){result.selectionError=String(e);}',
  'result.route=s.current&&s.current.name;',
  'result.connected=!!c.__anselConnected;',
  'result.runtime=c.__dulukaModsRuntime||null;',
  'result.prepared=c.__dulukaModsPrepared||null;',
  'result.initialization=!!c.__dulukaNativeInitialization;',
  'result.supported=!!c.modsSupported;',
  'result.enabled=!!c.modsIsEnabled;',
  'result.uiRunning=!!c.UIRunning;',
  'result.executable=exe;',
  'result.savedChoice=exe?choices[exe]:null;',
  'result.lastSlot=c.lastSlot;',
  'result.activeSlot=c.modsActiveSlot?c.modsActiveSlot.id:null;',
  'result.slotCatalogSizes=Array.isArray(c.modsSlots)?c.modsSlots.map(function(s){',
  'return s&&Array.isArray(s.filters)?s.filters.length:0;}):[];',
  '}catch(e){result.exception=String(e);}return result;})()'
].join('');
async function fetchJson(url) {
  try {
    const response=await fetch(url,{signal:AbortSignal.timeout(2500)});
    const raw=await response.text();
    let body; try {body=JSON.parse(raw);}catch (_) {body=raw.slice(0,250);}
    return {status:response.status,body};
  } catch(error) {return {error:error.message};}
}
async function inspectCdp() {
  const listing=await fetchJson(CDP);
  if (!Array.isArray(listing.body)) return {targetError:listing.error||listing.body};
  const page=listing.body.find(p=>p.type==='page' && /localhost:59011/.test(p.url));
  if (!page) return {targetError:'No Overlay page target found'};
  const ws=new WebSocket(page.webSocketDebuggerUrl);
  return await new Promise(resolve=>{
    let done=false;
    const finish=value=>{if(done)return;done=true;clearTimeout(timer);ws.close();resolve(value);};
    const timer=setTimeout(()=>finish({error:'CDP read timed out'}),3500);
    ws.onopen=()=>ws.send(JSON.stringify({id:1,method:'Runtime.evaluate',params:{
      expression:snapshotExpression,returnByValue:true,awaitPromise:false
    }}));
    ws.onerror=()=>finish({error:'CDP socket failed'});
    ws.onmessage=event=>{
      try {
        const msg=JSON.parse(event.data);
        if(msg.id!==1)return;
        finish(msg.result && msg.result.result
          ? {url:page.url,state:msg.result.result.value}
          : {url:page.url,error:JSON.stringify(msg.error||msg.result)});
      }catch(error){finish({error:error.message});}
    };
  });
}
async function main() {
  const [api,settings,watcher,overlay]=await Promise.all([
    fetchJson(HOST+'/Duluka/v.1.0/GameFilter/State'),
    fetchJson(HOST+'/Duluka/v.1.0/GameFilter/Settings'),
    fetchJson(WATCHER),
    inspectCdp()
  ]);
  let pipeNames=[],pipeError=null;
  try {pipeNames=fs.readdirSync('\\\\.\\pipe\\').filter(name=>/^NVIDIAAnsel\.\d+\.\d+$/.test(name));}
  catch(error){pipeError=error.message;}
  const output={timestamp:new Date().toISOString(),gameFilter:api,settings,
    gameWatcher:watcher,anselPipes:pipeNames,pipeError,overlay};
  console.log(JSON.stringify(output,null,2));
  // Disconnected game is a valid diagnostic outcome, not a tool failure.
}
main().catch(error=>{console.error(error);process.exitCode=1;});
