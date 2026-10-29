'use strict';
// Read-only CEF73 media-codec probe against running OSC: uses the same libcef.dll
// as Launcher.Cef but does not alter UI state or read desktop pixels.
const http=require('http');
http.get('http://127.0.0.1:59099/json',r=>{let s='';r.on('data',x=>s+=x);r.on('end',()=>{
 const page=JSON.parse(s).find(x=>x.type==='page'&&x.url.includes('59011'));
 if(!page){console.log('NO_OSC_CEF_TARGET');return}
 const ws=new WebSocket(page.webSocketDebuggerUrl);
 ws.addEventListener('open',()=>ws.send(JSON.stringify({id:42,method:'Runtime.evaluate',params:{
 expression:`(function(){var v=document.createElement('video');return {mp4:v.canPlayType('video/mp4'),h264:v.canPlayType('video/mp4; codecs="avc1.42E01E"'),aac:v.canPlayType('audio/mp4; codecs="mp4a.40.2"'),webm:v.canPlayType('video/webm; codecs="vp8"'),userAgent:navigator.userAgent}})()`,
 returnByValue:true
 }})));
 ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.id===42){console.log(JSON.stringify(m.result?.result?.value||m));ws.close()}});
 });}).on('error',e=>{console.log('NO_OSC_DEBUG:',e.message);process.exitCode=2});
