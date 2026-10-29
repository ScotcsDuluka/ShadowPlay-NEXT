'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');

test('NVIDIA Keyboard shortcuts Back/Reset controls are compact and scoped',()=>{
 const css=fs.readFileSync(path.join(__dirname,'../osc/Module/Base/UI/base.css'),'utf8');
 const scope='nv-preferences-keyboard-shortcuts .osc-side-buttons > .osc-button';
 const start=css.indexOf(scope+' {');
 assert(start!==-1,'must target original NVIDIA keyboard shortcuts buttons only');
 const styles=css.slice(start,css.indexOf('}',start));
 for(const s of ['width: 184px !important','height: 46px !important',
 'margin: 0 0 14px 0 !important','font-size: 15px !important',
 'box-sizing: border-box !important'])assert(styles.includes(s),s);
 assert(css.includes(scope+':focus'));
 assert(css.includes(scope+':hover'));
});
