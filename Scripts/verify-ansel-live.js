'use strict';
// Temporary uniform/toggle changes against a real game, with readback and
// restoration in finally. No ReShade preset file is saved by this verifier.
const assert = require('assert');
const http = require('http');
const fs = require('fs');
const path = require('path');
const express = require('../Project/Overlay On-Screen Control/NVIDIA NodeAPI/node_modules/express');
const { AnselClient } = require('../Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/ansel-client');
const mount = require('../Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/game-filter-route');

async function main() {
  const pid = Number(process.argv[2]);
  if (!Number.isInteger(pid) || pid <= 0) throw new Error('Usage: node Scripts/verify-ansel-live.js <game-pid>');
  const client = new AnselClient({timeout:4000});
  const initial = await client.state(pid);
  const state = initial.state;
  assert.strictEqual(state.api, 'D3D11');
  const filter = state.filters.find(f => f.effect === 'FXAA.fx' && state.stack.indexOf(f.id) === state.stack.length - 1);
  if (!filter) throw new Error('Live verification requires FXAA as the last active technique, to preserve other effects');
  const control = filter.controls.find(c => c.id === 'Subpix' && c.dataType === 'float' && c.currentValue.length === 1);
  if (!control) throw new Error('FXAA Subpix control is not exposed by the runtime');
  const original = control.currentValue[0], changed = original < 0.5 ? 0.75 : 0.25;
  const slot = state.stack.indexOf(filter.id), events = [];
  const app = express();
  const route = mount(app, {emit:(channel,data) => events.push({channel,data})}, {client,poll:false});
  const server = await new Promise(resolve => { const s = app.listen(0,'127.0.0.1',()=>resolve(s)); });
  const base = '/NvCamera/v.1.0';
  const call = (endpoint, body) => new Promise((resolve,reject) => {
    const payload = JSON.stringify(body || {});
    const request = http.request({hostname:'127.0.0.1',port:server.address().port,path:base+endpoint,method:'POST',
      headers:{'Content-Type':'application/json','Content-Length':Buffer.byteLength(payload),'x-ansel-game-pid':String(pid)}}, response => {
      let raw=''; response.on('data',c=>raw+=c); response.on('end',()=>resolve({status:response.statusCode,body:JSON.parse(raw)}));
    });
    request.on('error',reject); request.end(payload);
  });
  let mutated = false, report;
  const attribute = '/Filter/' + encodeURIComponent(filter.id) + '/Attribute';
  try {
    const available = await call('/GetAvailable',{}); assert.strictEqual(available.body.available,true);
    const catalog = await call('/GetFilters',{}); assert(catalog.body.filters.some(f=>f.id===filter.id));
    mutated = true;
    let result = await call(attribute,{controlId:control.id,value:[changed],stackIdx:slot});
    assert.strictEqual(result.status,200,JSON.stringify(result.body));
    result = await call('/Filter/GetInfo',{stackIdx:slot});
    assert(Math.abs(result.body.controls.find(c=>c.id===control.id).currentValue[0]-changed)<0.00001,'Live ReShade did not acknowledge the new uniform');
    result = await call(attribute,{controlId:control.id,value:[2],stackIdx:slot}); assert.strictEqual(result.status,400);
    result = await call('/Filter/Remove',{stackIdx:slot}); assert.strictEqual(result.status,200);
    let updated = await client.state(pid); assert.strictEqual(updated.state.filters.find(f=>f.id===filter.id).enabled,false);
    result = await call('/Filter',{type:filter.id,stackIdx:slot}); assert.strictEqual(result.status,200);
    updated = await client.state(pid); assert.strictEqual(updated.state.filters.find(f=>f.id===filter.id).enabled,true);
    assert(events.some(e=>e.data.type==='setFilterAttribute' && e.data.status===1));
    assert(events.some(e=>e.data.type==='currentFilter' && e.data.id===filter.id));
    report = {verifiedUtc:new Date().toISOString(),processId:pid,executable:state.executable,api:state.api,
      session:state.session,generation:state.generation,filters:catalog.body.filters,
      uniform:{filter:filter.id,control:control.id,original,changed,readbackVerified:true},
      toggleReadbackVerified:true,outOfRangeRejected:true,notificationCount:events.length};
  } finally {
    try {
      if (mutated) {
        const current=await client.state(pid);
        // Restore only the parameter we changed. Other effects may intentionally
        // contain preset values outside their editor annotations.
        const restore=state.stack.map((id,index)=>({id,stackIdx:index,controls:id===filter.id?[{id:control.id,value:[original]}]:[]}));
        await client.command(current,'replace',{filters:restore});
        const restored=await client.state(pid);
        assert.deepStrictEqual(restored.state.stack,state.stack,'Original effect stack was not restored');
        assert(Math.abs(restored.state.filters.find(f=>f.id===filter.id).controls.find(c=>c.id===control.id).currentValue[0]-original)<0.00001,'Original uniform value was not restored');
        console.log('RESTORED: original ReShade stack and exposed parameter values');
      }
    } finally { route.close(); await new Promise(resolve=>server.close(resolve)); }
  }
  report.restorationVerified=true;
  const reportPath=path.resolve(__dirname,'../Build/AnselValidation/live-result.json');
  fs.mkdirSync(path.dirname(reportPath),{recursive:true});
  fs.writeFileSync(reportPath,JSON.stringify(report,null,2));
  console.log('PASS: real D3D11 ReShade catalog, OSC HTTP slider write/readback, toggle, rejection, notifications and restoration');
}
main().catch(error=>{console.error(error);process.exitCode=1;});
