// Isolated API mocks, real NVIDIA Angular components and Chromium rendering.
// This never posts settings to the game/backend and always removes its UI.
const fs = require('fs');
const assert = require('assert');
const path = require('path');
const base = 'Project/Overlay On-Screen Control/NVIDIA NodeAPI';
const {createFilterSettingsModel,filterSettingsTemplate} = require(path.resolve(base,'osc/mods/gpu-status-panel.js'));
const css = fs.readFileSync(path.join(base,'osc/mods/gpu-status-panel.css'),'utf8');
const out = path.resolve('build_test/NVIDIA FreestyleSettings_Test');
const results = [];
(async () => {
  const pages = await (await fetch('http://127.0.0.1:59099/json/list')).json();
  const page = pages.find(p => p.url.indexOf('localhost:59011') >= 0);
  assert(page,'OSC debugging page is not available');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve,reject) => {ws.onopen=resolve;ws.onerror=reject;});
  let seq=0; const pending=new Map();
  ws.onmessage=e=>{const r=JSON.parse(e.data);if(pending.has(r.id)){pending.get(r.id)(r);pending.delete(r.id);}};
  function call(method,params={}) {return new Promise(resolve=>{const id=++seq;pending.set(id,resolve);ws.send(JSON.stringify({id,method,params}));});}
  async function run(expression) {
    const r=await call('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});
    if(r.error || r.result.exceptionDetails) throw Error(JSON.stringify(r));
    return r.result.result.value;
  }
  async function shot(name) {
    const r=await call('Page.captureScreenshot',{format:'png'});
    if(r.error)throw Error(JSON.stringify(r.error));
    fs.writeFileSync(path.join(out,name+'.png'),Buffer.from(r.result.data,'base64'));
  }
  const initial = await (await fetch('http://127.0.0.1:59011/Duluka/v.1.0/GameFilter/Settings')).json();
  try {
    await run(`(()=>{
      var settingsUrl='/Duluka/v.1.0/GameFilter/Settings';
      var make=${createFilterSettingsModel.toString()};
      var injector=angular.element(document).injector(),scope=injector.get('$rootScope').$new(true);
      var stage=document.createElement('section');stage.id='nv-filter-settings';
      stage.setAttribute('data-freestyle-qa','true');
      stage.style.cssText='position:fixed;left:70px;right:314px;top:140px;bottom:70px;padding:30px 40px;background:#24292c!important;color:white;z-index:50000;overflow:auto;';
      stage.innerHTML=${JSON.stringify(filterSettingsTemplate)};
      var style=document.createElement('style');style.textContent=${JSON.stringify(css)};document.head.appendChild(style);
      var requests=[];
      var http={get:function(url,options){var d=injector.get('$q').defer();requests.push({kind:'get',deferred:d,options:options});return d.promise;},post:function(url,body,options){var d=injector.get('$q').defer();requests.push({kind:'post',deferred:d,body:body,options:options});return d.promise;}};
      var model=scope.filterSettings=make(http);
      injector.get('$compile')(stage)(scope);document.body.appendChild(stage);scope.$evalAsync();
      window.__freestyleQa={scope:scope,stage:stage,style:style,model:model,requests:requests,injector:injector,make:make,
        act:function(fn){scope.$apply(fn);},
        settle:function(){return new Promise(function(r){setTimeout(r,180);});},
        reply:function(value,reject){var request=requests.shift();if(!request)throw Error('No pending mock request');scope.$apply(function(){reject?request.deferred.reject({status:500}):request.deferred.resolve({data:value});});return request.kind;},
        remove:function(){model.dispose();scope.$destroy();stage.remove();style.remove();delete window.__freestyleQa;}};
      model.load();return true;
    })()`);
    await run('__freestyleQa.settle()');
    const busy=await run(`(()=>{var q=__freestyleQa;return {busy:q.model.busy,loaded:q.model.loaded,disabled:q.stage.querySelector('md-select').getAttribute('aria-disabled'),installer:q.stage.querySelector('button[disabled]').classList.contains('osc-button-disabled')};})()`);
    assert(busy.busy && !busy.loaded && busy.disabled==='true' && busy.installer);results.push({case:'loading locks controls; installer visibly disabled',pass:true});
    await shot('01-loading');
    await run('__freestyleQa.reply(null,true);__freestyleQa.settle()');
    assert(await run(`!!__freestyleQa.stage.querySelector('[role="alert"]')`));
    await shot('02-load-error');
    await run(`__freestyleQa.act(function(){__freestyleQa.model.retry();});`);
    await run(`__freestyleQa.reply({mode:'reshade',allowConnection:true,connectionManager:true,bridgeEnabled:true});__freestyleQa.settle()`);
    results.push({case:'visible load error and successful retry',pass:true});
    await shot('03-reshade');
    const layout=await run(`(()=>{var q=__freestyleQa;var rows=Array.from(q.stage.querySelectorAll('.nv-freestyle-row'));return rows.map(function(row){var label=row.querySelector('.nv-freestyle-label')||row.querySelector('label');var control=row.querySelector('md-switch,md-select,button');if(!label||!control)return true;var a=label.getBoundingClientRect(),b=control.getBoundingClientRect(),r=row.getBoundingClientRect();return a.right<=b.left+1 && b.right<=r.right+1;});})()`);
    assert(layout.every(Boolean));results.push({case:'labels and controls aligned without overlap',pass:true});
    // Exercise the actual native select's focus/open/escape behavior.
    await run(`(()=>{var el=__freestyleQa.stage.querySelector('md-select');el.focus();el.click();})()`);
    await run('__freestyleQa.settle()');
    const options=await run(`Array.from(document.querySelectorAll('.md-select-menu-container.md-active md-option')).map(function(e){return e.textContent.trim();})`);
    assert.deepStrictEqual(options,['ReShade Freestyle','Duluka Freestyle','NVIDIA Freestyle']);
    await shot('04-mode-options');
    await call('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await call('Input.dispatchKeyEvent',{type:'keyUp',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await run('__freestyleQa.settle()');
    results.push({case:'native dropdown includes exactly three modes; Escape dismisses',pass:true});
    for(const mode of ['duluka','nvidia']) {
      await run(`__freestyleQa.act(function(){__freestyleQa.model.mode=${JSON.stringify(mode)};__freestyleQa.model.save();});`);
      await run(`__freestyleQa.reply({mode:${JSON.stringify(mode)},allowConnection:true,connectionManager:true,bridgeEnabled:false});__freestyleQa.settle()`);
      assert(await run(`/unavailable/.test(__freestyleQa.model.status) && __freestyleQa.stage.querySelectorAll('md-switch').length===0`));
      await shot('05-mode-'+mode);
    }
    results.push({case:'unavailable modes show honest status and hide ReShade controls',pass:true});
    await run(`__freestyleQa.act(function(){__freestyleQa.model.mode='reshade';__freestyleQa.model.save();});`);
    await run(`__freestyleQa.reply({mode:'reshade',allowConnection:true,connectionManager:true,bridgeEnabled:true});__freestyleQa.settle()`);
    await run(`__freestyleQa.act(function(){__freestyleQa.model.allowConnection=false;__freestyleQa.model.save();});`);
    assert(await run(`__freestyleQa.stage.querySelector('md-select').getAttribute('aria-disabled')==='true'`));
    await run(`__freestyleQa.reply(null,true);__freestyleQa.settle()`);
    assert(await run(`__freestyleQa.model.allowConnection && !!__freestyleQa.stage.querySelector('[role="alert"]')`));
    await shot('06-save-error-rollback');
    await run(`__freestyleQa.act(function(){__freestyleQa.model.retry();});`);
    assert.strictEqual(await run('__freestyleQa.requests[0].body.allowConnection'),false);
    await run(`__freestyleQa.reply({mode:'reshade',allowConnection:false,connectionManager:true,bridgeEnabled:false});__freestyleQa.settle()`);
    results.push({case:'save locks controls; rollback; retry sends intended value',pass:true});
    await run(`(()=>{var tabs=__freestyleQa.stage.querySelectorAll('md-tab-item');tabs[1].click();})();__freestyleQa.settle()`);
    assert(await run(`__freestyleQa.stage.querySelector('md-tab-item[aria-selected="true"]').textContent.toLowerCase().indexOf('photo')>=0`));
    await shot('07-photo-mode');
    results.push({case:'Photo mode tab opens and remains unavailable',pass:true});
    await run('new Promise(function(r){setTimeout(r,350);})');
    await run(`(()=>{__freestyleQa.stage.querySelectorAll('md-tab-item')[0].click();})();__freestyleQa.settle()`);
    await run('new Promise(function(r){setTimeout(r,350);})');
    await run(`__freestyleQa.stage.querySelector('md-switch').focus()`);
    const focus=await run(`(()=>{var e=__freestyleQa.stage.querySelector('md-switch');return {ok:document.activeElement===e,active:document.activeElement.tagName,tabindex:e.getAttribute('tabindex'),disabled:e.getAttribute('aria-disabled'),rect:e.getBoundingClientRect().toJSON(),tab:e.closest('md-tab-content').className,tabs:Array.from(__freestyleQa.stage.querySelectorAll('md-tab-item')).map(function(t){return {text:t.textContent,selected:t.getAttribute('aria-selected')};})};})()`);
    assert(focus.ok,JSON.stringify(focus));
    await call('Input.dispatchKeyEvent',{type:'keyDown',key:' ',code:'Space',windowsVirtualKeyCode:32});
    await call('Input.dispatchKeyEvent',{type:'char',text:' ',unmodifiedText:' '});
    await call('Input.dispatchKeyEvent',{type:'keyUp',key:' ',code:'Space',windowsVirtualKeyCode:32});
    await run('__freestyleQa.settle()');
    assert.strictEqual(await run('__freestyleQa.requests.length'),1,'Space should activate the focused NVIDIA switch once');
    assert.strictEqual(await run('__freestyleQa.requests[0].body.allowConnection'),true);
    await run(`__freestyleQa.reply({mode:'reshade',allowConnection:true,connectionManager:true,bridgeEnabled:true});__freestyleQa.settle()`);
    results.push({case:'keyboard focus and Space toggles native switch exactly once',pass:true});
    // A separate iframe gets a real CSS viewport without resizing the game/OSC.
    for(const width of [960,640]) {
      const result=await run(`(async()=>{var q=__freestyleQa;q.stage.style.visibility='hidden';var frame=document.createElement('iframe');frame.style.cssText='position:fixed;left:20px;top:100px;width:${width}px;height:760px;z-index:60000;border:0;';document.body.appendChild(frame);var doc=frame.contentDocument;doc.open();doc.write('<!doctype html><html><head><base href="http://localhost:59011/">'+Array.from(document.querySelectorAll('style,link[rel="stylesheet"]')).map(function(e){return e.outerHTML;}).join('')+'</head><body style="margin:0;background:#24292c!important;color:white;"></body></html>');doc.close();var el=doc.createElement('section');el.id='nv-filter-settings';el.style.padding='24px';el.innerHTML=${JSON.stringify(filterSettingsTemplate)};var scope=q.injector.get('$rootScope').$new(true);scope.filterSettings={mode:'reshade',loaded:true,busy:false,allowConnection:true,connectionManager:true,status:'Game filter can connect to ReShade in supported games.'};q.injector.get('$compile')(el)(scope);doc.body.appendChild(el);scope.$evalAsync();await new Promise(function(r){setTimeout(r,300);});var overflow=el.scrollWidth>el.clientWidth+2;var overlap=Array.from(el.querySelectorAll('.nv-freestyle-row')).some(function(row){var a=row.querySelector('.nv-freestyle-label')||row.querySelector('label'),b=row.querySelector('md-select,md-switch,button');if(!a||!b)return false;a=a.getBoundingClientRect();b=b.getBoundingClientRect();return a.right>b.left+1&&a.top<b.bottom-1&&a.bottom>b.top+1;});window.__freestyleQa.frame={frame:frame,scope:scope};return {width:${width},overflow:overflow,overlap:overlap};})()`);
      await shot('08-responsive-'+width);
      assert(!result.overflow && !result.overlap,JSON.stringify(result));
      await run(`__freestyleQa.frame.scope.$destroy();__freestyleQa.frame.frame.remove();delete __freestyleQa.frame`);
      results.push({case:'responsive viewport '+width+'px without overflow/overlap',pass:true});
    }
    const after=await (await fetch('http://127.0.0.1:59011/Duluka/v.1.0/GameFilter/Settings')).json();
    assert.deepStrictEqual(after,initial,'visual QA changed real backend settings');
    results.push({case:'real backend settings unchanged by visual tests',pass:true});
    fs.writeFileSync(path.join(out,'visual-results.json'),JSON.stringify({results,passed:results.length,initial,after},null,2));
    console.log(JSON.stringify({passed:results.length,results},null,2));
  } finally {
    await run(`(()=>{if(window.__freestyleQa){if(__freestyleQa.frame){__freestyleQa.frame.scope.$destroy();__freestyleQa.frame.frame.remove();}__freestyleQa.remove();}return true;})()`).catch(()=>{});
    ws.close();
  }
})().catch(error=>{fs.writeFileSync(path.join(out,'visual-failure.txt'),error.stack);console.error(error);process.exitCode=1;});
