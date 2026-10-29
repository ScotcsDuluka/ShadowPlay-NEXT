'use strict';
// Read-only served-asset verification; optional safe CDP page reload on #/base.
// No process restarts, no configuration changes or game manipulation.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const project = path.resolve(__dirname,'..');
const root = path.resolve(project,'..','..','..');
const installed = path.join(root,'build','NVIDIA ShadowPlay','Overlay On-Screen Control','NVIDIA NodeAPI','osc');
const hash = b=>crypto.createHash('sha256').update(b).digest('hex');
const reload = process.argv.includes('--reload-safe');
const localeFiles = fs.readdirSync(path.join(installed,'l10n'))
  .filter(n=>/^[a-z]{2}-[A-Z]{2,3}\.json$/.test(n)).sort();
const files = ['index.html','Module/Base/Localization/launcher-language-sync.js']
  .concat(localeFiles.map(n=>'l10n/'+n));
async function main(){
  for(const rel of files){
    const result = await fetch('http://127.0.0.1:59011/'+rel, {cache:'no-store'});
    if(result.status!==200)throw Error(rel+' HTTP '+result.status);
    const received = Buffer.from(await result.arrayBuffer());
    const actual = hash(received);
    const expected = hash(fs.readFileSync(path.join(installed,rel)));
    if(actual!==expected)throw Error('HTTP/Build hash mismatch '+rel+' '+actual+' '+expected);
    console.log('SERVED_OK '+rel+' sha256='+actual.slice(0,16));
  }
  let targets;
  try{
    const response = await fetch('http://127.0.0.1:59099/json/list');
    targets = await response.json();
  }catch(e){console.log('CEF_DEBUG_UNAVAILABLE '+e.message);return;}
  const page = targets.find(x=>x.type==='page' && /:59011/.test(x.url));
  if(!page){console.log('CEF_PAGE_UNAVAILABLE');return;}
  console.log('CEF_TARGET='+page.url);
  if(!page.webSocketDebuggerUrl){console.log('CEF_DEBUG_WEBSOCKET_UNAVAILABLE');return;}
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((ok, bad)=>{ws.onopen=ok;ws.onerror=bad;});
  let id=0;const pending=new Map();
  ws.onmessage=e=>{
    const m=JSON.parse(e.data),p=pending.get(m.id);
    if(!p)return; pending.delete(m.id);
    if(m.error)p.reject(Error(m.error.message));else p.resolve(m.result);
  };
  const send=(method,params={})=>new Promise((resolve,reject)=>{
    const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));
  });
  const evaluate=async expr=>{
    const result=await send('Runtime.evaluate',{expression:expr,returnByValue:true,awaitPromise:true});
    if(result.exceptionDetails)throw Error(result.exceptionDetails.text);
    return result.result.value;
  };
  const stateExpression=`(function(){
    var value=null;
    try {value=angular.element(document.documentElement).injector().get('$translate').use()}catch(e){}
    return {href:location.href,lang:document.documentElement.lang,translate:value,
      scriptNew:!!document.querySelector('script[src*="20261024-language-audit"]'),
      editorOpen:!!document.querySelector('.nv-live-editor'),
      phraseCount:window.__oscUiL10n?window.__oscUiL10n.phraseCount:0};
  })()`;
  const before=await evaluate(stateExpression);
  console.log('CEF_BEFORE='+JSON.stringify(before));
  if(reload){
    if(!/#\/base$/.test(before.href) || before.editorOpen){
      console.log('RELOAD_SKIPPED unsafe UI route/editor (preserved)');
    } else {
      await send('Page.reload',{ignoreCache:true});
      await new Promise(ok=>setTimeout(ok,2300));
      const after=await evaluate(stateExpression);
      console.log('CEF_AFTER='+JSON.stringify(after));
      if(!after.scriptNew)throw Error('CEF did not load new versioned script');
      console.log('RELOAD_OK current_language='+after.translate);
    }
  }
  ws.close();
}
main().catch(e=>{console.error('FAIL '+e.stack);process.exitCode=1});
