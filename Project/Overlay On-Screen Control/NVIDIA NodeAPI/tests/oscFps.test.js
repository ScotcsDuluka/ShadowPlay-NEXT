'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../osc/mods/osc-fps.js'),'utf8');
const flush=()=>new Promise(resolve=>setImmediate(resolve));
function fixture(handler) {
  const states={},controllers={},routes=[],requests=[],listeners={};
  const module={config(args){args.at(-1)({state(name,value){states[name]=value;}});return this;},controller(name,args){controllers[name]=args.at(-1);return this;}};
  const window={angular:{module:()=>module}};
  if(handler)window.cefQuery=options=>{const request=JSON.parse(options.request);requests.push(request);handler(request,options);};
  const context={window,JSON,Error};vm.runInNewContext(source,context);
  const controller={};controllers.OscFpsController.call(controller,executor=>new Promise(executor),{go:name=>routes.push(name)},{$on(name,callback){listeners[name]=callback;}});
  return {window,context,states,controller,routes,requests,listeners};
}
function success(options,fps){options.onSuccess(JSON.stringify({fps,cefFps:Math.min(fps,60),min:15,max:240,scope:'desktop'}));}

test('loads current native setting and uses native NVIDIA select and buttons',async()=>{
  const f=fixture((request,options)=>success(options,75));await flush();
  assert.equal(f.controller.loaded,true);assert.equal(f.controller.fps,75);assert.equal(f.controller.busy,false);
  assert.deepEqual(f.requests,[{command:'QUERY_OSC_GET_FPS'}]);
  const template=f.states['main.osc-fps'].template;
  assert(template.includes('<md-select class="osc-select resolution-fps-select"'));
  assert(template.includes('class="osc-side-buttons"'));assert(template.includes('osc-button osc-button-gray'));
  f.controller.back();assert.deepEqual(f.routes,['main.main-menu']);
});
test('saves selected FPS, blocks duplicate in-flight changes and accepts native result',async()=>{
  let apply;
  const f=fixture((request,options)=>{if(request.command==='QUERY_OSC_GET_FPS')success(options,60);else apply=options;});await flush();
  f.controller.fps=120;const pending=f.controller.save();f.controller.save();
  assert.equal(f.controller.busy,true);assert.equal(f.requests.length,2);assert.equal(f.requests[1].fps,120);
  success(apply,120);await pending;
  assert.equal(f.controller.fps,120);assert.equal(f.controller.saved,true);assert.equal(f.controller.busy,false);
  f.controller.save();assert.equal(f.requests.length,2,'unchanged value must not be saved again');
});
test('save errors restore the persisted value and permit retry',async()=>{
  let fail=true;
  const f=fixture((request,options)=>{if(request.command==='QUERY_OSC_GET_FPS')success(options,75);else if(fail)options.onSuccess('{"error":"disk full"}');else success(options,request.fps);});await flush();
  f.controller.fps=90;await f.controller.save();assert.equal(f.controller.fps,75);assert.equal(f.controller.saved,false);assert(f.controller.error);
  fail=false;f.controller.fps=90;await f.controller.save();assert.equal(f.controller.fps,90);assert.equal(f.controller.error,'');
});
test('missing native bridge and malformed responses do not display false success',async()=>{
  const f=fixture();await flush();assert.equal(f.controller.loaded,false);assert(f.controller.error);
  f.window.cefQuery=options=>options.onSuccess('{bad json');await f.controller.load();assert.equal(f.controller.loaded,false);assert(f.controller.error);
  f.window.cefQuery=options=>success(options,60);await f.controller.load();assert.equal(f.controller.loaded,true);assert.equal(f.controller.error,'');
});
test('rejects nonnumeric, fractional and out-of-range values before sending',async()=>{
  const f=fixture((request,options)=>success(options,60));await flush();
  for(const value of [0,14,241,60.5,'120',NaN,Infinity]){f.controller.fps=value;f.controller.save();assert.equal(f.controller.fps,60);}
  assert.equal(f.requests.length,1);
});
test('preserves custom saved FPS and does not mutate a destroyed controller',async()=>{
  const f=fixture((request,options)=>success(options,117));await flush();assert(f.controller.options.includes(117));
  let apply;f.window.cefQuery=options=>{apply=options;};f.controller.fps=120;const pending=f.controller.save();
  f.listeners.$destroy();success(apply,120);await pending;assert.equal(f.controller.saved,false);
});
test('reloading the module does not register it twice',async()=>{
  const f=fixture((request,options)=>success(options,60));await flush();
  f.window.angular.module=()=>{throw Error('duplicate registration');};vm.runInNewContext(source,f.context);
});
