'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const base = path.join(__dirname, '..');
const script = fs.readFileSync(path.join(base,'ui','module-profiles.js'),'utf8');
const html = fs.readFileSync(path.join(base,'ui','index.html'),'utf8');

function harness(catalog, priorCookie, nativeHandler) {
  const nodes = Object.create(null);
  const picker = {
    children: [], value: '', handlers: {},
    get firstChild() { return this.children[0]; },
    removeChild(node) { this.children.splice(this.children.indexOf(node),1); },
    appendChild(node) { this.children.push(node); },
    addEventListener(name,fn) { this.handlers[name]=fn; }
  };
  const apply = { handlers:{}, addEventListener(name,fn){this.handlers[name]=fn;}, click(){this.handlers.click();} };
  const install = { handlers:{}, disabled:false, addEventListener(name,fn){this.handlers[name]=fn;},click(){this.handlers.click();} };
  const remove = { handlers:{}, disabled:true, addEventListener(name,fn){this.handlers[name]=fn;},click(){this.handlers.click();} };
  const importButton = { handlers:{}, disabled:false, addEventListener(name,fn){this.handlers[name]=fn;},click(){this.handlers.click();} };
  const info = { textContent:'' };
  const style = { values:{},setProperty(k,v){this.values[k]=v;} };
  const surface = {style:{}};
  nodes.appearanceProfile=picker;nodes.btnApplyAppearance=apply;nodes.appearanceInfo=info;
  nodes.btnInstallAppearance=install;nodes.btnRemoveAppearance=remove;nodes.btnImportBackground=importButton;
  const cookieJar={};
  for(const pair of String(priorCookie||'').split(';')) {
    const pieces=pair.trim().split('=');
    if(pieces[0])cookieJar[pieces[0]]=pieces.slice(1).join('=');
  }
  const document = {
    get cookie(){return Object.keys(cookieJar).map(k=>k+'='+cookieJar[k]).join('; ');},
    set cookie(line){const pair=line.split(';')[0].split('=');cookieJar[pair[0]]=pair.slice(1).join('=');},
    getElementById:k=>nodes[k]||null,
    createElement:()=>({value:'',textContent:''}),
    querySelector:()=>surface,
    documentElement:{style}
  };
  function Request(){}
  Request.prototype.open=function(){};
  Request.prototype.send=function(){
    this.readyState=4;this.status=200;this.responseText=JSON.stringify(catalog);
    this.onreadystatechange();
  };
  const calls=[];
  const win={
    console, confirm:()=>true,
    cefQuery: nativeHandler ? ({request,onSuccess}) => {
      const req=JSON.parse(request);calls.push(req);
      onSuccess(JSON.stringify(nativeHandler(req,catalog)));
    } : undefined
  };
  vm.runInNewContext(script,{window:win,document,XMLHttpRequest:Request,Date,JSON,RegExp,Array,Promise,
    setTimeout:()=>1, clearTimeout:()=>{}}, {filename:'module-profiles.js'});
  return {picker,apply,install,remove,importButton,info,style,surface,document,calls,win};
}

test('appearance profile picker and safe BG apply',()=>{
  const h=harness({schemaVersion:1,profiles:[{
    id:'forest',name:'Forest',version:'1.0.0',author:'Test',accent:'#119944',
    accentBright:'#33bb77',background:'profiles/assets/forest/background.png'
  }]});
  assert.equal(h.picker.children.length,2);
  h.picker.value='forest';h.apply.click();
  assert.equal(h.surface.style.borderLeftColor,'#119944');
  assert.match(h.surface.style.backgroundImage,/profiles\/assets\/forest\/background\.png/);
  assert.match(h.document.cookie,/spnext_module_appearance_v1_Container=forest/);
});

test('reject malicious background path, invalid colors and duplicate IDs',()=>{
  const h=harness({schemaVersion:1,profiles:[
    {id:'evil',name:'Bad',accent:'#ff0000',background:'../../secret.png'},
    {id:'bad-color',name:'Color',accent:'red;url(x)',background:''},
    {id:'default',name:'Pretend Default',accent:'#000000',background:''}
  ]});
  assert.equal(h.picker.children.length,1);
  assert.equal(h.surface.style.borderLeftColor,'#76b900');
});

test('cookie persists across ephemeral ports, unknown profile falls back',()=>{
  const h=harness({schemaVersion:1,profiles:[{
    id:'forest',name:'Forest',accent:'#113355',accentBright:'#ffffff',
    background:'profiles/assets/forest/bg.jpg'
  }]},'spnext_module_appearance_v1_Container=forest');
  assert.equal(h.surface.style.borderLeftColor,'#113355');
  const gone=harness({schemaVersion:1,profiles:[]},'spnext_module_appearance_v1_Container=forest');
  assert.equal(gone.picker.value,'default');
});

test('settings UI exposes only fixed native install/remove actions, no code evaluation',()=>{
  assert.match(html,/id="appearanceProfile"/);
  assert.match(html,/id="btnApplyAppearance"/);
  assert.match(html,/id="btnImportBackground"/);
  assert.match(html,/id="btnInstallAppearance"/);
  assert.match(html,/id="btnRemoveAppearance"/);
  // Appearance code remains archived for future optional add-ons, but Core
  // explicitly does not execute it or expose profile management.
  assert.doesNotMatch(html,/src="module-profiles\.js/);
  assert.match(script,/LAUNCHER_IMPORT_BACKGROUND/);
  assert.match(script,/LAUNCHER_INSTALL_APPEARANCE/);
  assert.match(script,/LAUNCHER_UNINSTALL_APPEARANCE/);
  assert.doesNotMatch(script,/eval\(|new Function|ShellExecute/);
});

test('native install action reloads catalog, without automatically enabling the new theme', async()=>{
  const catalog={schemaVersion:1,profiles:[]};
  const h=harness(catalog,'',req=>{
    assert.equal(req.command,'LAUNCHER_INSTALL_APPEARANCE');
    catalog.profiles.push({id:'graphite',name:'Graphite',author:'Test',version:'1.0.0',
      accent:'#334455',accentBright:'#556677',background:''});
    return {ok:true};
  });
  h.install.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(h.calls.length,1);
  assert.equal(h.picker.children.length,2);
  assert.equal(h.surface.style.borderLeftColor,'#76b900');
});

test('native uninstall action is limited to selected module id and restores default', async()=>{
  const catalog={schemaVersion:1,profiles:[
    {id:'graphite',name:'Graphite',author:'Test',version:'1.0.0',
      accent:'#334455',accentBright:'#556677',background:''}
  ]};
  const h=harness(catalog,'spnext_module_appearance_v1_Container=graphite',req=>{
    assert.equal(req.command,'LAUNCHER_UNINSTALL_APPEARANCE');
    assert.equal(req.id,'graphite');
    catalog.profiles=[];
    return {ok:true};
  });
  assert.equal(h.remove.disabled,false);
  h.remove.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(h.calls.length,1);
  assert.equal(h.picker.value,'default');
  assert.equal(h.surface.style.borderLeftColor,'#76b900');
  assert.match(h.document.cookie,/spnext_module_appearance_v1_Container=default/);
});

test('built-in classic profile cannot trigger native removal',()=>{
  const h=harness({schemaVersion:1,profiles:[]},'',()=>{throw new Error('must not call')});
  assert.equal(h.remove.disabled,true);
  h.remove.click();
  assert.equal(h.calls.length,0);
});

test('removing inactive module preserves the active profile and its background', async()=>{
  const catalog={schemaVersion:1,profiles:[
    {id:'graphite',name:'Graphite',author:'Test',version:'1.0.0',
      accent:'#334455',accentBright:'#556677',background:'profiles/assets/graphite/background.png'},
    {id:'forest',name:'Forest',author:'Test',version:'1.0.0',
      accent:'#446622',accentBright:'#557733',background:'profiles/assets/forest/background.png'}
  ]};
  const h=harness(catalog,'spnext_module_appearance_v1_Container=graphite',req=>{
    assert.equal(req.command,'LAUNCHER_UNINSTALL_APPEARANCE');
    assert.equal(req.id,'forest');
    catalog.profiles=catalog.profiles.filter(x=>x.id!=='forest');
    return {ok:true};
  });
  h.picker.value='forest';
  h.picker.handlers.change();
  h.remove.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(h.calls.length,1);
  assert.equal(h.picker.value,'graphite');
  assert.equal(h.surface.style.borderLeftColor,'#334455');
  assert.match(h.document.cookie,/spnext_module_appearance_v1_Container=graphite/);
});

test('one click background importer refreshes catalog without activating new profile', async()=>{
  const catalog={schemaVersion:1,profiles:[]};
  const h=harness(catalog,'',req=>{
    assert.equal(req.command,'LAUNCHER_IMPORT_BACKGROUND');
    catalog.profiles.push({id:'bg-forest-1234abcd',name:'Forest',author:'Local User',version:'1.0.0',
      accent:'#76B900',accentBright:'#9BD532',
      background:'profiles/assets/bg-forest-1234abcd/background.png'});
    return {ok:true};
  });
  h.importButton.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(h.calls.length,1);
  assert.equal(h.picker.children.length,2);
  assert.equal(h.picker.value,'default');
  assert.equal(h.surface.style.borderLeftColor,'#76b900');
});

test('theme background is saved independently for each core module, never on Launcher',()=>{
  const h=harness({schemaVersion:1,profiles:[
    {id:'forest',name:'Forest',accent:'#119944',background:'profiles/assets/forest/background.png'},
    {id:'dusk',name:'Dusk',accent:'#335599',background:'profiles/assets/dusk/background.png'}
  ]});
  h.picker.value='forest';h.apply.click();
  assert.match(h.document.cookie,/spnext_module_appearance_v1_Container=forest/);
  assert.equal(h.surface.style.borderLeftColor,'#119944');
  assert.deepEqual(h.style.values,{},'root Launcher theme must remain unchanged');
  h.win.__quickModuleTarget='Capture';
  h.win.__onModuleAppearanceTargetChanged();
  assert.equal(h.picker.value,'default');
  h.picker.value='dusk';h.apply.click();
  assert.match(h.document.cookie,/spnext_module_appearance_v1_Capture=dusk/);
  assert.match(h.document.cookie,/spnext_module_appearance_v1_Container=forest/);
  h.win.__quickModuleTarget='Container';
  h.win.__onModuleAppearanceTargetChanged();
  assert.equal(h.picker.value,'forest');
  assert.equal(h.surface.style.borderLeftColor,'#119944');
});
test('appearance bridge has no access to the Launcher root background',()=>{
  assert.doesNotMatch(script,/querySelector\('\.quick-main'\)/);
  assert.doesNotMatch(script,/querySelector\('\.main-column'\)/);
  assert.doesNotMatch(script,/root\.style\.setProperty\('--green'/);
  assert.match(script,/quick-module\[data-module=/);
});
