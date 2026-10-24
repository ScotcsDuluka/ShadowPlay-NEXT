'use strict';
// Isolated Portal UI tests. No real accounts, network, server or credentials.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const crypto = require('node:crypto').webcrypto;

const ui = path.resolve(__dirname, '../Duluka.Server/Portal');
const js = fs.readFileSync(path.join(ui, 'app.js'), 'utf8');
const html = fs.readFileSync(path.join(ui, 'index.html'), 'utf8');

function fakePortal({ mode='staging', enabled=true, configError=false }={}) {
  const elements = Object.create(null);
  const requested = [];
  const forms = ['login-form', 'register-form', 'apply-form'];
  for (const match of html.matchAll(/\bid="([^"]+)"/g)) {
    const listeners = Object.create(null);
    elements[match[1]] = {
      id:match[1],hidden:false,disabled:false, textContent:'',value:'',
      attrs:{},listeners, firstChild:{textContent:''},
      classList:{toggle(){},add(){},remove(){}},
      addEventListener(name,fn){listeners[name]=fn;},
      setAttribute(name,value){this.attrs[name]=value;},
      removeAttribute(name){delete this.attrs[name];},
      focus(){this.focused=true;},
      replaceChildren(){this.children=[];},
      append(...items){this.children=(this.children||[]).concat(items);},
      reportValidity(){return true;}
    };
  }
  for(const name of forms) {
    elements[name].elements={
      username:{value:'valid_tester'},
      password:{value:'Staging-strong-password-123'},
      confirmPassword:{value:'different-password',attrs:{},setAttribute(k,v){this.attrs[k]=v;},
        removeAttribute(k){delete this.attrs[k]},focus(){this.focused=true;}}
    };
  }
  const st=new Map(),local=new Map();
  const storage=(back)=>({
    getItem(k){return back.has(k)?back.get(k):null;},
    setItem(k,v){back.set(k,String(v));},
    removeItem(k){back.delete(k);}
  });
  const document={
    getElementById(id){return elements[id]||null;},
    createElement(tag){return {tagName:tag,className:'',textContent:'',children:[],
      append(...items){this.children.push(...items);},addEventListener(){},setAttribute(){}};},
    querySelectorAll(){return [{disabled:false},{disabled:false}];}
  };
  const fetch=async(url,opts={})=>{
    requested.push({url,method:opts.method||'GET',body:opts.body?JSON.parse(opts.body):null});
    if(url==='/portal/config' && configError) throw new Error('config unavailable');
    const result =
      url==='/portal/config' ? {accountMode:mode,registrationEnabled:enabled} :
      url==='/v1/cbt1/status' ? {accepting:false} :
      url==='/v1/cbt1/me' ? {username:'valid_tester',application:null} :
      url==='/v1/account/me' ? {resource:{username:'valid_tester'}} :
      url==='/v1/auth/register' ? {ok:true,resource:{sessionToken:'TESTONLY-PLACEHOLDER-TOKEN',username:'valid_tester'}} :
      url==='/v1/auth/login' ? {ok:true,resource:{sessionToken:'TESTONLY-PLACEHOLDER-TOKEN',username:'valid_tester'}} :
      {ok:true};
    return {ok:true,status:200,async json(){return result;}};
  };
  const context={document,fetch,crypto,sessionStorage:storage(st),localStorage:storage(local),
    btoa:(s)=>Buffer.from(s,'binary').toString('base64'),Uint8Array,Error,JSON,String,
    console};
  vm.runInNewContext(js,context,{filename:'portal/app.js'});
  async function flush(){for(let i=0;i<5;i++)await new Promise(r=>setImmediate(r));}
  async function submit(formId){
    const form=elements[formId];
    await form.listeners.submit({preventDefault(){},currentTarget:form});
    await flush();
  }
  return {elements,requested,st,local,flush,submit};
}

test('register asks for password confirmation and never sends the confirmation value',async()=>{
  assert.match(html,/id="register-confirm-password"/);
  const h=fakePortal();
  await h.flush();
  await h.submit('register-form');
  assert.equal(h.requested.filter(x=>x.url==='/v1/auth/register').length,0);
  assert.match(h.elements['auth-message'].textContent,/do not match/);
  assert.equal(h.elements['register-form'].elements.confirmPassword.attrs['aria-invalid'],'true');
  h.elements['register-form'].elements.confirmPassword.value='Staging-strong-password-123';
  await h.submit('register-form');
  const sent=h.requested.filter(x=>x.url==='/v1/auth/register');
  assert.equal(sent.length,1);
  assert.deepEqual(Object.keys(sent[0].body).sort(),['deviceKey','deviceName','password','username'].sort());
  assert.equal(h.elements['register-form'].elements.password.value,'');
  assert.equal(h.elements['register-form'].elements.confirmPassword.value,'');
});

test('failed mode verification fails closed rather than exposing unknown account registration',async()=>{
  const h=fakePortal({configError:true});
  await h.flush();
  assert.equal(h.elements['login-form'].hidden,true);
  assert.equal(h.elements['register-form'].hidden,true);
  assert.equal(h.elements['show-register'].hidden,true);
  await h.submit('register-form');
  assert.equal(h.requested.filter(x=>x.url.startsWith('/v1/auth/')).length,0);
});

test('main account mode with closed public signup still allows login',async()=>{
  const h=fakePortal({mode:'main',enabled:false});
  await h.flush();
  assert.equal(h.elements['show-register'].hidden,true);
  assert.equal(h.elements['register-form'].hidden,true);
  h.elements['register-form'].elements.confirmPassword.value=
    h.elements['register-form'].elements.password.value;
  await h.submit('register-form');
  assert.equal(h.requested.filter(x=>x.url==='/v1/auth/register').length,0);
  await h.submit('login-form');
  assert.equal(h.requested.filter(x=>x.url==='/v1/auth/login').length,1);
  assert.ok(h.st.has('duluka.main.session.v1'));
  assert.ok(!h.st.has('duluka.staging.session.v1'));
});
