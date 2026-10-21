'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const mods=path.join(__dirname,'../osc/mods'),liveSource=fs.readFileSync(path.join(mods,'main-menu-live.js'),'utf8'),gpuSource=fs.readFileSync(path.join(mods,'gpu-profiles.js'),'utf8');
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
function menu(visibility){
 const routes=[],quietCalls=[],nodes={},styles={},micAction=()=>{},cameraAction=()=>{},galleryAction=()=>{};
 const tiles={Performance:{name:'performance-tile',visible:true,enabled:true},Gallery:{name:'gallery-tile',visible:true,click:galleryAction},Mic:{name:'mic-tile',visible:true,state:{On:{items:[{click:micAction}]}}},Camera:{name:'camera-tile',visible:true,state:{Off:{items:[{click:cameraAction}]}}},QuietMode2:{name:'quietmode2-tile',visible:true,enabled:true,click:()=>quietCalls.push('toggle')},Preferences:{name:'preferences-tile',visible:true,enabled:true,click:()=>{throw Error('Settings must open its submenu');}}};
 const scope={mainMenu:{tiles},digests:0,$applyAsync(){this.digests++;}},root={querySelector:sel=>nodes[sel.replace('md-menu.','')]||null};
 const document={documentElement:{},querySelector:()=>root,getElementById:id=>styles[id]||null,createElement:()=>({textContent:'',parentNode:null}),head:{appendChild(style){styles[style.id]=style;style.parentNode=this;}}};
 let editStarts=0;
 const window={angular:{element:el=>el===root?{scope:()=>scope}:{injector:()=>({get:()=>({go:route=>routes.push(route)})})}}},f=live(window,document);
 f.api.items.forEach(item=>{nodes[item.name]=styledNode(item.name);tiles[item.key]={name:item.name,visible:true,enabled:true,click(){},state:{}};});
 window.__dulukaStartMainMenuEditor=()=>{editStarts++;};if(visibility)window.__dulukaMainMenuVisibility=visibility;
 const context={window,document,mainMenuTileStyleId:'test-menu-style',mainMenuTileMarker:'test-marker',performanceSupportAvailable:null,liveLayoutUnsubscribe:null,openGameProfilesFromMainMenu:()=>routes.push('main.gpu-profiles')};
 vm.createContext(context);const start=gpuSource.indexOf('  function ensureMainMenuTileStyle()'),end=gpuSource.indexOf('  function registerPerformanceSupportListener()',start);vm.runInContext(gpuSource.slice(start,end),context);
 const synchronize=()=>vm.runInContext('updateMainMenuPerformance()',context);synchronize();
 return Object.assign(f,{routes,tiles,nodes,root,scope,quietCalls,context,synchronize,micAction,cameraAction,galleryAction,editStarts:()=>editStarts});
}
function nativeTemplate(){
 const bundle=fs.readFileSync(path.join(__dirname,'../osc/app.js'),'utf8'),position=bundle.indexOf('main-menu-container" ng-cloak'),start=bundle.lastIndexOf('e.exports=',position)+'e.exports='.length,end=bundle.indexOf('},function',start);
 assert(position>=0&&start>=10&&end>start,'The bundled native menu template must be found');
 return vm.runInNewContext(bundle.slice(start,end),{n:()=>'/native-performance.svg'});
}
test('Settings keeps native actions and starts customization directly on the real Main-menu',()=>{
 const f=menu(),settings=f.tiles.Preferences;
 assert.equal(settings.name,'nv-settings-menu');assert.equal(typeof settings.click,'undefined');
 assert.deepEqual(Array.from(settings.state.Menu.items,item=>item.name),['Settings','Customize Main-menu','Game Profiles','Whisper Mode']);
 settings.state.Menu.items[0].click();settings.state.Menu.items[1].click();settings.state.Menu.items[2].click();
 assert.deepEqual(f.routes,['main.preferences','main.gpu-profiles']);assert.equal(f.editStarts(),1);assert.equal(settings.state.Menu.items[1].icon,'icon-share');
 assert.equal(f.tiles.Mic.state.On.items[0].click,f.micAction);assert.equal(f.tiles.Camera.state.Off.items[0].click,f.cameraAction);assert.equal(f.tiles.Gallery.click,f.galleryAction);
});
test('Whisper Mode stays in Settings and follows a replacement native control',()=>{
 const f=menu(),items=f.tiles.Preferences.state.Menu.items,quietItem=items[3];
 assert.equal(f.tiles.QuietMode2.visible,false);assert.equal(quietItem.visible,true);quietItem.click();f.synchronize();
 assert.equal(f.tiles.Preferences.state.Menu.items,items,'Synchronizing must not rebuild an open submenu');
 f.tiles.QuietMode2={visible:true,enabled:true,click:()=>f.quietCalls.push('new toggle')};f.synchronize();quietItem.click();
 assert.equal(f.tiles.QuietMode2.visible,false);assert.deepEqual(f.quietCalls,['toggle','new toggle']);
});
test('missing Whisper Mode disables its submenu action',()=>{
 const f=menu();delete f.tiles.QuietMode2;f.synchronize();const item=f.tiles.Preferences.state.Menu.items[3];
 assert.equal(item.visible,false);assert.equal(item.enabled,false);assert.doesNotThrow(()=>item.click());
});
test('all five fixed right controls stay visible despite obsolete saved hide preferences',()=>{
 const f=menu({Performance:false,Gallery:false,Mic:false,Camera:false,Preferences:false});for(let i=0;i<3;i++)f.synchronize();
 for(const key of ['Performance','Gallery','Mic','Camera','Preferences'])assert.equal(f.tiles[key].visible,true,key);
 assert.equal(f.tiles.GameProfiles,undefined);
});
test('native feature support controls Performance availability independently of visibility',()=>{
 const f=menu();f.context.performanceSupportAvailable=false;f.synchronize();assert.equal(f.tiles.Performance.enabled,false);assert.equal(f.tiles.Performance.visible,true);
 f.context.performanceSupportAvailable=true;f.synchronize();assert.equal(f.tiles.Performance.enabled,true);
});
test('native menu elements accept cross-panel positions without replacing action or keyboard handlers',()=>{
 const f=menu(),handlers=Object.fromEntries(Object.entries(f.nodes).map(([name,node])=>[name,node.handlers]));
 assert.equal(f.api.set({version:2,left:['Stream'],center:['Screenshot','Mods']}),true);f.synchronize();
 assert.equal(f.nodes['stream-tile'].styles.left,'-280px');assert.equal(f.nodes['stream-tile'].styles.top,'0px');assert.equal(f.nodes['stream-tile'].styles.width,'240px');assert.equal(f.nodes['stream-tile'].styles.height,'240px');
 assert.equal(f.nodes['screenshot-tile'].styles.left,'0px');assert.equal(f.nodes['screenshot-tile'].styles.width,'480px');assert.equal(f.nodes['mods-tile'].styles.left,'480px');assert.equal(f.nodes['mods-tile'].styles.height,'240px');
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
 assert.equal((delegate[0].template.match(/<p class=gallery>Settings<\/p>/g)||[]).length,2);assert(!gpuSource.includes('settingsButton.innerHTML'));
});
test('the old customization URL opens live editing without a separate preview page',()=>{
 const f=live(),routes=[],state=f.states['main.customize-main-menu'];assert.equal(typeof state.template,'undefined');state.onEnter.at(-1)({go:name=>routes.push(name)});assert.deepEqual(routes,['main.main-menu']);
});
function interaction(){
 const rootEvents={},documentEvents={},windowEvents={},destroy=[],classNames=new Set(),ghosts=[],timers=[];
 const root={addEventListener:(n,fn)=>{rootEvents[n]=fn;},removeEventListener:n=>{delete rootEvents[n];},classList:{add:n=>classNames.add(n),remove:n=>classNames.delete(n)},querySelector:()=>null};
 const document={addEventListener:(n,fn)=>{documentEvents[n]=fn;},removeEventListener:n=>{delete documentEvents[n];},createElement:()=>({style:{},remove(){this.removed=true;}}),body:{appendChild:node=>ghosts.push(node)},elementFromPoint:()=>document.target};
 const window={addEventListener:(n,fn)=>{windowEvents[n]=fn;},removeEventListener:n=>{delete windowEvents[n];},setTimeout:fn=>{timers.push(fn);return timers.length;},clearTimeout(){}},f=live(window,document),definition=f.directives.nvMainMenuLiveEditor(window,[document]);
 const scope={$evalAsync:fn=>fn(),$on:(n,fn)=>{if(n==='$destroy')destroy.push(fn);}},editor={};definition.controller.at(-1).call(editor,scope);definition.link(scope,[root]);
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

