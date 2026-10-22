'use strict';
const fs=require('fs');
(async()=>{
 const tab=(await(await fetch('http://127.0.0.1:59099/json/list')).json()).find(t=>t.type==='page'&&t.url.includes('59011'));if(!tab)throw Error('No OSC CEF tab');
 const ws=new WebSocket(tab.webSocketDebuggerUrl);await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j});
 const result=await new Promise((r,j)=>{const timeout=setTimeout(()=>j(Error('CEF capture timeout')),8000);
  ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id===1){clearTimeout(timeout);m.error?j(Error(JSON.stringify(m.error))):r(m.result)}};
  ws.send(JSON.stringify({id:1,method:'Page.captureScreenshot',params:{format:'png',captureBeyondViewport:false}}))
 });
 const p='C:/My Project/NVIDIA-Shadowplay/build_test/NVIDIA GroupLayout Backdrop Fix 20261022/live-after-backdrop.png';
 fs.writeFileSync(p,Buffer.from(result.data,'base64'));console.log('SAVED_CEF_VISUAL',p);
 ws.close();
})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
