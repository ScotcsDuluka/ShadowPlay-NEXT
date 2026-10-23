'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const p=path.resolve(__dirname,'../osc');
function read(rel){return fs.readFileSync(path.join(p,rel),'utf8')}
class Node {
 constructor(tag,cls=''){
  this.tagName=tag;this.className=cls;this.children=[];this.parentNode=null;this.textContent='';this.attrs={};
  this.classList={add:name=>{if(!this.classList.contains(name))this.className+=' '+name},
   remove:name=>{this.className=this.className.split(' ').filter(x=>x!==name).join(' ')},
   contains:name=>this.className.split(' ').includes(name)};
  const properties={};this.style={getPropertyValue:name=>properties[name]||'',
   setProperty:(name,value)=>{properties[name]=value},removeProperty:name=>{delete properties[name]}};
 }
 appendChild(el){if(el.parentNode)el.parentNode.removeChild(el);this.children.push(el);el.parentNode=this;return el}
 removeChild(el){let i=this.children.indexOf(el);if(i<0)throw Error('not child');this.children.splice(i,1);el.parentNode=null;return el}
 insertBefore(el,before){if(el.parentNode)el.parentNode.removeChild(el);let i=this.children.indexOf(before);if(i<0)throw Error('not child');this.children.splice(i,0,el);el.parentNode=this;return el}
 setAttribute(k,v){this.attrs[k]=v}
 querySelector(selector) {
  if(selector.startsWith(':scope > '))return this.children.find(x=>x.tagName===selector.slice(9))||null;
  let isClass=selector.startsWith('.'),key=isClass?selector.slice(1):selector;
  const all=(n)=>{for(let c of n.children){if((isClass?c.className.split(' ').includes(key):c.tagName===key))return c;let found=all(c);if(found)return found}return null};
  return all(this)
 }
}
test('existing Angular-owned background, palette and md-select footer are moved, not cloned',()=>{
 const editor=new Node('section','nv-live-editor'),head=new Node('header'),workspace=new Node('div','nv-live-workspace'),library=new Node('div','nv-live-library'),side=new Node('div','nv-live-panel'),foot=new Node('footer'),background=new Node('nv-osc-background-settings'),help=new Node('p'),scope={};
 help.textContent='Drag onto a Side row to group 2–3 icon slots. Settings stays available.';
 const nativeHelp=help.textContent;library.appendChild(help);
 workspace.appendChild(side);workspace.appendChild(library);
 editor.appendChild(head);editor.appendChild(workspace);editor.appendChild(foot);editor.appendChild(background);
 const document={readyState:'complete',querySelector(q){return q==='.nv-live-editor'?editor:null},createElement(tag){return new Node(tag)},createComment(){return new Node('#comment')},addEventListener(){}};
 const window={setInterval:null};
 vm.runInNewContext(read('mods/nv-freestyle-settings.js'),{window,document}, {timeout:2000});
 const grid=editor.querySelector('.nv-freestyle-grid');
 assert.ok(grid);
 const left=grid.querySelector('.nv-freestyle-background'),right=grid.querySelector('.nv-freestyle-controls');
 assert.equal(left.querySelector('nv-osc-background-settings'),background);
 assert.equal(left.querySelector('footer'),foot);
 assert.equal(right.querySelector('.nv-live-library'),library);
 assert.equal(library.querySelector('p'),help,'keeps the native help node');
 assert.equal(help.textContent,'Drag Controls onto the menu.','the Controls column uses concise, accurate V5 help');
 assert.equal(left.querySelector('.nv-live-workspace'),workspace);
 assert.equal(workspace.querySelector('.nv-live-panel'),side);
 assert.equal(editor.querySelector('header'),head);
 assert.equal(editor.querySelector('.nv-freestyle-legacy').tagName,'details');
 assert.equal(editor.querySelector('.nv-freestyle-legacy').hidden,true,'V2 workspace is hidden before V5 starts');
 assert.equal(editor.querySelector('.nv-freestyle-legacy').style.display,'none');
 assert.equal(window.__nvFreestyleSettings.scan(),undefined);
 assert.equal(editor.querySelector('.nv-freestyle-grid'),grid,'no duplicates after repeated scans');
 assert.equal(window.__nvFreestyleSettings.restore(),true);
 assert.deepEqual(editor.children,[head,workspace,foot,background],'restores exact original sibling order');
 assert.deepEqual(workspace.children,[side,library],'restores the original Angular library parent');
 assert.equal(help.textContent,nativeHelp,'restores original help when returning the native nodes');
 assert.equal(editor.classList.contains('nv-freestyle-settings'),false);
 assert.equal(window.__nvFreestyleSettings.restore(),false,'cleanup is idempotent');
 assert.equal(window.__nvFreestyleSettings.organize(editor),true,'same nodes can be opened again');
 assert.equal(editor.querySelector('.nv-freestyle-grid').querySelector('nv-osc-background-settings'),background);
});
test('80/20 visual contract remains scoped to original native Settings',()=>{
 const css=read('mods/nv-freestyle-settings.css'),index=read('index.html');
 assert.match(css,/grid-template-columns:minmax\(0,4fr\) minmax\(0,1fr\)/);
 assert.match(css,/\.nv-live-editor\.nv-freestyle-settings/);
 assert.match(css,/\.nv-freestyle-legacy\[open\]/);
 assert.doesNotMatch(css,/\b(?:clamp|min|max)\(/,'the real Chromium 73 runtime cannot parse these CSS functions');
 assert.ok(index.includes('/mods/nv-freestyle-settings.js'));
 assert.ok(index.includes('/mods/nv-freestyle-settings.css'));
 assert.ok(index.includes('/mods/launcher-language-sync.js'));
 assert.ok(index.includes('/mods/osc-motion.js'));
});
test('header actions reserve native focus borders and fixed dimensions while interaction states only change paint',()=>{
 const css=read('mods/nv-freestyle-settings.css');
 const rules=Array.from(css.matchAll(/([^{}]+)\{([^{}]*)\}/g),m=>({selector:m[1].trim(),body:m[2]}));
 const base=rules.find(rule=>rule.selector.endsWith('.nv-live-editor.nv-freestyle-settings > header > .osc-button'));
 assert.ok(base,'header actions have an independently scoped box');
 for(const property of ['width','min-width','max-width'])
  assert.ok(base.body.includes(property+':var(--nv-f-header-button-width)!important;'),property+' cannot depend on translated text');
 for(const property of ['height','min-height','max-height'])
  assert.ok(base.body.includes(property+':var(--nv-f-header-button-height,50px)!important;'),property+' shares the responsive fixed height');
 assert.match(base.body,/box-sizing:border-box!important/);
 assert.match(base.body,/border:3px solid transparent!important/,'reserve native focus ring before focus');
 assert.match(base.body,/flex:0 0 var\(--nv-f-header-button-width\)!important/,'flex cannot shrink header actions');
 assert.match(base.body,/white-space:nowrap!important/);assert.match(base.body,/text-overflow:ellipsis!important/);
 const states=rules.filter(rule=>rule.selector.includes('> header > .osc-button')&&/:(hover|focus|active)|\[disabled\]|\.osc-button-disabled/.test(rule.selector));
 assert.equal(states.length,3,'gray, green and disabled header paint states');
 for(const rule of states) {
  assert.doesNotMatch(rule.body,/(?:^|;)\s*(?:(?:min-|max-)?(?:width|height)|margin(?:-[a-z]+)?|padding(?:-[a-z]+)?|border-(?:width|style)|flex(?:-[a-z]+)?|font-(?:size|weight)|line-height|transform)\s*:/,
   'interaction states must not alter box metrics: '+rule.selector);
 }
 const gray=states.find(rule=>rule.selector.includes('.osc-button-gray'));
 const green=states.find(rule=>rule.selector.includes('.osc-button-green'));
 for(const rule of [gray,green])for(const state of ['hover','focus','active'])assert.ok(rule.selector.includes(':'+state));
 assert.match(gray.body,/border-color:#76b900!important/);assert.match(green.body,/border-color:#fff!important/);
 assert.ok(css.includes('--nv-f-header-button-height:38px'),'short viewports change every height bound together');
 const source=read('mods/main-menu-live.js'),header=source.match(/<header>[\s\S]*?<\/header>/);
 assert.ok(header);const buttons=header[0].match(/<button\b[^>]*class="osc-button[^>]*>/g)||[];
 assert.equal(buttons.length,2);for(const button of buttons)assert.match(button,/\bhover-focus\b/,'pointer entry uses the shipped NVIDIA focus directive');
});
test('DOM insertions organize the native shell immediately, including delayed Background creation',()=>{
 for(const readyState of ['complete','loading']){
  const editor=new Node('section','nv-live-editor'),workspace=new Node('div','nv-live-workspace'),
   library=new Node('div','nv-live-library'),footer=new Node('footer'),background=new Node('nv-osc-background-settings');
  workspace.appendChild(library);editor.appendChild(workspace);editor.appendChild(footer);
  let ready,callback,observed,intervalMs,instances=0;
  const document={readyState,body:readyState==='complete'?new Node('body'):null,
   querySelector:q=>q==='.nv-live-editor'?editor:null,createElement:tag=>new Node(tag),
   createComment:()=>new Node('#comment'),addEventListener:(event,fn)=>{if(event==='DOMContentLoaded')ready=fn}};
  const window={setInterval:(fn,ms)=>{intervalMs=ms},MutationObserver:function(fn){
   instances++;callback=fn;this.observe=(target,options)=>{observed={target,options}};
  }};
  vm.runInNewContext(read('mods/nv-freestyle-settings.js'),{window,document},{timeout:2000});
  if(readyState==='loading'){
   assert.equal(instances,0,'waits for body at DOMContentLoaded');
   document.body=new Node('body');ready();
  }
  assert.equal(instances,1);
  assert.equal(observed.target,document.body);
  assert.equal(observed.options.childList,true);assert.equal(observed.options.subtree,true);
  assert.equal(intervalMs,300,'retains the fallback for older hosts');
  assert.equal(editor.querySelector('.nv-freestyle-grid'),null,'waits for all Angular-owned nodes');
  editor.appendChild(background);
  callback([{type:'childList',target:editor,addedNodes:[background]}]);
  const grid=editor.querySelector('.nv-freestyle-grid');
  assert.ok(grid,'organizes in the DOM observer delivery, without waiting for a timer');
  assert.equal(grid.querySelector('nv-osc-background-settings'),background);
  assert.equal(grid.querySelector('.nv-freestyle-legacy').hidden,true);
  callback([]);
  assert.equal(editor.querySelector('.nv-freestyle-grid'),grid,'observer follow-up does not rebuild nodes');
 }
});
function positioningFixture({width=1920,height=1080,scale=1,offsetX=0,offsetY=160,barBottom=400}={}){
 const editor=new Node('section','nv-live-editor'),workspace=new Node('div','nv-live-workspace'),
  library=new Node('div','nv-live-library'),foot=new Node('footer'),background=new Node('nv-osc-background-settings');
 workspace.appendChild(library);editor.appendChild(workspace);editor.appendChild(foot);editor.appendChild(background);
 const value=(key,fallback)=>parseFloat(editor.style.getPropertyValue('--nv-f-settings-'+key))||fallback;
 const computed=()=>({left:value('left',width*.104)+'px',top:value('top',height*.44)+'px',
  width:value('width',width*.792)+'px',height:value('height',height*.42)+'px'});
 editor.getBoundingClientRect=()=>{const s=computed();return {left:parseFloat(s.left)*scale+offsetX,
  top:parseFloat(s.top)*scale+offsetY,width:parseFloat(s.width)*scale,height:parseFloat(s.height)*scale}};
 let visible=editor;
 const document={readyState:'complete',documentElement:new Node('html'),querySelector(q){
  if(q==='.nv-live-editor')return visible;
  if(q.includes('md-menu-bar'))return {getBoundingClientRect:()=>({height:240,bottom:barBottom})};return null},
  createElement:tag=>new Node(tag),createComment:()=>new Node('#comment'),addEventListener(){}};
 const window={innerWidth:width,innerHeight:height,getComputedStyle:computed};
 vm.runInNewContext(read('mods/nv-freestyle-settings.js'),{window,document},{timeout:2000});
 return {editor,document,window,close:()=>{visible=null;window.__nvFreestyleSettings.scan()}};
}
test('floating Settings follows settled native bar through parent translation and host scaling',()=>{
 const f=positioningFixture({scale:.8,offsetX:30,offsetY:160});
 const expected={left:1920*.104,top:440,width:1920*.792,height:1080*.42};
 function near(actual,want){assert.ok(Math.abs(actual-want)<.002,actual+' ≈ '+want)}
 for(let i=0;i<5;i++){
  f.window.__nvFreestyleSettings.position();
  const actual=f.editor.getBoundingClientRect();
  for(const key of Object.keys(expected))near(actual[key],expected[key]);
 }
 const top=f.editor.style.getPropertyValue('--nv-f-settings-top');
 f.document.documentElement.classList.add('nv-motion-active');
 f.window.__nvFreestyleSettings.position();
 assert.equal(f.editor.style.getPropertyValue('--nv-f-settings-top'),top);
 f.close();
 assert.equal(f.editor.style.getPropertyValue('--nv-f-settings-top'),'','route cleanup removes transient positioning');
 assert.equal(f.editor.querySelector('.nv-freestyle-grid'),null);
});
test('floating panel stays inside 1080p, 1200p and scaled short viewports',()=>{
 const {window}=positioningFixture();
 for(const [width,height,barBottom] of [[1920,1080,400],[1920,1200,400],[1280,720,400],[640,420,400]]){
  const box=window.__nvFreestyleSettings.bounds(width,height,{height:240,bottom:barBottom});
  assert.ok(box.left>=0&&box.left+box.width<=width);
  assert.ok(box.top>=0&&box.top+box.height<=height-19.999);
  assert.ok(box.height>0);
 }
});
