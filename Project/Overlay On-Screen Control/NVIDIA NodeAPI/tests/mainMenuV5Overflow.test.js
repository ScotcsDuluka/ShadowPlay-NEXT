'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-group-editor.css'),'utf8');
const live=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-live.css'),'utf8');
function block(selector){
 const start=css.indexOf(selector+' {');assert(start>=0,selector+' missing');
 const from=css.indexOf('{',start),to=css.indexOf('}',from);
 return css.slice(from+1,to);
}
test('V5 fullscreen Settings uses a finite, contained flex layout without changing native 40px shell',()=>{
 const host=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on');
 assert.match(host,/display:flex!important/);
 assert.match(host,/flex-direction:column!important/);
 assert.match(host,/overflow-y:hidden!important/);
 assert.match(live,/html body \.nv-live-editor\s*\{[\s\S]*?inset:\s*40px\s*!important/);
});
test('V5 Group panels shrink to an actual grid track and their long lists scroll separately',()=>{
 const editor=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on > .nv-group-layout-editor');
 const panels=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on .nv-g-panels');
 const group=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on .nv-g-panel');
 assert.match(editor,/flex:1 1 auto/);
 assert.match(editor,/min-height:0/);
 assert.match(editor,/overflow:hidden/);
 assert.match(panels,/grid-template-rows:minmax\(0,1fr\)/);
 assert.match(panels,/min-height:0/);
 assert.match(group,/min-height:0/);
 assert.match(group,/overflow:hidden/);
 assert.match(css,/\.nv-g-group-list\{[^}]*overflow-y:auto/);
});
test('Footer remains in shell and Background Settings stays accessible via independent scrolling',()=>{
 const bg=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on > nv-osc-background-settings');
 assert.match(bg,/display:block!important/);
 assert.match(bg,/flex:0 0 110px/);
 assert.match(bg,/overflow-y:auto!important/);
 assert.match(css,/\.nv-live-editor\.nv-group-layout-on > footer\s*\{\s*margin-top:3px/);
 assert.match(css,/\.nv-g-search\{[^}]*box-sizing:border-box/);
});
test('Short windows keep whole-page scrolling as fallback instead of hiding Controls',()=>{
 const fallback=css.substring(css.indexOf('@media(max-height:1050px)'));
 assert.match(fallback,/overflow-y:auto!important/);
 assert.match(fallback,/grid-template-rows:auto/);
 assert.match(fallback,/max-height:none/);
});
