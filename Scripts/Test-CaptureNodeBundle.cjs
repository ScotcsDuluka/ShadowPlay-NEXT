'use strict';
// Isolated loopback test of the exact staged registration block and shims.
const assert = require('assert');
const fs = require('fs');
const http = require('http');
const path = require('path');
const vm = require('vm');
const moduleApi = require('module');
const createRequire = moduleApi.createRequire || moduleApi.createRequireFromPath;
const stage = path.resolve(process.argv[2] || '');
const express = require('../Project/Overlay On-Screen Control/NVIDIA NodeAPI/node_modules/express');
const index = fs.readFileSync(path.join(stage, 'index.js'), 'utf8');
const start = index.indexOf('// Capture routes use the current guarded gateway;');
const end = index.indexOf("require('./shims/route-floor.js')(app, io);", start);
assert(start >= 0 && end > start, 'staged route block must precede route floor');
const app = express();
vm.runInNewContext(index.slice(start, end), {app, require: createRequire(path.join(stage, 'index.js'))});
let hits = 0;
const upstream = http.createServer((req,res) => {
  hits++;
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({schemaVersion:1, source:'NvCapture', runtimeEnabled:true,
    workerReady:true, runtimeReady:true, recording:false, busy:false}));
});
function listen(server) { return new Promise(r => server.listen(0, '127.0.0.1', r)); }
function call(port, origin) { return new Promise((resolve,reject) => {
  const headers = origin ? {Origin:origin} : {};
  http.get({host:'127.0.0.1',port,path:'/DulukaCapture/v.1.0/status',headers}, r => {
    let body=''; r.on('data',x => body += x); r.on('end',() => resolve({code:r.statusCode,body:JSON.parse(body)}));
  }).on('error',reject);
}); }
(async () => {
  await listen(upstream);
  process.env.NV_CAPTURE_RUNTIME_ENABLED='1';
  process.env.NV_CONTAINER_CONTROL_API_PORT=String(upstream.address().port);
  const server = http.createServer(app);
  await listen(server);
  try {
    const ok=await call(server.address().port);
    assert.strictEqual(ok.code,200); assert.strictEqual(ok.body.ready,true);
    assert.strictEqual(ok.body.recording,false); assert.strictEqual(ok.body.stateKnown,true);
    const bad=await call(server.address().port,'https://untrusted.example');
    assert.strictEqual(bad.code,403); assert.strictEqual(hits,1);
    console.log('CAPTURE_NODE_BUNDLE_TEST_PASS status_truth=true cross_origin_denied=true production_modified=false');
  } finally { server.close(); upstream.close(); }
})().catch(e => {console.error(e); process.exitCode=1; upstream.close();});
