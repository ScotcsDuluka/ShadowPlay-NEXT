'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const p=path.resolve(__dirname,'../osc');
function read(rel){return fs.readFileSync(path.join(p,rel),'utf8')}
class Node {
 constructor(tag,cls=''){this.tagName=tag;this.className=cls;this.children=[];this.parentNode=null;this.textContent='';this.attrs={};this.classList={add:name=>{this.className+=' '+name}}}
 appendChild(el){if(el.parentNode)el.parentNode.removeChild(el);this.children.push(el);el.parentNode=this;return el}
 removeChild(el){let i=this.children.indexOf(el);if(i<0)throw Error('not child');this.children.splice(i,1);el.parentNode=null;return el}
 insertBefore(el,before){if(el.parentNode)el.parentNode.removeChild(el);let i=this.children.indexOf(before);if(i<0)throw Error('not child');this.children.splice(i,0,el);el.parentNode=this;return el}
 setAttribute(k,v){this.attrs[k]=v}
 querySelector(selector) {
  if(selector===':scope > footer')return this.children.find(x=>x.tagName==='footer')||null;
  let isClass=selector.startsWith('.'),key=isClass?selector.slice(1):selector;
  const all=(n)=>{for(let c of n.children){if((isClass?c.className.split(' ').includes(key):c.tagName===key))return c;let found=all(c);if(found)return found}return null};
  return all(this)
 }
}
test('existing Angular-owned background, palette and md-select footer are moved, not cloned',()=>{
 const editor=new Node('section','nv-live-editor'),head=new Node('header'),workspace=new Node('div','nv-live-workspace'),library=new Node('div','nv-live-library'),side=new Node('div','nv-live-panel'),foot=new Node('footer'),background=new Node('nv-osc-background-settings'),scope={};
 workspace.appendChild(side);workspace.appendChild(library);
 editor.appendChild(head);editor.appendChild(workspace);editor.appendChild(foot);editor.appendChild(background);
 const document={readyState:'complete',querySelector(q){return q==='.nv-live-editor'?editor:null},createElement(tag){return new Node(tag)},addEventListener(){}};
 const window={setInterval:null};
 vm.runInNewContext(read('mods/nv-freestyle-settings.js'),{window,document}, {timeout:2000});
 const grid=editor.querySelector('.nv-freestyle-grid');
 assert.ok(grid);
 const left=grid.querySelector('.nv-freestyle-background'),right=grid.querySelector('.nv-freestyle-controls');
 assert.equal(left.querySelector('nv-osc-background-settings'),background);
 assert.equal(left.querySelector('footer'),foot);
 assert.equal(right.querySelector('.nv-live-library'),library);
 assert.equal(left.querySelector('.nv-live-workspace'),workspace);
 assert.equal(workspace.querySelector('.nv-live-panel'),side);
 assert.equal(editor.querySelector('header'),head);
 assert.equal(editor.querySelector('.nv-freestyle-legacy').tagName,'details');
 assert.equal(window.__nvFreestyleSettings.scan(),undefined);
 assert.equal(editor.querySelector('.nv-freestyle-grid'),grid,'no duplicates after repeated scans');
});
test('80/20 visual contract remains scoped to original native Settings',()=>{
 const css=read('mods/nv-freestyle-settings.css'),index=read('index.html');
 assert.match(css,/grid-template-columns:minmax\(0,4fr\) minmax\(240px,1fr\)/);
 assert.match(css,/\.nv-live-editor\.nv-freestyle-settings/);
 assert.match(css,/\.nv-freestyle-legacy\[open\]/);
 assert.ok(index.includes('/mods/nv-freestyle-settings.js'));
 assert.ok(index.includes('/mods/nv-freestyle-settings.css'));
 assert.ok(index.includes('/mods/launcher-language-sync.js'));
 assert.ok(index.includes('/mods/osc-motion.js'));
});
