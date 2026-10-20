'use strict';
const fs = require('fs'), assert = require('assert');
const out = 'build_test/NVIDIA FilterEntry_Test/';
async function main() {
  const pages = await (await fetch('http://127.0.0.1:59099/json/list')).json();
  const page = pages.find(p => p.url.includes('localhost:59011'));
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let id = 0;
  const waiting = new Map(), errors = [], requests = [];
  ws.onmessage = event => {
    const msg = JSON.parse(event.data);
    if (waiting.has(msg.id)) { waiting.get(msg.id)(msg); waiting.delete(msg.id); }
    else if (msg.method === 'Runtime.exceptionThrown') errors.push(msg.params.exceptionDetails.text);
    else if (msg.method === 'Network.requestWillBeSent' && /NvCamera/.test(msg.params.request.url))
      requests.push({url:msg.params.request.url,method:msg.params.request.method});
  };
  const call = (method, params = {}) => new Promise(resolve => {
    const n = ++id; waiting.set(n, resolve); ws.send(JSON.stringify({id:n,method,params}));
  });
  async function run(expression) {
    const msg = await call('Runtime.evaluate', {expression,returnByValue:true,awaitPromise:true});
    if (msg.error || msg.result.exceptionDetails) throw Error(JSON.stringify(msg));
    return msg.result.result.value;
  }
  const runtime = async () => (await fetch('http://127.0.0.1:59011/Duluka/v.1.0/GameFilter/State')).json();
  const delay = `function pause(ms){return new Promise(function(r){setTimeout(r,ms);});}`;
  const open = `(async function(){${delay}var i=angular.element(document).injector(),c=i.get('nvCameraService');
    await i.get('$rootScope').$apply(function(){return c.launchUIForMods();});
    for(var n=0;n<50;n++){var e=document.querySelector('mods-menu'),k=e&&angular.element(e).controller('modsMenu');
      if(k&&k.slotFocus!==0&&c.__dulukaModsPrepared===c.__dulukaModsRuntime&&c.__dulukaModsPrepared)return true;
      await pause(100);}throw Error('Editor did not finish preparing');})()`;
  const snapshot = `(function(){var i=angular.element(document).injector(),c=i.get('nvCameraService'),e=document.querySelector('mods-menu'),k=e&&angular.element(e).controller('modsMenu'),game=c.__dulukaModsExecutable;
    return {route:location.hash,runtime:c.__dulukaModsRuntime,prepared:c.__dulukaModsPrepared,active:c.modsActiveSlot?c.modsActiveSlot.id:null,
      slotFocus:k&&k.slotFocus===undefined?'off':k&&k.slotFocus,offActive:!!(e&&e.querySelectorAll('.mods-slot')[3].classList.contains('active')),
      catalogs:c.modsSlots.map(function(s){return (s.filters||[]).map(function(f){return f.title;});}),
      choices:JSON.parse(localStorage.getItem('DulukaModsSelectionStorage')||'{}')[game],
      nativeData:JSON.parse(localStorage.getItem('ModsDataStorage')||'{}')[game],
      storedPresets:JSON.parse(localStorage.getItem('ModsSlotStorage')||'{}')[game]&&JSON.parse(localStorage.getItem('ModsSlotStorage')||'{}')[game].length};})()`;
  async function screenshot(name) {
    const msg = await call('Page.captureScreenshot',{format:'png'});
    fs.writeFileSync(out+name,Buffer.from(msg.result.data,'base64'));
  }
  try {
    await call('Runtime.enable'); await call('Network.enable');
    const before = await runtime();
    assert(before.connected && before.filters.length > 0);
    await run(open);
    const cold = await run(snapshot);
    assert(cold.catalogs.every(filters => filters.length === before.filters.length));
    await run(`document.querySelector('mods-menu').querySelectorAll('.mods-slot')[1].click();new Promise(function(r){setTimeout(r,350);})`);
    const selected = await run(snapshot);
    assert.strictEqual(selected.active,2); assert.strictEqual(selected.choices,1);
    assert(selected.nativeData.modsIsEnabled && selected.nativeData.lastSlot===1);
    assert.strictEqual(await run(`angular.element(document.querySelector('mods-menu')).controller('modsMenu').filters.length`),before.filters.length);
    await screenshot('catalog-populated.png');
    await run(`document.querySelector('#filter-dropdown').click();new Promise(function(r){setTimeout(r,150);})`);
    const available = await run(`Array.from(document.querySelectorAll('.md-select-menu-container.md-active md-option')).map(function(e){return e.textContent.trim();})`);
    assert.deepStrictEqual(available,before.filters.map(f=>f.name));
    await screenshot('catalog-options.png');
    await call('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await call('Input.dispatchKeyEvent',{type:'keyUp',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await run(`document.querySelector('mods-menu').querySelectorAll('.mods-slot')[3].click();new Promise(function(r){setTimeout(r,350);})`);
    const off = await run(snapshot), offRuntime = await runtime();
    assert(off.offActive && off.active===null && off.choices===-1 && off.nativeData.modsIsEnabled===false);
    assert.deepStrictEqual(offRuntime.stack,[]);
    await run(`document.querySelector('mods-menu .mods-close-button').click();new Promise(function(r){setTimeout(r,700);})`);
    await run(open);
    const reopened = await run(snapshot);
    assert(reopened.offActive && reopened.choices===-1 && reopened.nativeData.modsIsEnabled===false);
    await screenshot('off-reopened.png');
    fs.writeFileSync(out+'live-before-reload.json',JSON.stringify({before:{pid:before.pid,session:before.session,generation:before.generation,filters:before.filters.map(f=>f.name)},cold,selected,available,off,reopened,errors,requests},null,2));
    assert.deepStrictEqual(errors,[]);
    console.log(JSON.stringify({cold,selected,available,off,reopened,errors},null,2));
  } finally { ws.close(); }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
