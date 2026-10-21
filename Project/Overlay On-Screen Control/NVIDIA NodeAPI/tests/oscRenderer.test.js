'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../osc/mods/osc-renderer.js'),'utf8');
const flush=()=>new Promise(resolve=>setImmediate(resolve));
function response(options,state) {
  options.onSuccess(JSON.stringify(Object.assign(
    {ui:'auto',desktop:'gpu',background:'cpu',restartRequired:false},state||{})));
}
function fixture(handler) {
  const states={},controllers={},requests=[],routes=[],listeners=[],changes=[];
  const module={
    config(args){args.at(-1)({state(name,value){states[name]=value;}});return this;},
    controller(name,args){controllers[name]=args.at(-1);return this;}
  };
  const window={angular:{module:()=>module},
    __dulukaOscBackground:{setRenderer:function(mode){changes.push(mode);}}};
  if(handler)window.cefQuery=options=>{
    const request=JSON.parse(options.request);requests.push(request);handler(request,options);
  };
  const context={window,JSON,Error};vm.runInNewContext(source,context);
  const controller={};controllers.OscRendererController.call(
    controller,executor=>new Promise(executor),
    {go:route=>routes.push(route)},{$on:(event,listener)=>listeners.push(listener)});
  return {window,context,states,controller,requests,routes,listeners,changes};
}
test('loads native renderer preferences and exposes separate NVIDIA select fields',async()=>{
  const f=fixture((request,opts)=>response(opts));await flush();
  assert.equal(f.controller.loaded,true);
  assert.equal(f.controller.values.background,'cpu');
  assert.deepEqual(f.requests,[{command:'QUERY_OSC_GET_RENDERERS'}]);
  const template=f.states['main.osc-renderer'].template;
  assert.match(template,/md-select/);assert.match(template,/ng-repeat="part in rendererSettings.parts/);
  assert.equal(f.controller.parts.length,3);
  assert.deepEqual(f.changes,['cpu']);
  f.controller.back();assert.deepEqual(f.routes,['main.main-menu']);
});
test('saves all selections atomically and marks restart for UI/display',async()=>{
  let complete;
  const f=fixture((request,opts)=>{
    if(request.command==='QUERY_OSC_GET_RENDERERS')response(opts);
    else complete=opts;
  });await flush();
  f.controller.values.ui='cpu';f.controller.values.background='gpu';
  const promise=f.controller.save();f.controller.save();
  assert.equal(f.requests.length,2);
  assert.deepEqual(f.requests[1],{command:'QUERY_OSC_SET_RENDERERS',ui:'cpu',desktop:'gpu',background:'gpu'});
  response(complete,{ui:'cpu',desktop:'gpu',background:'gpu',restartRequired:true});
  await promise;
  assert.equal(f.controller.busy,false);assert.equal(f.controller.saved,true);
  assert.equal(f.controller.restartRequired,true);
  assert.deepEqual(f.changes,['cpu','gpu']);
});
test('errors roll back selection; invalid and missing bridge must never show success',async()=>{
  let fail=true;
  const f=fixture((request,opts)=>{
    if(request.command==='QUERY_OSC_GET_RENDERERS')response(opts);
    else if(fail)opts.onSuccess('{"error":"disk full"}');
    else response(opts,{ui:request.ui,desktop:request.desktop,background:request.background});
  });await flush();
  f.controller.values.background='gpu';await f.controller.save();
  assert.equal(f.controller.values.background,'cpu');assert(f.controller.error);
  fail=false;f.controller.values.background='gpu';await f.controller.save();
  assert.equal(f.controller.values.background,'gpu');assert.equal(f.controller.error,'');
  const g=fixture();await flush();
  assert.equal(g.controller.loaded,false);assert(g.controller.error);
});
test('rejects bad native values and ignores callbacks after destroy',async()=>{
  const f=fixture((req,opts)=>response(opts,{ui:'impossible'}));await flush();
  assert.equal(f.controller.loaded,false);
  f.window.cefQuery=options=>response(options);await f.controller.load();
  assert.equal(f.controller.loaded,true);
  f.controller.values.ui='bad';f.controller.save();assert(f.controller.error);
  let next;f.window.cefQuery=options=>{next=options;};
  f.controller.values={ui:'gpu',desktop:'auto',background:'gpu'};
  const pending=f.controller.save();f.listeners[0]();response(next);
  await pending;assert.equal(f.controller.saved,false);
});
test('duplicate module load is idempotent',async()=>{
  const f=fixture((req,opts)=>response(opts));await flush();
  f.window.angular.module=()=>{throw new Error('double-registration');};
  vm.runInNewContext(source,f.context);
});
