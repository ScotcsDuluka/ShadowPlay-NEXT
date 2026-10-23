'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-customize-shell.css'),'utf8');

test('Customize top band has a solid backdrop behind NVIDIA native menu rather than transparent desktop',()=>{
 const selector=/html\.nv-live-layout-editing[^{}]*body \[nv-main-menu-live-editor\]::before\s*\{([^}]*)\}/;
 const m=css.match(selector);assert(m,'backdrop must be scoped to the Customize UI');
 const block=m[1];
 assert.match(block,/content\s*:\s*""/);
 assert.match(block,/position\s*:\s*fixed/);
 assert.match(block,/background\s*:\s*#101617/);
 assert.match(block,/pointer-events\s*:\s*none/,'backdrop cannot capture clicks');
 assert.match(block,/z-index\s*:\s*1/,'backdrop must sit below NVIDIA native rail z-index 2');
 assert.match(css,/html\.nv-live-layout-editing\s*\{[\s\S]*?--nv-top-preview-height\s*:\s*300px/);
 assert.match(css,/@media\s*\(max-height:\s*780px\)\s*\{[\s\S]*?--nv-top-preview-height:\s*215px/);
});

test('native buttons and Settings controls remain mouse-reachable with the V5 backdrop',()=>{
 assert.match(css,/html\.nv-live-layout-editing body \.nv-live-editor[^{]*\{[^}]*pointer-events\s*:\s*none\s*!important/s);
 assert.match(css,/html\.nv-live-layout-editing body \.nv-live-editor > \*\s*\{\s*pointer-events\s*:\s*auto\s*!important/);
 assert.match(css,/html\.nv-live-layout-editing body \.main-menu-container md-menu\s*\{\s*visibility\s*:\s*visible\s*!important/);
 assert.doesNotMatch(css,/\[nv-main-menu-live-editor\]::before[^}]*pointer-events\s*:\s*auto/s);
});
