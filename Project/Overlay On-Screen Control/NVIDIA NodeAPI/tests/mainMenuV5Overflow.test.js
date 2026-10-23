'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-customize-shell.css'),'utf8');
const live=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-customize-shell.css'),'utf8');
function block(selector){
 const start=css.indexOf(selector+' {');assert(start>=0,selector+' missing');
 const from=css.indexOf('{',start),to=css.indexOf('}',from);
 return css.slice(from+1,to);
}
test('V5 fullscreen Settings uses a finite, contained flex layout without changing native 40px shell',()=>{
 const host=block('html.nv-live-layout-editing body .nv-live-editor.nv-unified-editor-active');
 assert.match(host,/display:\s*flex\s*!important/);
 assert.match(host,/flex-direction:\s*column\s*!important/);
 assert.match(host,/overflow-y:\s*hidden\s*!important/);
 assert.match(live,/--nv-customize-inset:\s*40px/);
});
test('V5 Group panels shrink to an actual grid track and their long lists scroll separately',()=>{
 const editor=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on > .nv-group-layout-editor');
 const panels=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on .nv-g-panels');
 const group=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on .nv-g-panel');
 assert.match(editor,/flex:\s*1 1 auto/);
 assert.match(editor,/min-height:\s*0/);
 assert.match(editor,/overflow:\s*hidden/);
 assert.match(panels,/grid-template-rows:\s*minmax\(0,1fr\)/);
 assert.match(panels,/min-height:\s*0/);
 assert.match(group,/min-height:\s*0/);
 assert.match(group,/overflow:\s*hidden/);
 assert.match(css,/\.nv-g-group-list\s*\{[^}]*overflow-y:\s*auto/);
});
test('Footer remains in shell and Background Settings stays accessible via independent scrolling',()=>{
 const bg=block('html.nv-live-layout-editing body .nv-live-editor.nv-group-layout-on > nv-osc-background-settings');
 assert.match(bg,/display:\s*block\s*!important/);
 assert.match(bg,/flex:\s*0 0 var\(--nv-customize-background-height\)/);
 assert.match(bg,/overflow-y:\s*auto\s*!important/);
 assert.match(css,/\.nv-live-editor\.nv-group-layout-on > footer\s*\{[^}]*margin:\s*6px 0 8px/);
 assert.match(css,/\.nv-g-search\s*\{[^}]*box-sizing:\s*border-box/);
});
test('Short windows keep whole-page scrolling as fallback instead of hiding Controls',()=>{
 const fallback=css.substring(css.indexOf('@media (max-height: 1000px)'));
 assert.match(fallback,/overflow-y:\s*auto\s*!important/);
 assert.match(fallback,/grid-template-rows:\s*auto/);
 assert.match(fallback,/max-height:\s*none/);
});
