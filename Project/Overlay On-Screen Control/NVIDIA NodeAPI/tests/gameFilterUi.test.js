'use strict';
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname,'../osc/mods/ansel-game-filter.js'),'utf8');

async function scenario(existing, coldStart) {
  const data = {OtherGame:[{id:1,activeFilterStack:[{id:'keep-other-game'}]}]};
  if (existing) data.Game = [{id:1,activeFilterStack:[{id:'keep-existing-preset'}]}];
  const otherGameData={lastSlot:2,modsIsEnabled:true};
  const store = {ModsSlotStorage:JSON.stringify(data),ModsDataStorage:JSON.stringify({OtherGame:otherGameData}),DulukaModsSelectionStorage:JSON.stringify({OtherGame:2})};
  const state = {connected:true,pid:42,session:'42:test:1',generation:1,executable:'Game',stack:['fx'],filters:[{id:'fx',name:'Filter',controls:[{id:'Amount',currentValue:[0.5]}]}]};
  const applied=[],writes=[],osc={onlineState:{online:true},setOnline(){throw new Error('Local entry must not reinitialize cloud capabilities');}};
  const filterEvents=[],shutdownCalls=[],storageWrites=[];
  let nativeEntries=0,httpReads=0,catalogReads=0,enableGate=null;
  const camera = {persistSlots:{},NUM_MODS_SLOTS:3,modsSlots:[1,2,3,4].map(id=>({id,filters:[],activeFilterStack:[]})),modsSupported:true,lastActiveSlot:-1,lastSlot:-1,UIRunning:true,
    checkReshadeSupport:()=>Promise.resolve(),
    setModsStatusToNvcamera:()=>Promise.resolve(),
    isOn:()=>false,isModsOn:()=>false,hasVariableAvailability:()=>false,
    applySlot(slot){
      applied.push(slot);this.modsActiveSlot=slot;
      this.lastActiveSlot=slot?slot.id-1:-1;
      if(slot)this.lastSlot=slot.id-1;
      return slot;
    },
    shutdownModsUI(keepEffects){shutdownCalls.push(keepEffects);return Promise.resolve(true);},
    setupFilterStackEvents(enabled){filterEvents.push(enabled);},
    getSupportedFilterTypes(){catalogReads++;return Promise.resolve(200);},
    enableModsMode(enabled){
      const changed=this.modsIsEnabled!==enabled;
      const gate=enableGate;
      this.modsIsEnabled=enabled;
      return enabled && changed?this.getSupportedFilterTypes().then(()=>gate || true):Promise.resolve(true);
    },
    modsHelper:{addCurrentFilter:()=>Promise.resolve(true)},
    getCurrentFilterAttribute:()=>{},
    setAttributeV1:(id,slot,control,value)=>{writes.push(value[0]);return Promise.resolve({status:200});},
    launchUIForMods(){
      nativeEntries++;this.modsSupported=true;
      this.persistSlots.Game=this.persistSlots.Game || JSON.parse(store.ModsSlotStorage).Game || [];
      this.modsSlots=this.persistSlots.Game;
      return Promise.resolve('opened');
    }};
  const menu={tiles:{Mods:{visible:false,enabled:false},Ansel:{visible:false,enabled:false},Screenshot:{visible:false},Stream:{visible:false},QuietMode2:{visible:false}}}, menuElement={},preferencesElement={};
  const preferences={tiles:[{visible:false,precondition:()=>false}]},entered=[],notifications=[];
  let refreshMenu,checkGame,closeListener;
  const windowClickListeners=[];
  const listeners = {};
  const policy={fullInterface:true,navigationEnabled:true,preferencesEnabled:true,menus:Object.keys(menu.tiles),editorRoutes:{Ansel:'nvcamera',Mods:'mods'}};
  const services = {$http:{get:url=>{httpReads++;return Promise.resolve({data:url.indexOf('/OSC/Interface')>=0?policy:state});}},nvCameraService:camera,eventAggregator:{trigger:()=>{}},
    $window:{localStorage:{getItem:key=>store[key]}},oscDisplayService:{closeOSC:()=>{},setLocalStorage:(key,value)=>{storageWrites.push(key);store[key]=value;}},
    cefService:{query:()=>Promise.resolve()},
    oscNotificationService:{show:message=>notifications.push(message)},
    NOTIFIER_SELECTIONS:{WARNING_SUPPORTED_GAME_REQUIRED:'supported-game-required'},
    oscService:osc,$rootScope:{$evalAsync:fn=>fn(),$apply:fn=>fn(),$on:(name,fn)=>{listeners[name]=fn;return ()=>{};},$watch:fn=>{refreshMenu=fn;}},$timeout:Object.assign((fn,ms)=>setTimeout(fn,ms),{cancel:clearTimeout}),
    $interval:fn=>{checkGame=fn;},
    $state:{current:{name:'main.main-menu'},go:route=>{services.$state.current.name=route;entered.push(route);return Promise.resolve(route);}},
    $q:{when:Promise.resolve.bind(Promise),defer:()=>{let resolve,reject;const promise=new Promise((r,j)=>{resolve=r;reject=j;});return{promise,resolve,reject};}}};
  const injector={get:key=>{assert(services[key],'unexpected service '+key);return services[key];}};
  const angular={element:element=>({injector:()=>injector,controller:()=>element===menuElement?menu:preferences})};
  const context={window:{angular,addEventListener:(name,fn)=>{if(name==='click')windowClickListeners.push(fn);}},document:{addEventListener:(name,fn)=>{closeListener=fn;},querySelector:selector=>selector==='nv-main-menu'?menuElement:preferencesElement},console};
  const emitWindowClick=event=>windowClickListeners.forEach(listener=>listener(event));
  function selectStyle(index){
    const buttons=[0,1,2,3].map(()=>({closest:selector=>selector==='mods-menu'?{querySelectorAll:()=>buttons}:null}));
    emitWindowClick({target:{closest:selector=>selector==='mods-menu .mods-slot'?buttons[index]:null}});
  }
  const photoToasts=[];
  context.Notifier={show:(message,options)=>photoToasts.push({message,options})};
  if (coldStart) {
    let resolveInit, rejectInit, initCalls=0;
    const pendingInit=new Promise((resolve,reject)=>{resolveInit=resolve;rejectInit=reject;});
    camera.init=()=>{initCalls++;return pendingInit;};
    services.$state.current.name='';
    const normalGo=services.$state.go;
    services.$state.go=(route,params)=>{
      let cancelled=false;
      if (route==='mods' && listeners.$stateChangeStart) {
        listeners.$stateChangeStart({preventDefault:()=>{cancelled=true;}},{name:route},params);
        if (cancelled) return Promise.resolve('prevented');
      }
      const result=normalGo(route,params);
      if(route==='main.main-menu') camera.init().catch(()=>{});
      return result;
    };
    const nativeLaunch=camera.launchUIForMods;
    camera.launchUIForMods=function(){
      return nativeLaunch.apply(this,arguments).then(value=>services.$state.go('mods').then(()=>value));
    };
    vm.runInNewContext(source,context,{filename:'ansel-game-filter.js'});
    let prevented=0;
    const change={preventDefault:()=>{prevented++;}};
    listeners.$stateChangeStart(change,{name:'mods'},{lastState:'base'});
    listeners.$stateChangeStart(change,{name:'mods'},{lastState:'base'});
    assert.strictEqual(prevented,2,'cold links bypassed the route guard');
    assert.strictEqual(initCalls,1,'cold bootstrap did not initialize native camera exactly once');
    assert.strictEqual(nativeEntries,0,'editor opened before native initialization completed');
    assert.strictEqual(services.$state.current.name,'main.main-menu','cold route never bootstrapped the menu');
    if (coldStart==='fail') {
      rejectInit(new Error('native initialization failed'));
      for(let n=0;n<12;n++) await new Promise(resolve=>setImmediate(resolve));
      assert.strictEqual(nativeEntries,0,'failed native initialization launched the editor');
      assert.deepStrictEqual(notifications,['supported-game-required'],
        'failed bootstrap did not surface an unavailable-game notification');
      return;
    }
    resolveInit(true);
    for(let n=0;n<12;n++) await new Promise(resolve=>setImmediate(resolve));
    assert.strictEqual(nativeEntries,1,'cold route did not enter the verified native editor once');
    assert.strictEqual(services.$state.current.name,'mods',
      'cold route prepared native services but never reopened the editor');
    return;
  }
  vm.runInNewContext(source,context,{filename:'ansel-game-filter.js'});
  assert.strictEqual(camera.launchUIForNvCamera(),false,'unavailable Photo Mode was launched');
  assert.strictEqual(photoToasts[0].message,'Photo mode');
  assert.strictEqual(photoToasts[0].options.subtext,'This feature is currently unavailable.');
  assert.strictEqual(camera.UIRunning,false,'stale editor flag blocked main-menu launch');
  camera.UIRunning=true;
  emitWindowClick({target:{closest:selector=>selector==='.mods-close-button'?{}:null},preventDefault:()=>{},stopImmediatePropagation:()=>{}});
  assert.strictEqual(entered.pop(),'main.main-menu','X must return to main menu');
  assert.strictEqual(camera.UIRunning,false,'X left the editor launch guard enabled');
  camera.UIRunning=true;
  listeners.$stateChangeSuccess({}, {name:'mods'});
  assert.strictEqual(camera.UIRunning,true,'active editor flag was cleared');
  listeners.$stateChangeSuccess({}, {name:'main.main-menu'});
  assert.strictEqual(camera.UIRunning,false,'leaving editor did not clear launch guard');
  const installed = camera.launchUIForMods;
  vm.runInNewContext(source,context);
  assert.strictEqual(camera.launchUIForMods,installed,'hot mod loaded twice');
  const beforeDeepLinkEntries=nativeEntries;
  let deepLinkPrevented=false;
  listeners.$stateChangeStart({preventDefault:()=>{deepLinkPrevented=true;}},{name:'mods'},{lastState:'base'});
  assert.strictEqual(deepLinkPrevented,true,'connected pipe with default slots bypassed native editor preparation');
  await new Promise(resolve=>setImmediate(resolve));
  assert.strictEqual(nativeEntries,beforeDeepLinkEntries+1,'cold direct route did not use the native verified entry path');
  assert.notStrictEqual(services.$state.current.name,'mods','cold route opened the editor before native initialization');
  let initializedPrevented=false;
  const beforeInitializedReads=httpReads;
  listeners.$stateChangeStart({preventDefault:()=>{initializedPrevented=true;}},{name:'mods'},{lastState:'base'});
  assert.strictEqual(initializedPrevented,false,'native verified transition was recursively blocked');
  assert.strictEqual(httpReads,beforeInitializedReads,'native verified transition repeated availability requests');
  assert.strictEqual(await camera.launchUIForMods(),'opened');
  assert.strictEqual(nativeEntries,beforeDeepLinkEntries+2,'four default empty slots bypassed initial editor preparation');
  const emptySlotsReads=httpReads;
  camera.modsSlots=[1,2,3,4].map(id=>({id,filters:[],activeFilterStack:[]}));
  camera.modsIsEnabled=true;camera.lastSlot=0;
  assert.strictEqual(await camera.launchUIForMods(),'opened','empty slots were treated as a loaded catalog');
  assert(httpReads>emptySlotsReads,'stale modsSupported bypassed runtime validation');
  assert.strictEqual(nativeEntries,beforeDeepLinkEntries+3,'empty catalog skipped native preparation');
  refreshMenu();
  assert.strictEqual(camera.isModsOn(),true,'verified ReShade runtime remained gated by cloud capability');
  assert.strictEqual(menu.tiles.Mods.visible,true,'main menu still hides the local editor');
  assert.strictEqual(menu.showDiscoveryBanner(),false,'screenshot hint duplicates the visible Screenshot tile');
  assert(Object.values(menu.tiles).every(tile=>tile.visible),'full OSC menu still hides entries');
  assert.strictEqual(menu.tiles.Ansel.enabled,true,'backend navigation policy did not unlock Photo Mode entry');
  assert.strictEqual(preferences.tiles[0].visible,true,'backend policy did not unlock Settings entries');
  assert.strictEqual(preferences.tiles[0].precondition(),true);
  await menu.tiles.Ansel.click();
  assert.strictEqual(entered.pop(),'nvcamera','Photo Mode tile failed to navigate');
  assert.strictEqual(menu.tiles.Mods.enabled,true,'connected editor tile remains disabled');
  // A recreated/reset menu receives the cached policy in its first digest,
  // without waiting for a timer or another backend request.
  const initialGet=services.$http.get;
  services.$http.get=()=>{throw new Error('Menu entry must not wait for HTTP');};
  menu.tiles.Mods.visible=false;menu.tiles.Mods.enabled=false;
  refreshMenu();
  assert.strictEqual(menu.tiles.Mods.visible,true);
  assert.strictEqual(menu.tiles.Mods.enabled,true);
  services.$http.get=initialGet;
  const saved=JSON.parse(store.ModsSlotStorage);
  assert.strictEqual(saved.OtherGame[0].activeFilterStack[0].id,'keep-other-game');
  if(existing) assert.strictEqual(saved.Game[0].activeFilterStack[0].id,'keep-existing-preset');
  else {
    assert.strictEqual(saved.Game[0].activeFilterStack[0].id,'fx');
    assert.strictEqual(saved.Game[0].filters[0].savedConfigs['fx#Amount'][0],0.5);
    // Inspect applySlot through the real wrapper while an import is active.
    const result=camera.enableModsMode(true);
    camera.applySlot(undefined);
    await result;
    assert.strictEqual(applied[0].id,1,'initial editor opening selected Off instead of the imported runtime stack');
  }
  assert.strictEqual(osc.onlineState.online,true,'temporary local availability was not restored');
  // NVIDIA's FILTERS callback removes itself while its internal setup flag
  // remains true. Each actual catalog request must explicitly re-arm it.
  const beforeCatalog=catalogReads,beforeFilterEvents=filterEvents.length;
  await camera.getSupportedFilterTypes();
  await camera.getSupportedFilterTypes();
  assert.strictEqual(catalogReads,beforeCatalog+2,'catalog request was swallowed');
  assert.deepStrictEqual(filterEvents.slice(beforeFilterEvents),[false,true,false,true],
    'real catalog requests did not reset and re-arm the self-removing native listener');
  const beforeDragSaves=storageWrites.filter(key=>key==='ModsSlotStorage').length;
  await Promise.all([camera.setAttributeV1('fx',0,'Amount',[0.2]),camera.setAttributeV1('fx',0,'Amount',[0.4]),camera.setAttributeV1('fx',0,'Amount',[0.8])]);
  assert.deepStrictEqual(writes,[0.8],'slider drag queued obsolete positions instead of the latest value');
  assert.strictEqual(storageWrites.filter(key=>key==='ModsSlotStorage').length,beforeDragSaves,'slider drag serialized the entire preset store');
  const beforeToggleGet = services.$http.get;
  camera.__anselConnected = true; camera.modsSupported = true;
  camera.modsSlots = [1,2,3].map(id=>({id,filters:[{id:'fx',title:'Filter'}],activeFilterStack:[]}));
  camera.modsActiveSlot=camera.modsSlots[0];
  camera.modsIsEnabled = true; camera.lastSlot = 0;
  await camera.enableModsMode(true);
  services.$http.get = () => { throw new Error('Prepared hotkey toggle must not wait on HTTP'); };
  services.oscDisplayService.openOSC = route => { services.$state.current.name = route; };
  services.$state.current.name = 'main.main-menu';
  services.eventAggregator.trigger('Hotkey_ModsShowUI');
  assert.strictEqual(services.$state.current.name,'mods','prepared Alt+F3 failed to open immediately');
  const beforeEntryApply = applied.length;
  camera.applySlot(undefined);
  assert.strictEqual(applied.length,beforeEntryApply,'native controller initialization cleared the prepared preset');
  assert.strictEqual(camera.modsActiveSlot,camera.modsSlots[0],'native initialization lost the prepared active slot');
  assert.strictEqual(camera.applySlot(camera.modsSlots[0]),camera.modsSlots[0]);
  assert.strictEqual(applied.length,beforeEntryApply,'opening the editor reapplied an already active preset');
  camera.applySlot(undefined);
  assert.strictEqual(applied.length,beforeEntryApply+1,'explicit Off was swallowed after entry');
  services.eventAggregator.trigger('Hotkey_ModsShowUI');
  assert.strictEqual(entered.pop(),'base','Alt+F3 failed to close locally');
  const preservedPresets=store.ModsSlotStorage;
  services.eventAggregator.trigger('Hotkey_ModsShowUI');
  const beforeExplicitOff=applied.length;
  // Capture runs before Angular's native clickSlot/applySlot handler. It must
  // clear the one-use reopen shortcut so the user's Off reaches the runtime.
  selectStyle(3);
  camera.applySlot(undefined);
  assert.strictEqual(applied.length,beforeExplicitOff+1,'Off click was mistaken for the editor initialization call');
  assert.strictEqual(camera.modsActiveSlot,undefined,'Off left a preset active');
  assert.strictEqual(camera.lastActiveSlot,-1,'native Off failed to clear active slot');
  assert.strictEqual(camera.lastSlot,0,'Off discarded the last selected preset');
  assert.strictEqual(JSON.parse(store.DulukaModsSelectionStorage).Game,-1,'explicit Off was not persisted');
  assert.strictEqual(JSON.parse(store.ModsDataStorage).Game.modsIsEnabled,false,'Off persisted as enabled');
  assert.strictEqual(JSON.parse(store.DulukaModsSelectionStorage).OtherGame,2,'Off changed another game selection');
  assert.deepStrictEqual(JSON.parse(store.ModsDataStorage).OtherGame,otherGameData,'Off changed another game state');
  assert.strictEqual(store.ModsSlotStorage,preservedPresets,'Off altered saved filter presets');
  services.oscDisplayService.setLocalStorage('ModsDataStorage',JSON.stringify({OtherGame:otherGameData,Game:{lastSlot:0,modsIsEnabled:true}}));
  assert.strictEqual(JSON.parse(store.ModsDataStorage).Game.modsIsEnabled,false,'late native shutdown overwrote the explicit Off choice');
  services.eventAggregator.trigger('Hotkey_ModsShowUI');
  services.eventAggregator.trigger('Hotkey_ModsShowUI');
  assert.strictEqual(services.$state.current.name,'mods','persisted Off failed to reopen immediately');
  assert.strictEqual(camera.modsActiveSlot,undefined,'reopen silently restored the last preset after explicit Off');
  const beforeOffReopen=applied.length;
  camera.applySlot(undefined);
  assert.strictEqual(applied.length,beforeOffReopen,'reopening in Off unnecessarily cleared the runtime again');
  camera.applySlot(undefined);
  assert.strictEqual(applied.length,beforeOffReopen+1,'subsequent explicit Off was swallowed');
  selectStyle(1);
  camera.applySlot(camera.modsSlots[1]);
  assert.strictEqual(JSON.parse(store.DulukaModsSelectionStorage).Game,1,'preset selection was not saved');
  assert.strictEqual(JSON.parse(store.ModsDataStorage).Game.modsIsEnabled,true,'preset selection remained persisted as Off');
  assert.strictEqual(JSON.parse(store.ModsDataStorage).Game.lastSlot,1,'selected preset index was not saved');
  assert.strictEqual(camera.modsActiveSlot,camera.modsSlots[1],'preset selection failed to activate the slot');
  assert.strictEqual(store.ModsSlotStorage,preservedPresets,'selecting a preset changed its saved filters');
  const validModsData=store.ModsDataStorage;
  assert.doesNotThrow(()=>services.oscDisplayService.setLocalStorage('ModsDataStorage',JSON.stringify({OtherGame:otherGameData,Game:'malformed-saved-entry'})),
    'native save crashed when a game entry was not an object');
  assert.strictEqual(JSON.parse(store.ModsDataStorage).Game,'malformed-saved-entry','malformed game data was silently replaced');
  services.oscDisplayService.setLocalStorage('ModsDataStorage',validModsData);
  const editedFilter=camera.modsSlots[1].filters[0];
  editedFilter.savedConfigs={'fx#Amount':[0.77]};
  camera.modsSlots[1].activeFilterStack=[editedFilter];
  const beforeShutdownSaves=storageWrites.filter(key=>key==='ModsSlotStorage').length;
  assert.strictEqual(await camera.shutdownModsUI(true),true,'keep-effects shutdown result changed');
  const savedOnClose=JSON.parse(store.ModsSlotStorage);
  assert.strictEqual(savedOnClose.Game[1].filters[0].savedConfigs['fx#Amount'][0],0.77,'keep-effects shutdown lost edited slider values');
  assert.strictEqual(savedOnClose.Game[1].activeFilterStack[0].id,'fx','keep-effects shutdown lost the selected filter stack');
  assert.deepStrictEqual(savedOnClose.OtherGame,data.OtherGame,'saving the current game overwrote another game preset');
  assert.deepStrictEqual(shutdownCalls,[true],'keep-effects shutdown did not reach native exactly once');
  assert.strictEqual(storageWrites.filter(key=>key==='ModsSlotStorage').length,beforeShutdownSaves+1,'shutdown did not save exactly one preset snapshot');
  services.eventAggregator.trigger('Hotkey_ModsShowUI');
  assert.strictEqual(entered.pop(),'base','editor failed to close after preset selection');
  services.$http.get = beforeToggleGet;
  const disabledReads=httpReads,disabledEntries=nativeEntries;
  await camera.enableModsMode(false);
  services.$state.current.name='main.main-menu';
  assert.strictEqual(await camera.launchUIForMods(),'opened','disabled editor reused the fast path');
  assert(httpReads>disabledReads,'disabled editor skipped runtime preparation');
  assert.strictEqual(nativeEntries,disabledEntries+1,'disabled editor did not use native preparation');
  camera.modsSlots=[{id:1,filters:[{id:'fx',title:'Filter'}],activeFilterStack:[]}];
  camera.lastSlot=0;camera.modsIsEnabled=true;
  await camera.enableModsMode(true);
  state.generation=2;
  checkGame();
  await new Promise(resolve=>setImmediate(resolve));
  const changedReads=httpReads,changedEntries=nativeEntries;
  assert.strictEqual(await camera.launchUIForMods(),'opened','old generation reused the fast path');
  assert(httpReads>changedReads,'generation change failed to invalidate editor readiness');
  assert.strictEqual(nativeEntries,changedEntries+1,'new runtime generation skipped native preparation');
  // A previous game's asynchronous enable completion cannot prepare the new
  // session. Concurrent enable callers share preparation until it settles.
  camera.modsSlots=[{id:1,filters:[{id:'fx',title:'Filter'}],activeFilterStack:[]}];
  camera.lastSlot=0;
  await camera.enableModsMode(false);
  let finishEnable;
  enableGate=new Promise(resolve=>{finishEnable=resolve;});
  const beforePendingCatalog=catalogReads;
  const firstEnable=camera.enableModsMode(true);
  const secondEnable=camera.enableModsMode(true);
  assert.strictEqual(catalogReads,beforePendingCatalog+1,'concurrent enables duplicated catalog preparation');
  state.session='42:test:2';
  checkGame();
  await new Promise(resolve=>setImmediate(resolve));
  finishEnable(true);
  await Promise.all([firstEnable,secondEnable]);
  enableGate=null;
  const replacedReads=httpReads,replacedEntries=nativeEntries;
  assert.strictEqual(await camera.launchUIForMods(),'opened','late old-session enable prepared the new runtime');
  assert(httpReads>replacedReads,'session replacement failed to invalidate readiness');
  assert.strictEqual(nativeEntries,replacedEntries+1,'replaced session skipped native preparation');
  state.connected=false;checkGame();
  await new Promise(resolve=>setImmediate(resolve));
  refreshMenu();
  assert.strictEqual(menu.tiles.Mods.visible,true,'disconnection hid the editor entry');
  assert.strictEqual(menu.tiles.Mods.enabled,true,'editor entry must remain clickable to explain why a game is required');
  services.$http.get=()=>Promise.reject(new Error('No game running'));
  assert.strictEqual(await camera.launchUIForMods(),false,'Game Filter opened without a supported runtime');
  assert.deepStrictEqual(notifications,['supported-game-required'],'missing-game click should show the standard notification');
  await menu.tiles.Ansel.click();
  assert.strictEqual(notifications.length,2,'Photo Mode must explain the missing supported game too');
  let prevented=false,stopped=false;
  closeListener({target:{closest:()=>({})},preventDefault:()=>{prevented=true;},stopImmediatePropagation:()=>{stopped=true;}});
  assert(prevented && stopped,'editor close must suppress the legacy reopen handler');
  assert.strictEqual(entered.pop(),'base','editor close must leave the interactive page');
}
(async()=>{await scenario(false);await scenario(true);await scenario(false,true);await scenario(false,'fail');console.log('PASS: cold direct route after deferred and failed native initialization, live UI entry, catalog listener re-arm, empty/stale/disabled/session readiness guards, fast reopen, explicit Off persistence, native-save race, runtime stack import, preset preservation and hot-load idempotency');})()
  .catch(error=>{console.error(error);process.exitCode=1;});
