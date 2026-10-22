'use strict';
const fs=require('fs'),path=require('path'),{execFileSync}=require('child_process');
const run='C:/My Project/NVIDIA-Shadowplay/build_test/NVIDIA MainMenuCustomize_Test/layout-live.cjs',dir=__dirname;
function q(expression){
 const file=path.join(dir,'read-cef.js');
 fs.writeFileSync(file,expression,'utf8');
 const out=execFileSync(process.execPath,[run,file],{encoding:'utf8'});
 return JSON.parse(out).result.value;
}
const state=q("(function(){return {v4:!!window.__dulukaUnifiedMainMenu,flag:localStorage.getItem('DulukaMainMenuUnifiedV4Enabled'),data:localStorage.getItem('DulukaMainMenuUnifiedV4'),v2:localStorage.getItem('DulukaMainMenuLayoutV2')};})()");
fs.writeFileSync(path.join(dir,'recovered-orphan-v4.json'),JSON.stringify(state,null,2));
if(state.v4)throw Error('V4 still active, refusing to clear');
q("(function(){localStorage.removeItem('DulukaMainMenuUnifiedV4Enabled');localStorage.removeItem('DulukaMainMenuUnifiedV4');return true;})()");
const after=q("(function(){return {flag:localStorage.getItem('DulukaMainMenuUnifiedV4Enabled'),data:localStorage.getItem('DulukaMainMenuUnifiedV4'),v2:localStorage.getItem('DulukaMainMenuLayoutV2')}})()");
if(after.flag!==null||after.data!==null||after.v2!==state.v2)throw Error('Restoration verification failed');
console.log('ORPHANED_V4_RESTORED',JSON.stringify({v2Unchanged:true,oldSnapshotSaved:path.join(dir,'recovered-orphan-v4.json')}));
