'use strict';
// Scoped asset-only release for Launcher.Cef's Core / Config Module UI.
// Never touches executables, live module catalog, external files, or native services.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const child=require('node:child_process');
const base=path.resolve(__dirname,'..');
const root=path.resolve(base,'..','..');
const source=path.join(base,'ui');
const build=path.join(root,'build','NVIDIA ShadowPlay','NVIDIA Launcher','Cef','Resources','launcher');
const backupRoot=path.join(root,'build','DeploymentBackups');
const files=['index.html','quick-ui.css','quick-ui.js','module-profiles.js','i18n.js'];
const deploy=process.argv.includes('--deploy');
const digest=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
if(!fs.existsSync(build)||!fs.existsSync(path.join(build,'index.html')))
  throw Error('No verified Launcher UI build destination: '+build);
const html=fs.readFileSync(path.join(source,'index.html'),'utf8');
if(!html.includes('id="quickCoreGrid"')||
   (html.match(/data-lifecycle="suite-managed"/g)||[]).length!==11||
   (html.match(/class="quick-module-head"/g)||[]).length!==11||
   html.includes('quick-module-readonly')||html.includes('quick-core-lock')||
   html.includes('id="quickPageConfig"')||html.includes('data-config-module=')||
   html.includes('src="module-profiles.js'))
  throw Error('Core modules must be read-only; editable UI or wrong inventory');
for(const file of files){
 const p=path.join(source,file);if(!fs.existsSync(p))throw Error('No source '+file);
 if(file.endsWith('.js')){
  const check=child.spawnSync(process.execPath,['--check',p],{encoding:'utf8'});
  if(check.status!==0)throw Error('JS syntax '+file+' '+check.stderr);
 }
}
const cases=[
 path.join(base,'tests','quick-ui.test.cjs'),
 path.join(base,'tests','appearance-profile.test.cjs'),
 path.join(base,'tests','command-center-ui.test.cjs'),
 path.join(base,'tests','languageCoverage.test.cjs')
];
const tests=child.spawnSync(process.execPath,['--test',...cases],{encoding:'utf8'});
if(tests.status!==0)throw Error('Launcher regression failed '+tests.stdout.slice(-3200));
const changed=files.filter(name=>digest(path.join(source,name))!==digest(path.join(build,name)));
console.log('PREFLIGHT tests=PASS source='+source+' runtime='+build);
console.log('MODE='+(deploy?'deploy':'dry-run')+' changed='+changed.join(','));
if(!deploy)process.exit(0);
const stamp=new Date().toISOString().replace(/[:.]/g,'-');
const backup=path.join(backupRoot,'ModuleConfig-'+stamp);
fs.mkdirSync(backup,{recursive:true});
const installed=[];
try{
 for(const name of changed){
  const from=path.join(source,name),to=path.join(build,name);
  fs.copyFileSync(to,path.join(backup,name));
  // Atomic replacement using same-directory temporary file.
  const temp=to+'.module-config-next.tmp';
  fs.copyFileSync(from,temp);
  if(digest(temp)!==digest(from))throw Error('Stage digest mismatch '+name);
  fs.renameSync(temp,to);
  if(digest(to)!==digest(from))throw Error('Deploy digest mismatch '+name);
  installed.push(name);
 }
 const evidence={at:new Date().toISOString(),backup,source,build,tests:'PASS',
  files:installed.map(name=>({name,sha256:digest(path.join(build,name))}))};
 const proof=path.join(backupRoot,'module-config-deploy-latest.json');
 fs.writeFileSync(proof,JSON.stringify(evidence,null,2)+'\n');
 console.log('DEPLOY_OK sha256='+installed.length+'/'+installed.length+' backup='+backup+' proof='+proof);
}catch(error){
 for(const name of installed.reverse()){
  const original=path.join(backup,name);
  if(fs.existsSync(original))fs.copyFileSync(original,path.join(build,name));
 }
 throw error;
}
