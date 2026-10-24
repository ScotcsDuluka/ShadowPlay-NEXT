'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

const safeInfo = {schemaVersion:1,mode:'guided',
  nativeInstall:false,nativeUninstall:false,officialUrl:'https://reshade.me/'};
const readOnlyReport = {schemaVersion:1,status:'not-detected',
  evidence:{},gameRunning:'not-seen',
  message:'No local ReShade evidence found',mutationSupported:false};

function deferred() {
  let resolve, reject;
  const promise = new Promise((ok, fail) => {resolve=ok; reject=fail;});
  return {promise,resolve,reject};
}
class Element {
  constructor(tag) {
    this.tag = tag;
    this.children = [];
    this.events = {};
    this.attrs = {};
    this.textContent = '';
    this.value = '';
    this.hidden = false;
    this.disabled = false;
    this.className = '';
    const names = new Set();
    this.classList = {add: name => names.add(name),remove: name => names.delete(name),
      contains: name => names.has(name)};
  }
  appendChild(item) { this.children.push(item); return item; }
  insertBefore(item, previous) {
    const index = this.children.indexOf(previous);
    this.children.splice(index < 0 ? 0 : index, 0, item);
    return item;
  }
  setAttribute(name,value) {this.attrs[name] = String(value);}
  removeAttribute(name) {delete this.attrs[name];}
  addEventListener(type, callback) {this.events[type] = callback;}
  querySelector(selector) {
    const id = selector.startsWith('#') ? selector.slice(1) : null;
    if (!id) return null;
    return scan(this, element => element.id === id);
  }
}
function scan(root,predicate) {
  if (predicate(root)) return root;
  for (const child of root.children || []) {
    const found = scan(child,predicate);
    if (found) return found;
  }
  return null;
}
async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
}
function createHarness() {
  const scope = {$evalAsync: () => {},$$destroyed:false,
    $destroy() {this.$$destroyed=true;}};
  const settings = new Element('section');
  settings.id = 'nv-filter-settings';
  settings.__nvScope = {$new: () => scope,$$destroyed:false};
  const document = {
    createElement: tag => new Element(tag),
    createTextNode: value => ({textContent:value,children:[]}),
    querySelector: selector => selector === '#nv-filter-settings' ? settings : null,
    documentElement: {contains: node => node === settings}
  };
  const injector = {get: name => {
    if (name !== '$compile') throw new Error('Unexpected Angular dependency');
    return () => () => {};
  }};
  const window = {angular:{element: () => ({injector: () => injector})},
    confirm: () => {throw new Error('Should not show confirmation without explicit click');},
    open: () => {throw new Error('Should not launch official website');}};
  const info = [];
  const fetch = (url, options) => {
    if (url.endsWith('/Info')) {
      const value=deferred();
      info.push(value);
      return value.promise.then(data => ({ok:true,json: () => Promise.resolve(data)}));
    }
    if (url.endsWith('/Inspect') && options && options.method === 'POST')
      return Promise.resolve({ok:true,json:()=>Promise.resolve(readOnlyReport)});
    throw new Error('Unexpected URL: '+url);
  };
  const script = fs.readFileSync(path.join(__dirname,'../osc/mods/reshade-manager.js'),'utf8');
  vm.runInNewContext(script,{window,document,fetch,console},{filename:'reshade-manager.js'});
  const ui = window.NvReShadeManager;
  return {ui,scope,info,settings,get:predicate=>scan(settings,predicate)};
}

test('never enables official Setup until current Info safety contract succeeds',async () => {
  const h=createHarness();
  assert.equal(h.ui.open(),true);
  assert.equal(h.info.length,1);
  const game=h.get(el=>el.tag==='input' && el.attrs['aria-label']==='Full path to game executable');
  const inspect=h.get(el=>el.tag==='button' && el.textContent==='Inspect game');
  const launch=h.get(el=>el.tag==='button' && el.textContent==='Open official ReShade Setup');
  const back=h.get(el=>el.className==='nv-reshade-back');
  assert.ok(game && inspect && launch && back);
  game.value='C:\\Games\\Example\\game.exe';
  inspect.events.click();
  h.scope.reshadeUi.confirmed=true;
  await flush();
  assert.equal(launch.disabled,true,'successful Inspect must not unlock pending Info');
  h.info[0].resolve({...safeInfo,nativeUninstall:true});
  await flush();
  assert.equal(launch.disabled,true,'unsafe Info contract must block Setup');
  back.events.click();
  assert.equal(h.ui.open(),true);
  assert.equal(launch.disabled,true,'reopen must reset safety-contract verification');
  h.info[1].resolve(safeInfo);
  await flush();
  assert.equal(launch.disabled,false,'current safe Info + user confirmation may enable Setup');
});

test('stale Info response after Back does not unlock a newer opening',async () => {
  const h=createHarness();
  h.ui.open();
  const game=h.get(el=>el.tag==='input' && el.attrs['aria-label']==='Full path to game executable');
  const inspect=h.get(el=>el.tag==='button' && el.textContent==='Inspect game');
  const launch=h.get(el=>el.tag==='button' && el.textContent==='Open official ReShade Setup');
  const back=h.get(el=>el.className==='nv-reshade-back');
  game.value='C:\\Games\\Example\\game.exe';
  inspect.events.click();
  h.scope.reshadeUi.confirmed=true;
  await flush();
  back.events.click();
  h.ui.open();
  h.info[0].resolve(safeInfo);
  await flush();
  assert.equal(launch.disabled,true,'stale successful Info cannot unlock Setup');
  h.info[1].resolve(safeInfo);
  await flush();
  assert.equal(launch.disabled,false,'newest confirmed Info can enable Setup');
  back.events.click();
  h.ui.open();
  assert.equal(launch.disabled,true,'third opening must lock Setup again');
  h.info[2].reject(new Error('connection lost'));
  await flush();
  assert.equal(launch.disabled,true,'failed Info must keep Setup locked');
});