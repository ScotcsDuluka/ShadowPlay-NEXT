'use strict';
const assert=require('assert');
const http=require('http');
const express=require('express');
const mount=require('../shims/performance-route');
async function scenario(output,expectedStatus) {
  let calls=0;
  const app=express();
  mount(app,{execFile:(file,args,options,callback)=>{
    calls++;assert(options.windowsHide);assert(args.includes('--format=csv,noheader,nounits'));
    callback(null,output);
  }});
  const server=await new Promise(resolve=>{const server=app.listen(0,'127.0.0.1',()=>resolve(server));});
  function query(command) {
    return new Promise((resolve,reject)=>{
      const body=JSON.stringify({command});
      const req=http.request({hostname:'127.0.0.1',port:server.address().port,path:'/Duluka/v.1.0/Performance/Query',
        method:'POST',headers:{'Content-Type':'application/json','Content-Length':Buffer.byteLength(body)}},res=>{
        let raw='';res.on('data',chunk=>raw+=chunk);res.on('end',()=>resolve({status:res.statusCode,body:JSON.parse(raw)}));
      });req.on('error',reject);req.end(body);
    });
  }
  try {
    const response=await query('GetGpuInfo');assert.strictEqual(response.status,expectedStatus);
    if(expectedStatus===200) {
      assert.strictEqual(response.body.payload.gpus[0].name,'Test GPU');
      await query('GetGpuInfo');assert.strictEqual(calls,1,'GPU catalog should be cached');
      const config=await query('GetOCConfig');assert.strictEqual(config.body.payload.ocConfig.manualOcSupported,false);
      const unsupported=await query('StartOcManualScan');assert.strictEqual(unsupported.body.payload.result,false);
      const folder=await query('GetDefaultLoggingPath');assert.strictEqual(typeof folder.body.payload.path.logpath,'string');
    } else assert.strictEqual(response.body.payload.result,false);
  } finally {await new Promise(resolve=>server.close(resolve));}
}
(async()=>{
  await scenario('0, Test GPU, GPU-test, 0000:01:00.0\r\n',200);
  await scenario('invalid catalog',503);
  console.log('PASS: Performance GPU catalog parsing, cache, unsupported OC and logging-path contracts');
})().catch(error=>{console.error(error);process.exitCode=1;});
