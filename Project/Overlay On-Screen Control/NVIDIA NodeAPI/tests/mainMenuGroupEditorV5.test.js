'use strict';
const test=require('node:test'), assert=require('node:assert/strict');
const fs=require('node:fs'), vm=require('node:vm'), path=require('node:path');
const G=require('../osc/mods/main-menu-group-model');
const V4=require('../osc/mods/main-menu-unified-model');
const source=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-group-editor.js'),'utf8');
const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-group-editor.css'),'utf8');

function fake(){
  const storage=Object.create(null),events=Object.create(null);
  let failSave=false,failSync=false,host=null;
  function node(){
    const handlers={},attrs={},classes=new Set(),children={};
    const el={isConnected:true,className:'',innerHTML:'',textContent:'',value:'',
      classList:{add:x=>classes.add(x),remove:x=>classes.delete(x),contains:x=>classes.has(x)},
      setAttribute:(k,v)=>{attrs[k]=v;},hasAttribute:k=>Object.hasOwn(attrs,k),
      getAttribute:k=>attrs[k]??null,addEventListener(k,f){(handlers[k]??=[]).push(f);},
      fire(k,target){for(const f of handlers[k]||[])f({target,button:0});},
      querySelector(sel){if(sel==='.nv-unified-editor')return {};if(sel==='.nv-group-layout-editor')return children.editor||null;
        return children[sel]??=(function(){return node();})()},
      insertBefore(child){children.editor=child;},
      appendChild(child){children.editor=child;},
      closest(){return this;},
      _handlers:handlers
    };
    return el;
  }
  const document={
    createElement:()=>node(),
    querySelector:q=>q==='.nv-live-editor'?host:null,
    addEventListener(k,f){(events[k]??=[]).push(f);},
    removeEventListener(k,f){events[k]=(events[k]||[]).filter(x=>x!==f);},
    elementFromPoint:()=>null
  };
  const window={
    NvGroupLayoutA:G,NvUnifiedLayoutV4:V4,
    localStorage:{
      getItem:key=>Object.hasOwn(storage,key)?storage[key]:null,
      setItem(key,val){if(failSave&&key==='DulukaMainMenuGroupLayoutA_V5')throw Error('disk full');storage[key]=String(val);},
      removeItem:key=>delete storage[key]
    },
    __dulukaUnifiedMainMenu:{get:()=>V4.initial(),openEditor(){}},
    setInterval(){return 42;},
    __nvUnifiedV4RequestNativeSync(){if(failSync)throw Error('native menu unavailable');}
  };
  vm.runInNewContext(source,{window,document,console,JSON,Math,Number},'main-menu-group-editor.js');
  return {api:window.__dulukaGroupLayoutA,window,document,storage,
    open(){host=node();return host;},
    remove(){if(host&&host.querySelector('.nv-group-layout-editor'))host.querySelector('.nv-group-layout-editor').isConnected=false;host=null;},
    listeners:k=>(events[k]||[]).length,fail:()=>{failSave=true;},failNativeSync:()=>{failSync=true;},
    button:(key,value)=>({
      closest(){return this;}, getAttribute:k=>k===key?value:null, hasAttribute:k=>k===key
    })
  };
}

test('Reset Groups can be undone back to the user-edited layout',()=>{
  const f=fake(),host=f.open();f.api.enable(true);
  const old=G.createGroup(f.api.get(),'left','small','triple');
  f.api.set(old,'Changed');
  const editor=host.querySelector('.nv-group-layout-editor');
  editor.fire('click',f.button('data-g-reset',''));
  assert.notDeepEqual(f.api.get(),old,'reset must restore legacy V4 state');
  editor.fire('click',f.button('data-g-undo',''));
  assert.deepEqual(f.api.get(),old,'undo should restore previous user state');
});

test('failed localStorage writes cannot change in-memory layout',()=>{
  const f=fake(),before=f.api.get(),newState=G.createGroup(before,'left','small','triple');
  f.fail();assert.throws(()=>f.api.set(newState,'Save'),/disk full/);
  assert.deepEqual(f.api.get(),before);
});

test('disabling and re-enabling V5 restores the editor class on an existing Settings panel',()=>{
  const f=fake(),host=f.open();f.api.enable(true);
  assert.equal(host.classList.contains('nv-group-layout-on'),true);
  f.api.enable(false);assert.equal(host.classList.contains('nv-group-layout-on'),false);
  f.api.enable(true);assert.equal(host.classList.contains('nv-group-layout-on'),true);
  assert.match(css,/\.nv-group-layout-editor\s*\{[^}]*display:\s*none/i,'disabled editor must not remain visible');
});

test('destroyed Settings editor releases document drag listener on remount',()=>{
  const f=fake();const first=f.open();f.api.enable(true);
  assert.equal(f.listeners('mouseup'),1);
  first.querySelector('.nv-group-layout-editor').isConnected=false;
  f.remove();f.api.mount();f.open();f.api.mount();
  assert.equal(f.listeners('mouseup'),1,'no stale mouseup listener from previous page');
});

test('a native Sync error after saving cannot erase Undo history',()=>{
  const f=fake(),host=f.open();f.api.enable(true);
  const old=f.api.get(),editor=host.querySelector('.nv-group-layout-editor');
  f.failNativeSync();
  editor.fire('click',f.button('data-g-add','left'));
  assert.equal(f.api.get().panels.left.length,old.panels.left.length+1,'the model was saved');
  editor.fire('click',f.button('data-g-undo',''));
  assert.deepEqual(f.api.get(),old,'Undo should still work after external Sync failure');
});
