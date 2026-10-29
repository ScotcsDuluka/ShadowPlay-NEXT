/* Test-only local CEF DevTools playback smoke. No native actions or UI mutation. */
'use strict';
const sleep=ms=>new Promise(ok=>setTimeout(ok,ms));
async function getPage(){
 try{const resp=await fetch('http://127.0.0.1:59357/json',{signal:AbortSignal.timeout(1000)});
 if(!resp.ok)return null;const tabs=await resp.json();return tabs.find(x=>x.type==='page'&&/127\.0\.0\.1/.test(x.url))||null;}catch{return null}
}
(async()=>{
 let t=null;for(let i=0;i<55;i++){t=await getPage();if(t)break;await sleep(180)}
 if(!t)throw Error('Launcher CEF debugging target unavailable');
 console.log('TARGET '+t.url);
 const ws=new WebSocket(t.webSocketDebuggerUrl);await new Promise((yes,no)=>{ws.addEventListener('open',yes,{once:true});ws.addEventListener('error',no,{once:true})});
 let seq=0;const pending=new Map();
 ws.addEventListener('message',e=>{const m=JSON.parse(e.data);const cb=pending.get(m.id);if(cb){pending.delete(m.id);cb(m)}});
 function send(method,params){
 return new Promise((yes,no)=>{const id=++seq;const timer=setTimeout(()=>{pending.delete(id);no(Error('Timeout '+method))},2400);
 pending.set(id,result=>{clearTimeout(timer);yes(result)});ws.send(JSON.stringify({id,method,params}))})}
 const expr=`(function(){var v=document.getElementById('launcherIntroVideo');var i=document.getElementById('launcherIntro');var s=window.__launcherIntroState||{};return {state:s,video:v?{src:v.currentSrc,readyState:v.readyState,networkState:v.networkState,error:v.error?v.error.code:null,paused:v.paused,currentTime:+v.currentTime.toFixed(2),duration:v.duration}:null,introVisible:i&&getComputedStyle(i).display!=='none',bodyClass:document.body.className,quickPresent:!!document.getElementById('quickPowerToggle')}})()`;
 let last=null;const seen=[];
 for(let i=0;i<29;i++){
  const result=await send('Runtime.evaluate',{expression:expr,returnByValue:true});
  const data=result.result?.result?.value;
  if(data){last=data;if(!seen.some(x=>x.state?.reason===data.state?.reason))seen.push(data);
   if(data.state?.reason==='ended'&&data.state?.finished)break;}
  await sleep(220);
 }
 console.log('SNAPSHOTS',JSON.stringify(seen));
 console.log('FINAL',JSON.stringify(last));
 ws.close();
 if(!last?.quickPresent || !last?.state?.finished)throw Error('Launcher did not complete intro lifecycle');
 if(last.state.reason!=='ended')throw Error('Playback failed: '+last.state.reason);
 if(!/\.webm(?:$|\?)/.test(last.video.src))throw Error('CEF did not select WebM');
 console.log('LAUNCHER_INTRO_REAL_CEF_PASS');
})().catch(e=>{console.error('LAUNCHER_INTRO_CEF_FAIL',e.stack||e.message);process.exitCode=1});
