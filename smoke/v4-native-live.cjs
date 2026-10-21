'use strict';
// Temporary in-memory test against a running, authorized NVIDIA OSC CEF tab.
// Restores previous V4 storage keys and reloads CEF in all exit paths.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const base=path.resolve(__dirname,'../Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods')+path.sep;
const reportPath=path.join(__dirname,'v4-native-live-results.json');
const screenshotPath=path.join(__dirname,'v4-native-editor-cef.png');
const assets=['main-menu-unified-model.js','main-menu-unified-native.js'].map(p=>fs.readFileSync(base+p,'utf8'));
const css=fs.readFileSync(base+'main-menu-unified-native.css','utf8');
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
let ws,seq=0,ready=false;
const pending=new Map();
async function open(){
  const tabs=await(await fetch('http://127.0.0.1:59099/json/list',{signal:AbortSignal.timeout(1500)})).json();
  const page=tabs.find(t=>t.type==='page'&&String(t.url).includes('59011'));
  if(!page)throw Error('OSC CEF page is not exposed at 59099');
  ws=new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject});
  ws.onmessage=e=>{const m=JSON.parse(e.data),p=pending.get(m.id);if(p){pending.delete(m.id);p(m)}};
  ready=true;
}
function send(method,params={}){
  return new Promise((resolve,reject)=>{
    let id=++seq,timeout=setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout '+method))},8000);
    pending.set(id,m=>{clearTimeout(timeout);if(m.error||m.result?.exceptionDetails)reject(Error(JSON.stringify(m.error||m.result.exceptionDetails)));else resolve(m.result)});
    ws.send(JSON.stringify({id,method,params}));
  });
}
async function evaluate(expression,awaitPromise=false){
  const r=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise});
  return r.result?.value;
}
async function screenshot(p){
  let r=await send('Page.captureScreenshot',{format:'png'});
  if(!r.data)throw Error('No rendered screenshot');
  fs.writeFileSync(p,Buffer.from(r.data,'base64'));
}
(async()=>{
  await open();
  const before=await evaluate("(function(){var r=angular.element(document.documentElement).injector().get('$state');return {route:r.current.name,flag:localStorage.getItem('DulukaMainMenuUnifiedV4Enabled'),data:localStorage.getItem('DulukaMainMenuUnifiedV4'),legacy:localStorage.getItem('DulukaMainMenuLayoutV2'),record:window.__nvRecordedTestState||null};})()");
  let report={before:{route:before.route,hasV4Before:before.flag!==null,hadPersistedModel:before.data!==null},checks:[],restored:false};
  try{
    if(before.route!=='base')throw Error('OSC not idle at base; refusing to disturb active UI');
    for(const s of assets)await evaluate(s);
    await evaluate("(function(){var tag=document.createElement('style');tag.id='nv-unified-v4-smoke-style';tag.textContent="+JSON.stringify(css)+";document.head.appendChild(tag);return !!window.__dulukaUnifiedMainMenu;})()");
    let enabled=await evaluate("(function(){return __dulukaUnifiedMainMenu.setEnabled(true);})()");
    assert.equal(enabled,true);
    await evaluate("angular.element(document.documentElement).injector().get('$state').go('main.main-menu').then(function(){return true;})",true);
    await sleep(1150);
    const collect=`(function(){
      var root=document.querySelector('nv-main-menu .main-menu-container'),u=window.__dulukaUnifiedMainMenu;
      if(!root)return {error:'Main-menu not mounted'};
      var g=u.nativeGeometry(root),actual=[],bar=root.querySelector('md-menu-bar');
      g.cells.forEach(function(cell){
        var cls=u.model.CONTROLS.filter(function(c){return c.key===cell.key;})[0].name;
        var el=root.querySelector('md-menu.'+cls);
        if(!el)return;
        var r=el.getBoundingClientRect();
        actual.push({key:cell.key,rect:{x:r.x,y:r.y,w:r.width,h:r.height},
          nativeButton:!!el.querySelector('button.menu-button'),sameMenuTag:el.tagName.toLowerCase()==='md-menu'});
      });
      var b=bar.getBoundingClientRect();
      return {route:location.hash,motion:window.__dulukaOscMotion&&window.__dulukaOscMotion.phase,count:actual.length,scale:g.scale,
        root:(function(){var r=root.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height};})(),
        barInline:bar.getAttribute('style'),barComputedTransform:getComputedStyle(bar).transform,
        barOffsetLeft:bar.offsetLeft,barParent:(function(){var r=bar.parentElement.getBoundingClientRect();return {x:r.x,w:r.width,transform:getComputedStyle(bar.parentElement).transform};})(),
        geometry:{x:b.x,y:b.y,w:b.width,h:b.height},
        tiles:actual,window:{w:innerWidth,h:innerHeight},
        fit:actual.every(function(t){return t.rect.x>=-1&&t.rect.y>=-1&&t.rect.x+t.rect.w<=innerWidth+1&&t.rect.y+t.rect.h<=innerHeight+1})};
    })()`;
    var first=await evaluate(collect);
    if(first.error)throw Error(first.error);
    report.checks.push({name:'legacy-migrated-12-native',count:first.count,fit:first.fit,bar:first.geometry,scale:first.scale});
    assert.equal(first.count,12,'Expected 12 original native NVIDIA md-menus');
    assert(first.tiles.every(t=>t.nativeButton&&t.sameMenuTag));
    assert(first.fit,JSON.stringify(first));
    const modelChange=`(function(){
      var u=window.__dulukaUnifiedMainMenu,M=u.model;
      var s=u.get();
      s=M.toBlock(s,'Settings','center',0,'big');
      M.CONTROLS.filter(function(c){return c.key!=='Settings'}).slice(0,8).forEach(function(c){s=M.join(s,c.key,'center',0);});
      u.set(s);
      return {center:s.panels.center[0],settings:M.locate(s,'Settings')};
    })()`;
    const grouped=await evaluate(modelChange);
    assert.equal(grouped.center.controls.length,9);
    assert.equal(grouped.settings.panel,'center');
    await sleep(180);
    let second=await evaluate(collect);
    report.checks.push({name:'9-in-big-center',count:second.count,fit:second.fit,scale:second.scale,bar:second.geometry,group:grouped.center.controls});
    assert.equal(second.count,12);assert(second.fit,JSON.stringify(second));
    await evaluate("(function(){window.__dulukaStartMainMenuEditor();return true;})()");
    await sleep(320);
    const editor=await evaluate(`(function(){
      var u=__dulukaUnifiedMainMenu,e=u.openEditor();
      if(!e)return {error:'Editor missing'};
      return {panels:e.querySelectorAll('.nv-unified-panel').length,
        nativeButtons:document.querySelectorAll('md-menu > button.menu-button').length,
        nativeContainer:!!document.querySelector('nv-main-menu .main-menu-container'),
        types:e.querySelectorAll('select[data-v4-type]').length,
        grouped:e.querySelectorAll('.nv-unified-member').length,
        settingsRemove:e.querySelectorAll('[data-v4-remove="Settings"]').length,
        background:!!e.querySelector('nv-osc-background-settings'),
        v2Hidden:getComputedStyle(e.querySelector('.nv-live-workspace')).display==='none'};
    })()`);
    if(editor.error)throw Error(editor.error);
    report.checks.push({name:'v4-editor-native',...editor});
    assert.equal(editor.panels,3);
    assert.equal(editor.grouped,12);
    assert.equal(editor.settingsRemove,0);
    assert.equal(editor.v2Hidden,true);
    // Current running Build still has V2 gpu-profiles/main-menu-live.js.
    // Emulate the two feature-branch hooks in-memory for the visual capture.
    await evaluate("(function(){var root=document.querySelector('nv-main-menu .main-menu-container'),s=angular.element(root).scope();while(s&&!s.flexMenu)s=s.$parent;if(s&&s.flexMenu){s.flexMenu.variant=function(tile){var key=this.key(tile),u=window.__dulukaUnifiedMainMenu,c=u.cell(key);return c?c.variant:'row';};s.$applyAsync();}return true;})()");
    await sleep(130);
    await evaluate("(function(){var r=document.querySelector('nv-main-menu .main-menu-container');return __dulukaUnifiedMainMenu.nativeGeometry(r).cells.length;})()");
    await sleep(65);
    await screenshot(screenshotPath);
    report.screenshot=screenshotPath;
    console.log('LIVE_V4_PASS',JSON.stringify(report.checks));
  }finally{
    try{
      await evaluate("(function(){var s=angular.element(document.documentElement).injector().get('$state');s.go('base');var t=document.getElementById('nv-unified-v4-smoke-style');if(t)t.remove();return true;})()");
      await evaluate("(function(){var previous="+JSON.stringify({flag:before.flag,data:before.data})+";if(previous.flag===null)localStorage.removeItem('DulukaMainMenuUnifiedV4Enabled');else localStorage.setItem('DulukaMainMenuUnifiedV4Enabled',previous.flag);if(previous.data===null)localStorage.removeItem('DulukaMainMenuUnifiedV4');else localStorage.setItem('DulukaMainMenuUnifiedV4',previous.data);return true;})()");
      report.restored=await evaluate("(function(){return localStorage.getItem('DulukaMainMenuUnifiedV4Enabled')==="+JSON.stringify(before.flag)+"&&localStorage.getItem('DulukaMainMenuUnifiedV4')==="+JSON.stringify(before.data)+";})()");
      // Reload destroys in-memory opt-in adapter; the real OSC returns to V2.
      await send('Page.reload',{ignoreCache:true});
      console.log('OSC_V2_RESTORED',report.restored);
    }catch(error){console.error('RESTORE_WARNING',error.message);process.exitCode=1;}
    fs.writeFileSync(reportPath,JSON.stringify(report,null,2));
    if(ws)ws.close();
  }
})().catch(error=>{console.error('V4_SMOKE_ERROR',error.stack||error);process.exitCode=1});
