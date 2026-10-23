'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const mods=path.join(__dirname,'../osc/mods'),liveSource=fs.readFileSync(path.join(mods,'main-menu-live.js'),'utf8'),gpuSource=fs.readFileSync(path.join(mods,'gpu-profiles.js'),'utf8');
const V5=require('../osc/mods/nv-layout-v5-core.js');
const plain=value=>JSON.parse(JSON.stringify(value));
function live(window={},document={}){
 const states={},decorators={},directives={},values={};
 const module={config(args){args.at(-1)({state(name,config){states[name]=config;}},{decorator(name,args){decorators[name]=args.at(-1);}});return this;},directive(name,args){directives[name]=args.at(-1);return this;},controller(){return this;},run(){return this;}};
 window.localStorage={getItem:key=>values[key]||null,setItem:(key,value)=>{values[key]=value;}};
 window.angular=Object.assign({module:()=>module,copy:plain,noop(){}},window.angular);
 vm.runInNewContext(liveSource,{window,document,JSON,console});
 return{window,document,states,decorators,directives,values,api:window.__dulukaMenuLayout};
}
function styledNode(className){
 const styles={},priorities={},classes=new Set([className]),node={writes:0,classWrites:0,handlers:{click(){},keydown(){}},styles,attributes:{}};
 node.style={setProperty(k,v,p){styles[k]=v;priorities[k]=p;node.writes++;},getPropertyValue:k=>styles[k]||'',getPropertyPriority:k=>priorities[k]||''};
 node.classList={contains:n=>classes.has(n),add(n){classes.add(n);node.classWrites++;},remove:n=>classes.delete(n)};
 node.setAttribute=(n,v)=>{node.attributes[n]=v;};return node;
}
function menu(visibility,{v5Owner=false}={}){
 const routes=[],quietCalls=[],nodes={},styles={},micAction=()=>{},cameraAction=()=>{},galleryAction=()=>{};
 const tiles={Performance:{name:'performance-tile',visible:true,enabled:true},Gallery:{name:'gallery-tile',visible:true,click:galleryAction},Mic:{name:'mic-tile',visible:true,state:{On:{items:[{click:micAction}]}}},Camera:{name:'camera-tile',visible:true,state:{Off:{items:[{click:cameraAction}]}}},QuietMode2:{name:'quietmode2-tile',visible:true,enabled:true,click:()=>quietCalls.push('toggle')},Preferences:{name:'preferences-tile',visible:true,enabled:true,click:()=>{throw Error('Settings must open its submenu');}}};
 const scope={mainMenu:{tiles},digests:0,$applyAsync(){this.digests++;}},root={querySelector:sel=>nodes[sel.replace('md-menu.','')]||null};
 const document={documentElement:{},querySelector:()=>root,getElementById:id=>styles[id]||null,createElement:()=>({textContent:'',parentNode:null}),head:{appendChild(style){styles[style.id]=style;style.parentNode=this;}}};
 let editStarts=0;
 const window={angular:{element:el=>el===root?{scope:()=>scope}:{injector:()=>({get:()=>({go:route=>routes.push(route)})})}}},f=live(window,document);
 f.api.items.forEach(item=>{nodes[item.name]=styledNode(item.name);tiles[item.key]={name:item.name,visible:true,enabled:true,click(){},state:{}};});
 window.__dulukaStartMainMenuEditor=()=>{editStarts++;};if(visibility)window.__dulukaMainMenuVisibility=visibility;
 let nextOwner=v5Owner,nextSyncs=0;
 window.__nvNextLayoutV5={enabled:()=>nextOwner,model:V5,sync(){nextSyncs++;}};
 const context={window,document,mainMenuTileStyleId:'test-menu-style',mainMenuTileMarker:'test-marker',performanceSupportAvailable:null,liveLayoutUnsubscribe:null,gameProfilesReturnState:'main.preferences'};
 vm.createContext(context);const start=gpuSource.indexOf('  function openGameProfilesFromMainMenu()'),end=gpuSource.indexOf('  function registerPerformanceSupportListener()',start);vm.runInContext(gpuSource.slice(start,end),context);
 const synchronize=()=>vm.runInContext('updateMainMenuPerformance()',context);synchronize();
 return Object.assign(f,{routes,tiles,nodes,root,scope,quietCalls,context,synchronize,micAction,cameraAction,galleryAction,editStarts:()=>editStarts,
  owner(value){nextOwner=value;},nextSyncs:()=>nextSyncs});
}
function nativeTemplate(){
 const bundle=fs.readFileSync(path.join(__dirname,'../osc/app.js'),'utf8'),position=bundle.indexOf('main-menu-container" ng-cloak'),start=bundle.lastIndexOf('e.exports=',position)+'e.exports='.length,end=bundle.indexOf('},function',start);
 assert(position>=0&&start>=10&&end>start,'The bundled native menu template must be found');
 return vm.runInNewContext(bundle.slice(start,end),{n:()=>'/native-performance.svg'});
}
test('Settings keeps native actions and starts customization directly on the real Main-menu',()=>{
 const f=menu(),settings=f.tiles.Preferences;
 assert.equal(settings.name,'nv-settings-menu');assert.equal(typeof settings.click,'undefined');
 assert.deepEqual(Array.from(settings.state.Menu.items,item=>item.name),['Settings','Customize Main-menu','Whisper Mode','Overlay FPS','Renderer Mode']);
 settings.state.Menu.items.find(item=>item.id==='settings').click();settings.state.Menu.items.find(item=>item.id==='customize-main-menu').click();
 assert.deepEqual(f.routes,['main.preferences']);assert.equal(f.editStarts(),1);assert.equal(settings.state.Menu.items.find(item=>item.id==='customize-main-menu').icon,'icon-share');
 assert.equal(f.tiles.Mic.state.On.items[0].click,f.micAction);assert.equal(f.tiles.Camera.state.Off.items[0].click,f.cameraAction);assert.equal(f.tiles.Gallery.click,f.galleryAction);
});
test('Whisper Mode stays in Settings and follows a replacement native control',()=>{
 const f=menu(),items=f.tiles.Preferences.state.Menu.items,quietItem=items.find(item=>item.id==='quiet-mode');
 assert.equal(f.tiles.QuietMode2.visible,false);assert.equal(quietItem.visible,true);quietItem.click();f.synchronize();
 assert.equal(f.tiles.Preferences.state.Menu.items,items,'Synchronizing must not rebuild an open submenu');
 f.tiles.QuietMode2={visible:true,enabled:true,click:()=>f.quietCalls.push('new toggle')};f.synchronize();quietItem.click();
 assert.equal(f.tiles.QuietMode2.visible,false);assert.deepEqual(f.quietCalls,['toggle','new toggle']);
});

test('Overlay FPS opens from Settings and remains single after synchronization',()=>{
 const f=menu(),items=f.tiles.Preferences.state.Menu.items;
 const fps=items.find(item=>item.id==='overlay-fps');assert(fps&&fps.visible&&fps.enabled);fps.click();
 assert.deepEqual(f.routes,['main.osc-fps']);for(let i=0;i<3;i++)f.synchronize();
 assert.equal(f.tiles.Preferences.state.Menu.items,items);assert.equal(items.filter(item=>item.id==='overlay-fps').length,1);
});
test('Renderer Mode opens from Settings and remains unique after synchronization',()=>{
 const f=menu(),items=f.tiles.Preferences.state.Menu.items;
 const item=items.find(x=>x.id==='renderer-mode');assert(item&&item.visible&&item.enabled);item.click();
 assert.deepEqual(f.routes,['main.osc-renderer']);
 for(let i=0;i<3;i++)f.synchronize();
 assert.equal(items.filter(x=>x.id==='renderer-mode').length,1);
});
test('missing Whisper Mode disables its submenu action',()=>{
 const f=menu();delete f.tiles.QuietMode2;f.synchronize();const item=f.tiles.Preferences.state.Menu.items.find(item=>item.id==='quiet-mode');
 assert.equal(item.visible,false);assert.equal(item.enabled,false);assert.doesNotThrow(()=>item.click());
});
test('right controls may be hidden but Settings always remains visible',()=>{
 const f=menu({Performance:false,Gallery:false,Mic:false,Camera:false,Preferences:false});for(let i=0;i<3;i++)f.synchronize();
 for(const key of ['Performance','Gallery','Mic','Camera'])assert.equal(f.tiles[key].visible,false,key);assert.equal(f.tiles.Preferences.visible,true);
 assert.equal(f.tiles.GameProfiles.visible,false,'unassigned Game Profiles remains absent from legacy Main-menu');
});
test('Game Profiles is an actual native menu tile while V5 owns it and opens the existing route',()=>{
 const f=menu(undefined,{v5Owner:true}),tile=f.tiles.GameProfiles;
 assert.equal(tile.name,'nv-game-profiles-tile');assert.equal(tile.title,'Game Profiles');assert.equal(tile.icon,'icon-edit_tune');
 assert.equal(tile.visible,true);assert.equal(tile.enabled,true);assert.equal(tile.initialFocus,'false');
 assert.equal(tile.currentState,'Ready');assert.equal(tile.state.Ready.icon,'icon-edit_tune');assert.equal(tile.state.Ready.status,'');
 assert.deepEqual(Array.from(tile.state.Ready.items),[],'direct native click has no submenu commands');
 assert.equal(tile[f.context.mainMenuTileMarker],true);assert.equal(typeof tile.click,'function');
 assert.equal(V5.findControl(V5.defaultLayout(),'GameProfiles'),null,'default original twelve slots are unchanged');
 assert.ok(V5.available(V5.defaultLayout()).includes('GameProfiles'),'new control starts in the palette');
 tile.click();assert.deepEqual(f.routes,['main.gpu-profiles']);assert.equal(f.context.gameProfilesReturnState,'main.main-menu');
 assert.ok(f.nextSyncs()>0,'positioning remains delegated to the V5 owner');
});
test('Game Profiles native tile identity and click survive repeated synchronization and ownership changes',()=>{
 const f=menu(undefined,{v5Owner:true}),tile=f.tiles.GameProfiles,click=tile.click,items=f.tiles.Preferences.state.Menu.items;
 for(let i=0;i<4;i++)f.synchronize();
 assert.equal(f.tiles.GameProfiles,tile);assert.equal(tile.click,click);assert.equal(f.tiles.Preferences.state.Menu.items,items);
 assert.equal(items.some(item=>item.id==='game-profiles'),false);
 f.owner(false);f.synchronize();assert.equal(tile.visible,false,'legacy owner does not introduce an extra tile');
 f.owner(true);f.synchronize();assert.equal(tile.visible,true);assert.equal(f.tiles.GameProfiles,tile);assert.equal(tile.click,click);
 tile.click();assert.deepEqual(f.routes,['main.gpu-profiles']);
});
test('stale Game Profiles Settings rows are removed in place without replacing native actions or open menu array',()=>{
 const f=menu(undefined,{v5Owner:true}),settings=f.tiles.Preferences,items=settings.state.Menu.items,
   existing=Array.from(items),actions=existing.map(item=>item.click);
 const obsolete={id:'game-profiles',name:'Old Game Profiles',visible:true,enabled:true,click(){throw Error('retired submenu action');}};
 items.splice(1,0,obsolete);items.push({...obsolete});
 assert.equal(settings.__dulukaSettingsSubmenu,true);f.synchronize();f.synchronize();
 assert.equal(settings.state.Menu.items,items,'Angular-owned open submenu array is retained');
 assert.deepEqual(Array.from(items),existing,'every stale copy removed while other row objects keep their order');
 for(let i=0;i<existing.length;i++)assert.equal(items[i].click,actions[i]);
 assert.equal(f.tiles.GameProfiles.visible,true,'actual native Game Profiles control remains available');
});
test('native feature support controls Performance availability independently of visibility',()=>{
 const f=menu();f.context.performanceSupportAvailable=false;f.synchronize();assert.equal(f.tiles.Performance.enabled,false);assert.equal(f.tiles.Performance.visible,true);
 f.context.performanceSupportAvailable=true;f.synchronize();assert.equal(f.tiles.Performance.enabled,true);
});
test('native menu elements accept cross-panel positions without replacing action or keyboard handlers',()=>{
 const f=menu(),handlers=Object.fromEntries(Object.entries(f.nodes).map(([name,node])=>[name,node.handlers]));
 assert.equal(f.api.set({version:2,left:['Stream'],center:['Screenshot','Mods']}),true);f.synchronize();
 assert.equal(f.nodes['stream-tile'].styles.left,'-280px');assert.equal(f.nodes['stream-tile'].styles.top,'0px');assert.equal(f.nodes['stream-tile'].styles.width,'240px');assert.equal(f.nodes['stream-tile'].styles.height,'80px');
 assert.equal(f.nodes['screenshot-tile'].styles.left,'0px');assert.equal(f.nodes['screenshot-tile'].styles.width,'240px');assert.equal(f.nodes['mods-tile'].styles.left,'240px');assert.equal(f.nodes['mods-tile'].styles.height,'240px');
 assert.equal(f.tiles.Ansel.visible,false);assert.equal(f.tiles.ManualRecord.visible,false);
 for(const [name,node]of Object.entries(f.nodes))assert.equal(node.handlers,handlers[name]);
});
test('repeated synchronization does not rewrite geometry, CSS, classes or Settings items',()=>{
 const f=menu(),items=f.tiles.Preferences.state.Menu.items,before=Object.values(f.nodes).map(n=>[n.writes,n.classWrites]),style=f.document.getElementById('test-menu-style'),css=style.textContent;
 let cssWrites=0;Object.defineProperty(style,'textContent',{get:()=>css,set(){cssWrites++;}});
 for(let i=0;i<5;i++)f.synchronize();
 assert.deepEqual(Object.values(f.nodes).map(n=>[n.writes,n.classWrites]),before);assert.equal(cssWrites,0);assert.equal(f.tiles.Preferences.state.Menu.items,items);
});
test('the actual NVIDIA template receives editable faces while retaining native button handlers',()=>{
 const f=live(),delegate=[{template:nativeTemplate()}];f.decorators.nvMainMenuDirective(delegate);const template=delegate[0].template;
 assert(template.includes('nv-main-menu-live-editor'));assert(template.includes('class="nv-flex-face"'));assert(template.includes('ng-if="!flexMenu.isMovable(tile)"'));assert(template.includes('data-nv-control="{{flexMenu.key(tile)}}"'));
 assert(template.includes('ng-click="mainMenu.openMenu($mdMenu.open, $mdMenu.close, $event, tile)"'));assert(template.includes('ng-keydown="mainMenu.keyDownMenu(tile, $event)"'));assert(template.includes('ng-mouseover="mainMenu.mouseOver($event, tile)"'));
 assert(template.includes('ng-disabled="!tile.enabled && !(flexMenu.editing && flexMenu.isMovable(tile))"'));assert(template.includes('ng-if="flexMenu.editing"'));assert(!template.includes('nv-native-menu-preview'));assert(!template.includes('preferences-root-panel'));
});
test('Settings icon and label exist in the first native template render',()=>{
 const f=live(),delegate=[{template:nativeTemplate()}];f.decorators.nvMainMenuDirective(delegate);
 assert(delegate[0].template.includes('ng-switch-when=preferences-tile><div layout=row'));assert(delegate[0].template.includes('ng-switch-when=nv-settings-menu>'));
 assert.equal((delegate[0].template.match(/<p class="gallery" translate="l10n.settings">Settings<\/p>/g)||[]).length,2);assert(!gpuSource.includes('settingsButton.innerHTML'));
});
test('the old customization URL opens live editing without a separate preview page',()=>{
 const f=live(),routes=[],state=f.states['main.customize-main-menu'];assert.equal(typeof state.template,'undefined');state.onEnter.at(-1)({go:name=>routes.push(name)});assert.deepEqual(routes,['main.main-menu']);
});
function interaction(){
 const rootEvents={},documentEvents={},windowEvents={},destroy=[],classNames=new Set(),ghosts=[],timers=[];
 const root={addEventListener:(n,fn)=>{rootEvents[n]=fn;},removeEventListener:n=>{delete rootEvents[n];},classList:{add:n=>classNames.add(n),remove:n=>classNames.delete(n)},querySelector:()=>null};
 const document={addEventListener:(n,fn)=>{documentEvents[n]=fn;},removeEventListener:n=>{delete documentEvents[n];},createElement:()=>({style:{},remove(){this.removed=true;}}),body:{appendChild:node=>ghosts.push(node)},elementFromPoint:()=>document.target};
 const window={addEventListener:(n,fn)=>{windowEvents[n]=fn;},removeEventListener:n=>{delete windowEvents[n];},setTimeout:fn=>{timers.push(fn);return timers.length;},clearTimeout(){}},f=live(window,document),definition=f.directives.nvMainMenuLiveEditor(window,[document]);
 const scope={$evalAsync:fn=>fn(),$on:(n,fn)=>{if(n==='$destroy')destroy.push(fn);}},editor={};
 const translated={'l10n.settings':'การตั้งค่า','l10n.camera':'กล้อง','l10n.microphone':'ไมโครโฟน'};
 const filter=name=>{assert.equal(name,'translate');return key=>translated[key]||key;};
 definition.controller.at(-1).call(editor,scope,filter);definition.link(scope,[root]);
 function target(key,group,index,isTile=true){
  const attributes={'data-menu-key':key,'data-nv-control':key,'data-menu-group':group,'data-menu-index':String(index)},node={classList:{add(){},remove(){}},getAttribute:n=>attributes[n]};
  node.closest=sel=>{if(sel==='.nv-live-remove'||sel==='.nv-live-drop'||sel==='md-menu-content')return null;if(sel==='[data-menu-key][data-menu-group]')return group&&!isTile?node:null;if(sel==='md-menu'||sel==='md-menu[data-nv-control]')return isTile?node:null;if(sel==='[data-menu-key],md-menu[data-nv-control]')return node;return null;};return node;
 }
 function event(node,extra){return Object.assign({target:node,button:0,clientX:10,clientY:10,prevented:false,stopped:false,preventDefault(){this.prevented=true;},stopImmediatePropagation(){this.stopped=true;}},extra);}
 return Object.assign(f,{editor,rootEvents,documentEvents,windowEvents,target,event,document,ghosts,classNames,destroy});
}
test('editing captures tile mouse and keyboard activation before recording commands can run',()=>{
 const f=interaction(),tile=f.target('ManualRecord');let commands=0;function activate(event,handler){handler(event);if(!event.stopped)commands++;}
 f.editor.editing=true;const click=f.event(tile);activate(click,f.rootEvents.click);assert.equal(commands,0);assert.equal(click.prevented,true);assert.equal(f.editor.selected,'ManualRecord');
 for(const key of ['Enter',' ']){const e=f.event(tile,{key});activate(e,f.rootEvents.keydown);assert.equal(e.prevented,true);}assert.equal(commands,0);
 f.editor.editing=false;activate(f.event(tile),f.rootEvents.click);assert.equal(commands,1,'Normal Main-menu must retain its native action');
});
test('dragging between actual native tiles commits one swap and suppresses the release click',()=>{
 const f=interaction(),source=f.target('Mods'),destination=f.target('ManualRecord');f.editor.editing=true;let commits=0;f.api.subscribe(()=>{commits++;});
 f.rootEvents.mousedown(f.event(source));f.document.target=destination;f.documentEvents.mousemove(f.event(destination,{clientX:60,clientY:20}));
 assert.equal(f.classNames.has('nv-live-dragging'),true);assert.equal(f.ghosts.length,1);f.documentEvents.mouseup();assert.equal(commits,1);
 assert.deepEqual(plain(f.api.get()),{version:2,left:['Screenshot','Ansel','ManualRecord'],center:['InstantReplay','Mods','Stream','Broadcast']});
 assert.equal(f.ghosts[0].removed,true);assert.equal(f.classNames.has('nv-live-dragging'),false);const click=f.event(source);f.rootEvents.click(click);assert.equal(click.stopped,true);assert.equal(f.editor.selected,'');
});
test('Escape cancels a drag and destruction removes every installed input listener',()=>{
 const f=interaction(),tile=f.target('Mods'),before=plain(f.api.get());f.editor.editing=true;f.rootEvents.mousedown(f.event(tile));f.document.target=f.target('ManualRecord');f.documentEvents.mousemove(f.event(tile,{clientX:40}));
 const escape=f.event(tile,{key:'Escape'});f.rootEvents.keydown(escape);f.documentEvents.mouseup();assert.equal(escape.stopped,true);assert.deepEqual(plain(f.api.get()),before);assert.equal(f.ghosts[0].removed,true);
 f.destroy.forEach(fn=>fn());assert.deepEqual(Object.keys(f.rootEvents),[]);assert.deepEqual(Object.keys(f.documentEvents),[]);assert.deepEqual(Object.keys(f.windowEvents),[]);
});


test('center rail shrinks without resizing tiles, recentering locked right Settings', () => {
  const f=menu();
  const originalQuery=f.root.querySelector;
  const rail=styledNode('md-menu-bar');
  f.root.querySelector=selector=>selector==='md-menu-bar'?rail:originalQuery(selector);
  const full=f.api.get();
  for (let count=4;count>=0;count--) {
    assert.equal(f.api.set({version:2,left:full.left,center:full.center.slice(0,count)}),true);
    f.synchronize();
    assert.equal(rail.styles.width,(240*count)+'px');
    assert.equal(rail.styles['min-width'],(240*count)+'px');
    assert.equal(rail.styles.transform,'translateX('+(count===0?500:(4-count)*120)+'px)');
    const controls=f.api.cells(f.api.get()).filter(x=>x.group==='center');
    assert(controls.every((c,i)=>c.width===240&&c.x===i*240&&c.height===240));
    assert.equal(f.tiles.Preferences.name,'nv-settings-menu');
    assert.equal(f.tiles.Preferences.visible,true);
    assert.equal(f.api.remove('Preferences'),false);
    assert.equal(f.api.move('Preferences','center',0),false);
    assert.equal(f.api.remove('Settings'),false);
    assert.equal(f.api.move('Settings','left',0),false);
  }
  assert.equal(f.api.set({version:2,left:[],center:[],right:[]}),false);
  assert.equal(f.api.set({version:2,left:['Settings'],center:[]}),false);
  assert.equal(f.tiles.Preferences.state.Menu.items[0].id,'settings');
});

test('initial native menu anchor ignores a temporary x=0 root position', () => {
  const f=menu(),rail=styledNode('md-menu-bar'),original=f.root.querySelector;
  f.root.querySelector=sel=>sel==='md-menu-bar'?rail:original(sel);
  f.context.window.innerWidth=1920;
  f.root.getBoundingClientRect=()=>({left:0,width:0});
  f.synchronize();
  assert.equal(rail.styles.transform,'translateX(0px)','zero-sized Angular view must not shift native controls');
  f.root.getBoundingClientRect=()=>({left:0,width:1040});
  f.synchronize();
  assert.equal(rail.styles.transform,'translateX(0px)','must not follow transient x=0 root');
  f.root.getBoundingClientRect=()=>({left:440,width:1040});
  f.synchronize();
  assert.equal(rail.styles.transform,'translateX(0px)','settled CSS position must not move the rail');
  assert.equal(f.api.set({version:2,left:[],center:[]}),true);
  f.synchronize();
  assert.equal(rail.styles.transform,'translateX(360px)','Settings-only view must center using stable root width');
});

test('native Customize remove buttons use the NVIDIA E91A app close glyph',()=>{
  const f=live(),delegate=[{template:nativeTemplate()}];
  f.decorators.nvMainMenuDirective(delegate);
  const output=delegate[0].template;
  assert.equal((output.match(/share-icon icon-app_close/g)||[]).length,3,'Left, Right and Center remove buttons must use NVIDIA icon-app_close');
  assert(!output.includes('>×</button>'),'remove controls must not render the Unicode multiplication sign');
  assert(output.includes("ng-if=\"key!=='Settings'\"")||output.includes("key!=='Settings'"),'Settings remains protected from removal');
  const css=fs.readFileSync(path.join(mods,'main-menu-live.css'),'utf8');
  assert(css.includes('.nv-live-remove .share-icon'));
  assert(gpuSource.includes('nv-settings-menu-tile'));
});

test('compact Controls tray is a single horizontal row with NVIDIA E909 collapse and mouse-wheel scrolling',()=>{
 const f=live(),delegate=[{template:nativeTemplate()}];
 f.decorators.nvMainMenuDirective(delegate);
 const output=delegate[0].template;
 assert(output.includes('class="nv-live-library-toggle"'),'Controls header has a collapse toggle');
 assert(output.includes('icon-chevron_down'),'toggle uses the NVIDIA uniE909 glyph');
 assert(liveSource.includes("closest(event.target,'.nv-live-library-toggle')"),'toggle uses native capture dispatch to avoid losing clicks during digest');
 assert(liveSource.includes('editor().libraryCollapsed=!editor().libraryCollapsed'),'toggle updates the existing Angular editor state');
 assert(output.includes('ng-show="!flexMenu.libraryCollapsed"'),'palette visibility follows toggle state');
 assert(liveSource.includes('self.libraryCollapsed=false'),'default tray is open');
 assert(liveSource.includes("tray.scrollLeft+=event.deltaY"),'wheel scrolls the horizontal tray');
 assert(output.includes('flexMenu.extraRows()'),'legacy side-row offset still exists');
 const css=fs.readFileSync(path.join(mods,'main-menu-live.css'),'utf8');
 assert.match(css,/\.nv-live-workspace\s*\{\s*display:grid/);
 assert.match(css,/\.nv-live-library\s*\{\s*grid-column:1\s*\/\s*-1/);
 assert.match(css,/\.nv-live-library \.nv-live-palette\s*\{[^}]*flex-wrap:nowrap/);
 assert.match(css,/\.nv-live-library \.nv-live-palette\s*\{[^}]*overflow-x:auto/);
 assert.match(css,/\.nv-live-library \.nv-live-palette button\s*\{[^}]*flex:0 0 160px/);
 assert(output.includes("key!=='Settings'"),'protected Settings remains in the template');
 assert.match(css,/html \.nv-live-editor\s*\{[^}]*left:calc\(50% - 760px\)!important/,'Customize overrides the BG extension only for centered editor geometry');
});

test('Customize control labels use OSC locale keys without changing NVIDIA glyphs',()=>{
 const f=interaction();
 assert.equal(f.editor.label('Settings'),'การตั้งค่า');
 assert.equal(f.editor.label('Camera'),'กล้อง');
 assert.equal(f.editor.label('Mic'),'ไมโครโฟน');
 assert.equal(f.editor.label('Screenshot'),'Screenshot','Missing key uses native English fallback');
 const template=nativeTemplate(),delegate=[{template}];f.decorators.nvMainMenuDirective(delegate);
 assert.match(delegate[0].template,/translate="l10n\.settings"/);
 assert.match(delegate[0].template,/class="share-icon icon48 icon-normal icon-settings"/);
});
