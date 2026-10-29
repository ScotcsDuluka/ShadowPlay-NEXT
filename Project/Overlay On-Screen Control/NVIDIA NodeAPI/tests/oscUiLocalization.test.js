'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const osc=path.join(__dirname,'..','osc');
const mapScript=fs.readFileSync(path.join(osc,'Module','Base','Localization','osc-ui-phrase-map.js'),'utf8');
const runtime=fs.readFileSync(path.join(osc,'Module','Base','Localization','osc-ui-localization.js'),'utf8');
const thai=JSON.parse(fs.readFileSync(path.join(osc,'l10n','th-TH.json'),'utf8').replace(/^\uFEFF/,''));

function element(name,attrs={}) {
  const node={
    nodeType:1, tagName:name.toUpperCase(), attributes:Object.assign({},attrs),
    childNodes:[], parentElement:null, nodeValue:null,
    appendChild(child){this.childNodes.push(child);child.parentElement=this;return child},
    hasAttribute(key){return Object.hasOwn(this.attributes,key)},
    getAttribute(key){return this.attributes[key]||null},
    setAttribute(key,val){this.attributes[key]=String(val)},
    closest(selector){ // minimal icon/native Angular selector support
      for(let e=this;e;e=e.parentElement){
        if(['MD-ICON','I','SVG','STYLE','SCRIPT','CODE','PRE'].includes(e.tagName))return e;
        if((e.getAttribute('class')||'').split(' ').some(c=>c==='share-icon'||c==='nv-flex-icon'))return e;
        if((selector.includes('[translate]')&&e.hasAttribute('translate'))||
           (selector.includes('[data-translate]')&&e.hasAttribute('data-translate')))return e;
      }
      return null;
    }
  };
  return node;
}
function textNode(value){return {nodeType:3,nodeValue:value,parentElement:null};}
function fixture(){
 const body=element('body');const h=element('h2');h.appendChild(textNode('Game profiles'));body.appendChild(h);
 const button=element('button',{'title':'Apply changes','aria-label':'Game name'});button.appendChild(textNode('Apply changes'));body.appendChild(button);
 const icon=element('md-icon',{'class':'share-icon icon-settings'});icon.appendChild(textNode('Settings'));body.appendChild(icon);
 const translated=element('label',{'translate':'l10n.settings'});translated.appendChild(textNode('Settings'));body.appendChild(translated);
 return {body,h,button,icon,translated};
}
function harness(){
 const nodes=fixture();
 let locale='th-TH',change=null,observer=null;
 const timers=[];
 const win={
   setTimeout(callback){timers.push(callback);},
   angular:{module(){return {run(def){win.start=def[def.length-1];}}}}
 };
 const doc={
   body:nodes.body,
   createTreeWalker(root){
     const all=[];function walk(n){for(const c of (n.childNodes||[])){all.push(c);walk(c)}}
     walk(root);let i=0;
     return {nextNode:()=>all[i++]||null};
   }
 };
 class MockObserver{
   constructor(cb){this.cb=cb;observer=this;}
   observe(){}
   emit(records){this.cb(records)}
 }
 const state={};
 vm.runInNewContext(mapScript,{window:win,JSON},{filename:'osc-ui-phrase-map.js'});
 vm.runInNewContext(runtime,{window:win,document:doc,MutationObserver:MockObserver,
   NodeFilter:{SHOW_ELEMENT:1,SHOW_TEXT:4},WeakMap,JSON,Object},{filename:'osc-ui-localization.js'});
 const translate={use:()=>locale};
 const filter=()=>key=>thai[key]||key;
 const rootScope={$on(name,handler){if(name==='$translateChangeSuccess')change=handler}};
 win.start(translate,filter,rootScope,{warn(){}});
 function flush(){while(timers.length)timers.shift()()}
 return {nodes,win,flush,observer:()=>observer,language(lang){locale=lang;change({}, {language:lang});flush();}};
}
test('locale dictionaries and bridge cover module phrases',()=>{
 const eng=JSON.parse(fs.readFileSync(path.join(osc,'l10n','en-US.json'),'utf8').replace(/^\uFEFF/,''));
 const thaiKeys=Object.keys(thai).filter(x=>x.startsWith('l10n.'));
 const enKeys=Object.keys(eng).filter(x=>x.startsWith('l10n.'));
 assert.equal(enKeys.length,thaiKeys.length);
 assert.equal(eng['l10n.oscUi_gameProfiles'],'Game profiles');
 assert.equal(thai['l10n.oscUi_gameProfiles'],'โปรไฟล์เกม');
 assert.equal(thai['l10n.gpuRestartNow'],'รีสตาร์ตตอนนี้');
 assert.match(mapScript,/Game profiles/);
});
test('new DOM labels and attributes translate without touching icons',()=>{
 const h=harness();h.flush();
 assert.equal(h.nodes.h.childNodes[0].nodeValue,'โปรไฟล์เกม');
 assert.equal(h.nodes.button.childNodes[0].nodeValue,'ใช้การเปลี่ยนแปลง');
 assert.equal(h.nodes.button.getAttribute('title'),'ใช้การเปลี่ยนแปลง');
 assert.equal(h.nodes.button.getAttribute('aria-label'),'ชื่อเกม');
 assert.equal(h.nodes.icon.childNodes[0].nodeValue,'Settings');
 assert.equal(h.nodes.translated.childNodes[0].nodeValue,'การตั้งค่า');
});
test('dynamic updates translate and changing to English restores originals',()=>{
 const h=harness();h.flush();
 h.nodes.h.childNodes[0].nodeValue='Graphics processor';
 h.observer().emit([{type:'characterData',target:h.nodes.h.childNodes[0]}]);h.flush();
 assert.equal(h.nodes.h.childNodes[0].nodeValue,'หน่วยประมวลผลกราฟิก');
 h.language('en-US');
 assert.equal(h.nodes.h.childNodes[0].nodeValue,'Graphics processor');
 assert.equal(h.nodes.button.getAttribute('title'),'Apply changes');
 h.language('th-TH');
 assert.equal(h.nodes.h.childNodes[0].nodeValue,'หน่วยประมวลผลกราฟิก');
});
