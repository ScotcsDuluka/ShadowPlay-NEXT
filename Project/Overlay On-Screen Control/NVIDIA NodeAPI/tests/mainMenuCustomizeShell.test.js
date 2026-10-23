'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const dir=path.join(__dirname,'../osc');
const html=fs.readFileSync(path.join(dir,'index.html'),'utf8');
const css=fs.readFileSync(path.join(dir,'mods/main-menu-customize-shell.css'),'utf8');
const legacy=fs.readFileSync(path.join(dir,'mods/main-menu-live.css'),'utf8');
const group=fs.readFileSync(path.join(dir,'mods/main-menu-group-editor.css'),'utf8');

test('one scoped Customize shell stylesheet owns preview, header, editor, footer and panel layout',()=>{
  const links=[...html.matchAll(/<link\s+rel="stylesheet"\s+href="([^"]+)"/g)].map(m=>m[1]);
  const dedicated=links.filter(x=>x.includes('main-menu-customize-shell.css'));
  assert.equal(dedicated.length,1,'exactly one Customize shell link');
  assert(links.indexOf(dedicated[0])>links.findIndex(x=>x.includes('main-menu-group-editor.css')),'load last after Group stylesheet');
  assert.match(css,/html\.nv-live-layout-editing body \.nv-live-editor/);
  assert.doesNotMatch(legacy,/^html body \.nv-live-editor\s*\{/m);
  assert.doesNotMatch(group,/V5 Customize: keep every Settings section/);
  assert.equal([...css.matchAll(/nv-osc-top-bar \.top-bar\s*\{/g)].length,1);
});

test('one responsive preview boundary also drives JS native geometry',()=>{
  const native=fs.readFileSync(path.join(dir,'mods/main-menu-unified-native.js'),'utf8');
  assert.match(css,/--nv-top-preview-height:\s*300px/);
  assert.match(css,/@media\s*\(max-height:\s*1000px\)/);
  assert.match(css,/--nv-top-preview-height:\s*255px/);
  assert.match(css,/--nv-top-preview-height:\s*215px/);
  assert.match(native,/getPropertyValue\('--nv-top-preview-height'\)/);
  assert.match(css,/height:\s*var\(--nv-top-preview-height\)/);
});

test('native hit targets are not replaced or intercepted',()=>{
  assert.match(css,/\[nv-main-menu-live-editor\]::before\s*\{[^}]*pointer-events:\s*none/s);
  assert.match(css,/nv-osc-top-bar \.top-bar\s*\{[^}]*pointer-events:\s*auto\s*!important/s);
  assert.match(css,/\.nv-live-editor > \*\s*\{pointer-events:\s*auto\s*!important/);
  assert.match(css,/\.main-menu-container md-menu\s*\{visibility:\s*visible\s*!important/);
});

test('bottom sections never get display:none and short screens have page-scroll fallback',()=>{
  assert.match(css,/> nv-osc-background-settings\s*\{[^}]*overflow-y:\s*auto/s);
  assert.match(css,/@media\s*\(max-height:\s*1000px\)\s*\{/);
  assert.match(css,/overflow-y:\s*auto\s*!important/);
  assert.doesNotMatch(css,/nv-osc-background-settings[^}]+display:\s*none/s);
});

test('CEF-compatible explicit geometry edges keep Settings 40px inset',()=>{
  const m=css.match(/html\.nv-live-layout-editing body \.nv-live-editor,[\s\S]*?\.nv-unified-editor-active\s*\{([^}]*)\}/);
  assert(m,'Customize shell geometry rule missing');
  const rules=m[1];
  for(const edge of ['top','right','bottom','left']){
    assert.match(rules,new RegExp('\\b'+edge+':\\s*40px\\s*!important'));
  }
  assert.match(rules,/width:\s*calc\(100vw\s*-\s*80px\)\s*!important/);
  assert.match(rules,/height:\s*calc\(100vh\s*-\s*80px\)\s*!important/);
  assert.doesNotMatch(rules,/\binset\s*:/);
  assert.doesNotMatch(rules,/calc\([^)]*\*/);
});