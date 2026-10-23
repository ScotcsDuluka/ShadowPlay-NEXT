'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const css=fs.readFileSync(path.join(__dirname,'../osc/mods/main-menu-customize-shell.css'),'utf8');
test('Customize shows the genuine NVIDIA OSC top bar and preserves its native click handling',()=>{
 const rules=Array.from(css.matchAll(/html\.nv-live-layout-editing\s+nv-osc-top-bar\s+\.top-bar\s*\{([^}]*)\}/g),x=>x[1]);
 assert.equal(rules.length,1,'only one Customize top bar override is allowed');
 const rule=rules[0];
 assert.match(rule,/visibility\s*:\s*visible\s*!important/);
 assert.match(rule,/pointer-events\s*:\s*auto\s*!important/);
 assert.match(rule,/position\s*:\s*relative/);
 assert.match(rule,/z-index\s*:\s*(?:8[0-9]|9[0-9])/);
 assert.doesNotMatch(rule,/visibility\s*:\s*hidden|pointer-events\s*:\s*none/);
});
test('Top bar still belongs to NVIDIA; motion initial hiding is preserved',()=>{
 const motion=fs.readFileSync(path.join(__dirname,'../osc/mods/osc-motion.css'),'utf8');
 assert.match(motion,/nv-motion-preparing\s+nv-osc-top-bar\s+\.top-bar\s*\{opacity:0!important\}/);
 assert.match(css,/html\.nv-live-layout-editing body \.nv-live-editor[^{]*\{[^}]*z-index:\s*75\s*!important/s);
});
