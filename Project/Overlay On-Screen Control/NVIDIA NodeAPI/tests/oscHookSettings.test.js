'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../osc/mods/osc-hook-settings.js'),'utf8');
const flush=()=>new Promise(resolve=>setImmediate(resolve));
const specs=[
 {id:'d3d9',mode:'auto',implemented:true,gpuAvailable:true,cpuAvailable:true,normalRoute:'cpu-or-windowed-gpu-dcomp-preview'},
 {id:'d3d9ex',mode:'auto',implemented:true,gpuAvailable:false,cpuAvailable:true,normalRoute:'cpu-shared-memory'},
 {id:'d3d10',mode:'auto',implemented:false,gpuAvailable:false,cpuAvailable:false,normalRoute:'unavailable'},
 {id:'d3d11',mode:'auto',implemented:true,gpuAvailable:true,cpuAvailable:false,normalRoute:'gpu-shared-or-cpu-mirror'},
 {id:'d3d12',mode:'auto',implemented:false,gpuAvailable:false,cpuAvailable:false,normalRoute:'unavailable'},
 {id:'vulkan',mode:'auto',implemented:false,gpuAvailable:false,cpuAvailable:false,normalRoute:'unavailable'},
 {id:'opengl',mode:'auto',implemented:false,gpuAvailable:false,cpuAvailable:false,normalRoute:'unavailable'}
];
const status={hostMode:'GPU_SHARED',consumerObserved:true,consumerApi:9,
  gameFocused:true,overlayOpen:true,gpuSharedRequested:true,gpuDeviceReady:true,
  dx9WindowedGpuPreviewRequested:false,dx9WindowedGpuActive:false};
function fixture(handler){
  const states={},controllers={},routes=[],requests=[],listeners=[];
  const app={
    config(deps){deps.at(-1)({state(k,route){states[k]=route;}});return this;},
    controller(k,deps){controllers[k]=deps.at(-1);return this;}
  };
  const window={angular:{module:()=>app}};
  if(handler)window.cefQuery=options=>{
    const request=JSON.parse(options.request);
    requests.push(request);
    handler(request,options);
  };
  const context={window,JSON,Error};
  vm.runInNewContext(source,context);
  const controller={};
  controllers.OscHookSettingsController.call(controller,
    callback=>new Promise(callback),{go:k=>routes.push(k)},
    {$on:(event,listener)=>listeners.push(listener)});
  return {window,context,states,controller,routes,requests,listeners};
}
function responder(req,opts){
  opts.onSuccess(JSON.stringify(req.command==='QUERY_OSC_GET_HOOK_STATUS'
    ?status:{apis:specs}));
}
test('native Hook status + per-API transport route render separately',async()=>{
  const f=fixture(responder);await flush();
  assert.equal(f.requests.length,2);
  assert.deepEqual(f.requests[0],{command:'QUERY_OSC_GET_HOOK_STATUS'});
  assert.deepEqual(f.requests[1],{command:'QUERY_OSC_GET_HOOK_TRANSPORT'});
  assert.equal(f.controller.loaded,true);
  assert.equal(f.controller.transportLoaded,true);
  assert.equal(f.controller.transportName(),'Direct3D 9 / 9Ex');
  assert.equal(f.controller.apis.length,7);
  assert.match(f.controller.routeLabel(f.controller.apis[0]),/CPU Shared Memory/);
  assert.match(f.states['main.osc-hook'].template,/oscHookD3d9GpuPreview/);
  assert.match(f.controller.routeLabel(f.controller.apis[3]),/GPU_SHARED/);
  assert.match(f.controller.selectionLabel(f.controller.apis[0]),/not proof/);
  const view=f.states['main.osc-hook'];
  assert.equal(view.url,'/preferences/overlay-hook');
  assert.match(view.template,/preferences-root-panel/);
  assert.match(view.template,/oscHookTransportTitle/);
  // Refresh and Renderer are NVIDIA right-side actions, not giant in-panel buttons.
  const actionsStart=view.template.indexOf('<div class="osc-side-buttons"');
  assert(actionsStart>0);
  for(const fn of ['hookSettings.refresh()','hookSettings.renderer()','hookSettings.back()'])
    assert(view.template.indexOf(fn)>actionsStart, fn+' must stay in right action rail');
  assert.match(view.template,/osc-button osc-button-green" ng-click="hookSettings.refresh\(\)"/);
  assert.match(view.template,/ng-disabled="!hookSettings.modeAllowed/);
  f.controller.renderer();f.controller.back();
  assert.deepEqual(f.routes,['main.osc-renderer','main.main-menu']);
});
test('each native API displays accurate capabilities, not fake modes',async()=>{
  const f=fixture(responder);await flush();
  const map=Object.fromEntries(f.controller.apis.map(api=>[api.id,api]));
  assert.equal(f.controller.modeAllowed(map.d3d9,'cpu'),true);
  assert.equal(f.controller.modeAllowed(map.d3d9,'gpu'),true); // windowed GPU preview only
  assert.equal(f.controller.modeAllowed(map.d3d9ex,'cpu'),true);
  assert.equal(f.controller.modeAllowed(map.d3d9ex,'gpu'),false);
  assert.equal(f.controller.modeAllowed(map.d3d11,'gpu'),true);
  assert.equal(f.controller.modeAllowed(map.d3d11,'cpu'),false);
  for(const id of ['d3d10','d3d12','vulkan','opengl']){
    assert.equal(map[id].implemented,false);
    assert.equal(f.controller.modeAllowed(map[id],'cpu'),false);
    assert.equal(f.controller.modeAllowed(map[id],'gpu'),false);
  }
});
test('saves valid per API preferences through native Host query',async()=>{
  let selected='auto';
  const f=fixture((req,opts)=>{
    if(req.command==='QUERY_OSC_SET_HOOK_TRANSPORT'){
      assert.equal(req.api,'d3d9');
      assert.equal(req.mode,'cpu');
      selected='cpu';
    }
    opts.onSuccess(JSON.stringify(req.command==='QUERY_OSC_GET_HOOK_STATUS'
      ?status:{apis:specs.map(api=>api.id==='d3d9'?{...api,mode:selected}:api)}));
  });
  await flush();
  const row=f.controller.apis.find(api=>api.id==='d3d9');
  row.mode='cpu';await f.controller.saveMode(row);
  assert.equal(f.controller.apis.find(api=>api.id==='d3d9').mode,'cpu');
  assert.equal(f.controller.transportSaved,true);
  assert.equal(f.requests.filter(req=>req.command==='QUERY_OSC_SET_HOOK_TRANSPORT').length,1);
});
test('rejects unsupported modes before a native query',async()=>{
  const f=fixture(responder);await flush();
  const api=f.controller.apis.find(x=>x.id==='d3d11');
  api.mode='cpu';await f.controller.saveMode(api);
  assert.equal(api.mode,'auto');
  assert.equal(f.controller.transportError,'l10n.oscHookTransportError');
  assert.equal(f.requests.filter(req=>req.command==='QUERY_OSC_SET_HOOK_TRANSPORT').length,0);
});
test('absent adapter is explicitly not claimed connected and supports refresh',async()=>{
  let statusCalls=0;
  const f=fixture((req,opts)=>{
    if(req.command==='QUERY_OSC_GET_HOOK_STATUS'){
      statusCalls++;
      opts.onSuccess(JSON.stringify({...status,consumerObserved:false,
        consumerApi:0,gameFocused:false}));
    }else opts.onSuccess(JSON.stringify({apis:specs}));
  });
  await flush();
  assert.equal(f.controller.transportName(),'Not connected');
  await f.controller.refresh();
  assert.equal(statusCalls,2);
  assert.equal(f.controller.error,'');
});
test('missing bridge, malformed native data and destroyed view fail safely',async()=>{
  const noBridge=fixture();await flush();
  assert.equal(noBridge.controller.loaded,false);
  assert.equal(noBridge.controller.transportLoaded,false);
  assert.equal(noBridge.controller.error,'l10n.oscHookError');
  const invalid=fixture((req,opts)=>opts.onSuccess(JSON.stringify(
    {...status,consumerApi:12})));
  await flush();
  assert.equal(invalid.controller.loaded,false);
  assert.equal(invalid.controller.transportLoaded,false);
  let complete;
  const pending=fixture((req,opts)=>{if(req.command==='QUERY_OSC_GET_HOOK_STATUS')complete=opts;});
  pending.listeners[0]();
  complete.onSuccess(JSON.stringify(status));
  await flush();
  assert.equal(pending.controller.loaded,false);
});
test('module is idempotent and settings route is loaded in OSC index',()=>{
  const f=fixture(responder);
  f.window.angular.module=()=>{throw Error('Module loaded twice');};
  vm.runInNewContext(source,f.context);
  const index=fs.readFileSync(path.join(__dirname,'../osc/index.html'),'utf8');
  assert.match(index,/\/Module\/Settings\/Hook\/osc-hook-settings\.js/);
  const gpu=fs.readFileSync(path.join(__dirname,'../osc/mods/gpu-profiles.js'),'utf8');
  assert.match(gpu,/id:'hook-settings',name:'Hook Settings'/);
});
test('English and Thai translations contain every per-API mode key',()=>{
  const locales=['en-US','th-TH'].map(lang=>
    JSON.parse(fs.readFileSync(path.join(__dirname,'../osc/l10n/'+lang+'.json'),'utf8')));
  for(const loc of locales){
    for(const key of ['oscHookTitle','oscHookInfo','oscHookLoading','oscHookError',
      'oscHookHostMode','oscHookConsumer','oscHookCaveat',
      'oscHookRefresh','oscHookRenderer','oscHookTransportTitle',
      'oscHookTransportInfo','oscHookTransportError','oscHookTransportUnavailable',
      'oscHookApiUnavailable','oscHookD3d9Note','oscHookD3d9ExNote',
      'oscHookD3d11Note','oscHookD3d9GpuPreview','oscHookTransportSaved'])
      assert.equal(typeof loc['l10n.'+key],'string');
  }
});


test('D3D9 GPU windowed preview is an opt-in preference, not fullscreen GPU proof',async()=>{
  let selected='auto';
  let active=false;
  const f=fixture((req,opts)=>{
    if(req.command==='QUERY_OSC_SET_HOOK_TRANSPORT'){
      assert.equal(req.api,'d3d9');
      assert.equal(req.mode,'gpu');
      selected='gpu';
    }
    opts.onSuccess(JSON.stringify(req.command==='QUERY_OSC_GET_HOOK_STATUS'
      ?{...status,dx9WindowedGpuPreviewRequested:selected==='gpu',
          dx9WindowedGpuActive:active}
      :{apis:specs.map(api=>api.id==='d3d9'?{...api,mode:selected}:api)}));
  });
  await flush();
  const row=f.controller.apis.find(api=>api.id==='d3d9');
  assert.equal(f.controller.modeAllowed(row,'gpu'),true);
  row.mode='gpu';
  await f.controller.saveMode(row);
  assert.equal(f.controller.apis.find(api=>api.id==='d3d9').mode,'gpu');
  assert.match(f.controller.routeLabel(f.controller.apis[0]),
    /only verified Windowed games/);
  assert.match(f.controller.routeLabel(f.controller.apis[0]),
    /Fullscreen CPU fallback/);
  assert.equal(f.controller.status.dx9WindowedGpuActive,false);
  active=true;
  await f.controller.refresh();
  assert.match(f.controller.routeLabel(f.controller.apis[0]),
    /preview active \(not pixel proof\)/);
  assert.equal(f.controller.status.dx9WindowedGpuActive,true);
});
