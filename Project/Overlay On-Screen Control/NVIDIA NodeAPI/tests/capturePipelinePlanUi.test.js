'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const root=path.join(__dirname,'../osc');
const mainJs=fs.readFileSync(path.join(root,'mods/video-capture-options.js'),'utf8');
const routeJs=fs.readFileSync(path.join(root,'mods/advanced-capture-route.js'),'utf8');
const mainCss=fs.readFileSync(path.join(root,'mods/video-capture-options.css'),'utf8');
const routeCss=fs.readFileSync(path.join(root,'mods/advanced-capture-route.css'),'utf8');
const index=fs.readFileSync(path.join(root,'index.html'),'utf8');
const settings={engineMode:'Duluka',apiCapture:'dxgi_desktop_duplication',
  quality:'Custom',encoder:'NVENC_H264',encoderPreset:4,captureCursor:true,
  fps:60,bitrateKbps:50000,useNativeResolution:true,width:1920,height:1080};
const catalog={schemaVersion:1,readOnly:true,policyValid:true,providers:[
  {id:'Duluka',routeWired:true,encoders:['h264_nvenc']},
  {id:'FFmpeg',routeWired:true,encoders:['h264_nvenc','hevc_nvenc']}
]};
function createHarness(opts={}) {
  let route,controller,scopeDestroy=()=>{},vmInstance;
  const calls=[],goes=[];
  const fakeAngular={
    copy(x){return JSON.parse(JSON.stringify(x));},
    module(name){
      assert.equal(name,'main');
      return {
        config(items) {
          items.at(-1)({state(name,data){route={name,...data};}});
          return this;
        },
        controller(name,items) {
          assert.equal(name,'OscAdvancedCaptureController');
          controller=items.at(-1);return this;
        }
      };
    }
  };
  const win={angular:fakeAngular};
  vm.runInNewContext(routeJs,{window:win,angular:fakeAngular,Array,Number,Object,
    String,JSON,Error}, {filename:'advanced-capture-route.js'});
  const http={
    get(url) {
      calls.push({method:'GET',url});
      if(url.endsWith('/settings'))return Promise.resolve({data:opts.settings || settings});
      if(url.endsWith('/engines'))return Promise.resolve({data:opts.catalog || catalog});
      return Promise.reject(Error('Unexpected URL'));
    },
    post(url,data) {
      calls.push({method:'POST',url,data:JSON.parse(JSON.stringify(data))});
      return Promise.resolve({data:{...data}});
    }
  };
  const state={go(name){goes.push(name);return Promise.resolve();}};
  const scope={$on(name,cb){if(name==='$destroy')scopeDestroy=cb;}};
  const target={};
  controller.call(target,http,state,scope);
  vmInstance=target;
  return {view:target,route,calls,goes,destroy(){scopeDestroy();}};
}
async function settle(){await Promise.resolve();await Promise.resolve();await Promise.resolve();await Promise.resolve();}

test('Native NVIDIA UI route is registered once in HEAD before bootstrap',()=>{
  assert.match(routeJs,/state\('main\.osc-capture-advanced'/);
  assert.match(routeJs,/url:\s*'\/preferences\/video-capture\/advanced'/);
  assert.match(routeJs,/controllerAs:\s*'captureAdvanced'/);
  assert.match(routeJs,/controller:\s*'OscAdvancedCaptureController'/);
  assert.match(routeJs,/\['\$http', '\$state', '\$scope', function/);
  assert.equal(index.split('/mods/advanced-capture-route.js').length-1,1);
  assert.equal(index.split('/mods/advanced-capture-route.css').length-1,1);
  assert.ok(index.indexOf('advanced-capture-route.js')<index.indexOf('</head>'));
  assert.match(routeJs,/class="osc-content nv-advanced-capture-route"/);
  assert.match(routeJs,/class="osc-bread-crumb"/);
  assert.match(routeJs,/class="osc-central-div"/);
  assert.match(routeJs,/class="preferences-root-panel"/);
  assert.match(routeJs,/class="osc-side-buttons"/);
  assert.match(routeJs,/class="osc-button osc-button-gray"/);
  assert.match(routeJs,/class="osc-button osc-button-green"/);
});

test('Main Video capture uses native OSC button and no longer inserts fake subpage',()=>{
  assert.match(mainJs,/panelVersion = '75'/);
  assert.match(mainJs,/openAdvancedCapture = function/);
  assert.match(mainJs,/injector\.get\('\$state'\)\.go\('main\.osc-capture-advanced'\)/);
  assert.match(mainJs,/advancedButton\.className = 'osc-button osc-button-gray nv-video-advanced-open-button'/);
  assert.doesNotMatch(mainJs,/host\.appendChild\(advancedPage\)/);
  assert.doesNotMatch(mainJs,/panel\.appendChild\(advancedSection\)/);
  assert.doesNotMatch(mainJs,/classList\.add\('nv-video-advanced-open'\)/);
  assert.match(mainJs,/window\.clearInterval\(runtimeStatusTimer\)/);
  assert.match(mainJs,/panel\.appendChild\(displaySection\)/);
  assert.match(mainJs,/footer\.appendChild\(cursorSwitch\)/);
});

test('Six pipeline selectors are design-only and unavailable modes stay disabled',async()=>{
  const h=createHarness();await settle();
  assert.equal(h.route.name,'main.osc-capture-advanced');
  assert.equal(h.view.ready,true);
  assert.equal(h.view.definitions.length,6);
  assert.equal(h.view.definitions.at(-1).disabled,true);
  assert.equal(h.view.definitions[0].choices[1].disabled,true);
  assert.equal(h.view.definitions[2].choices[1].disabled,true);
  assert.equal(h.view.definitions[3].choices[2].disabled,true);
  h.view.plan.systemAudio='wasapi';
  h.view.plan.exclusive='dxgi';
  assert.equal(h.calls.filter(x=>x.method==='POST').length,0);
  assert.match(h.route.template,/ng-model="captureAdvanced\.plan\[item\.key\]"/);
  assert.match(h.route.template,/ng-disabled="item\.disabled"/);
  assert.match(h.route.template,/ng-disabled="choice\.disabled"/);
});

test('Save requires catalog-wired encoder and preserves other Capture settings',async()=>{
  const h=createHarness({catalog:{...catalog,providers:[
    {id:'Duluka',routeWired:true,encoders:['h264_nvenc','hevc_nvenc']}
  ]}});
  await settle();
  assert.equal(h.view.ready,true);
  assert.equal(h.view.dirty,false);
  h.view.encoder='NVENC_HEVC';h.view.preset=5;h.view.changed();
  assert.equal(h.view.dirty,true);
  await h.view.save();
  const posts=h.calls.filter(x=>x.method==='POST');
  assert.equal(posts.length,1);
  assert.equal(posts[0].data.encoder,'NVENC_HEVC');
  assert.equal(posts[0].data.encoderPreset,5);
  assert.equal(posts[0].data.apiCapture,settings.apiCapture);
  assert.equal(posts[0].data.captureCursor,settings.captureCursor);
  assert.equal(h.view.dirty,false);
  h.view.encoder='AMF_HEVC';h.view.changed();
  await h.view.save();
  assert.equal(h.calls.filter(x=>x.method==='POST').length,1);
});

test('Bad catalog must fail closed; Back restores NVIDIA Video settings route',async()=>{
  const h=createHarness({catalog:{schemaVersion:1,readOnly:true,policyValid:false,providers:[]}});
  await settle();
  assert.equal(h.view.ready,false);
  assert.match(h.view.error,/catalog unavailable/);
  await h.view.save();
  assert.equal(h.calls.filter(x=>x.method==='POST').length,0);
  await h.view.back();
  assert.deepEqual(h.goes,['main.preferences.video']);
  h.destroy();
  assert.equal(h.calls.filter(x=>x.method==='POST').length,0);
});

test('New layout does not impose fixed dimensions on native md components',()=>{
  assert.match(routeCss,/\.nv-advanced-capture-route/);
  // v77: two columns keep long translated Material values inside the content area.
  assert.match(routeCss,/grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)/);
  assert.match(routeJs,/class="nv-advanced-capture-group nv-advanced-capture-plan"/);
  assert.match(routeJs,/class="nv-advanced-capture-group nv-advanced-capture-live"/);
  assert.match(mainCss,/nv-preferences-video \.resolution-fps\.layout-row\.nv-video-engine-fields-row\s*\{[^}]*grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)/);
  assert.doesNotMatch(routeCss,/md-select[^{}]*\{[^{}]*(height|min-height|max-height)\s*:/);
  assert.doesNotMatch(routeCss,/md-input-container[^{}]*\{[^{}]*(height|min-height|max-height)\s*:/);
  assert.match(mainCss,/v75: native NVIDIA Material owns component dimensions/);
  // Original NVIDIA quality presets are not custom capture controls.
  assert.doesNotMatch(mainCss,/nv-preferences-video\s+\.quality-list-box/);
  assert.doesNotMatch(mainCss,/nv-preferences-video\s+\.settings-customize-directive/);
  assert.doesNotMatch(mainCss,/UI v72: LOCKED NVIDIA-style/);
  assert.doesNotMatch(mainCss,/v74 — native NVIDIA md-/);
  assert.doesNotMatch(mainCss,/height:\s*(266|270|340|400|468)px/);
});

test('Advanced Capture title and design controls have EN and TH translation keys',()=>{
  const locales=['en-US.json','th-TH.json'].map(n=>JSON.parse(
    fs.readFileSync(path.join(root,'l10n',n),'utf8')));
  const matches=[...routeJs.matchAll(/l10n\.oscCapture[A-Za-z]+/g)];
  for(const key of [...new Set(matches.map(x=>x[0]))]){
    for(const locale of locales) assert.ok(locale[key], 'missing locale key '+key);
  }
});

test('All-GPU-failed NVENC synthetic proof disables codec in Advanced route',async()=>{
  const failed={...catalog,
    providers:[{id:'FFmpeg',routeWired:true,encoders:['h264_nvenc','av1_nvenc']}],
    workerRuntime:{ffmpegBinary:{nvencSyntheticValidation:{
      status:'complete',gpuCount:2,results:[
        {gpuIndex:0,encoder:'av1_nvenc',syntheticEncodePassed:false},
        {gpuIndex:1,encoder:'av1_nvenc',syntheticEncodePassed:false}
      ]
    }}}
  };
  const h=createHarness({settings:{...settings,engineMode:'FFmpeg'},catalog:failed});
  await settle();
  assert.equal(h.view.ready,true);
  assert.equal(h.view.codecs.some(x=>x.id==='NVENC_AV1'),false);
  h.view.encoder='NVENC_AV1';h.view.changed();
  await h.view.save();
  assert.equal(h.calls.filter(x=>x.method==='POST').length,0);
});

test('Encoder-specific presets keep NVIDIA, QSV and AMF indices aligned',async()=>{
  const providers=[{id:'FFmpeg',routeWired:true,
    encoders:['h264_nvenc','h264_qsv','h264_amf','libx264']}];
  const h=createHarness({settings:{...settings,engineMode:'FFmpeg'},
    catalog:{...catalog,providers}});
  await settle();
  assert.equal(h.view.presets[0].name,'P1');
  h.view.encoder='AMF_H264';h.view.changed();
  assert.deepEqual(Array.from(h.view.presets,x=>x.id),[2,4,6]);
  h.view.encoder='QuickSync_H264';h.view.changed();
  assert.equal(h.view.presets[0].name,'veryslow');
  h.view.encoder='LibX264';h.view.changed();
  assert.equal(h.view.presets[0].name,'slow');
  assert.equal(h.calls.filter(x=>x.method==='POST').length,0);
});

test('Returning from Advanced retries initialization after NVIDIA video DOM is rebuilt',()=>{
  assert.match(mainJs,/function scheduleVideoPageInitialization\(limit\)/);
  assert.match(mainJs,/if \(videoPage\(\)\) scheduleVideoPageInitialization\(40\)/);
  assert.match(mainJs,/scheduleVideoPageInitialization\(120\)/);
  assert.match(mainJs,/window\.clearInterval\(runtimeStatusTimer\)/);
});

test('Advanced UI keeps labels outside md-select and leaves NVIDIA controls naturally sized', () => {
  const template = routeJs.match(/var template = \[([\s\S]*?)\]\.join\(''\);/);
  assert.ok(template, 'Advanced template must exist');
  assert.match(template[1], /class="nv-advanced-capture-field-label"/);
  assert.match(template[1], /<md-select class="osc-select resolution-fps-select"/);
  assert.doesNotMatch(template[1], /<md-input-container/);
  assert.doesNotMatch(template[1], /<label translate=/);
  assert.match(routeCss, /\.nv-advanced-capture-route > \.osc-central-div\s*\{[^}]*width:\s*auto\s*!important/);
  assert.match(routeCss, /\.nv-advanced-capture-route > \.osc-side-buttons\s*\{[^}]*flex:\s*0 0 auto/);
  assert.match(routeCss, /\.nv-advanced-capture-field-label\s*\{[^}]*display:\s*block/);
  assert.doesNotMatch(routeCss, /md-select\s*\{[^}]*\b(?:height|min-height|max-height)\s*:/);
  assert.doesNotMatch(routeCss, /md-option\s*\{[^}]*\b(?:height|min-height|max-height)\s*:/);
  assert.doesNotMatch(routeCss, /overflow-y:\s*(?:auto|scroll)/);
});
