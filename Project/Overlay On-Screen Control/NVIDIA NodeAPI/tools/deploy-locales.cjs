'use strict';
// Scoped release asset stage/deploy for OSC locales. No native processes are stopped.
// Usage: node tools/deploy-locales.cjs --dry-run | --deploy
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const cp = require('node:child_process');
const project = path.resolve(__dirname, '..');
const repository = path.resolve(project, '..', '..', '..');
const source = path.join(project, 'osc');
const dest = path.join(repository, 'build', 'NVIDIA ShadowPlay',
  'Overlay On-Screen Control', 'NVIDIA NodeAPI', 'osc');
const output = path.join(repository, 'build_test', 'Launcher Localization_Test');
const mode = process.argv.includes('--deploy') ? 'deploy' : 'dry-run';
const dirs = ['l10n', 'Module'];
const langs = fs.readdirSync(path.join(source,'l10n')).filter(x=>/^[a-z]{2}-[A-Z]{2,3}\.json$/.test(x)).sort();
const files = [
  ...langs.map(x=>path.join('l10n',x)),
  path.join('Module','Base','Localization','launcher-language-sync.js'),
  'index.html'
];
const digest = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const hashOfBuffer = data => crypto.createHash('sha256').update(data).digest('hex');
const stateFile = path.join(dest,'launcher-language.json');
const stateHash = fs.existsSync(stateFile) ? digest(stateFile) : null;
const check = cp.spawnSync(process.execPath, [path.join(__dirname,'locale-coverage.cjs')], {encoding:'utf8'});
if (check.status !== 0) throw new Error('Locale coverage failed: '+check.stdout+' '+check.stderr);
if (!fs.existsSync(dest)) throw new Error('Installed OSC directory missing '+dest);
for (const dir of dirs) if(!fs.statSync(path.join(dest,dir)).isDirectory()) throw new Error('Missing '+dir);
const srcIndex = fs.readFileSync(path.join(source,'index.html'),'utf8');
const installedIndex = fs.readFileSync(path.join(dest,'index.html'),'utf8');
const oldVersion = 'v=20261022-shared-locale';
const newVersion = 'v=20261024-language-audit';
if (srcIndex !== installedIndex.replace(oldVersion,newVersion) && srcIndex !== installedIndex)
  throw new Error('index.html has other undeployed changes; refusing to overwrite');
if (!srcIndex.includes(newVersion)) throw new Error('OSC asset cache bust missing');
const changes = files.filter(rel=> !fs.existsSync(path.join(dest,rel)) ||
  digest(path.join(source,rel)) !== digest(path.join(dest,rel)));
console.log('MODE='+mode+' SOURCE='+source+' TARGET='+dest);
console.log('FILES='+files.length+' DIFFERENT='+changes.length+' LOCALES='+langs.length);
for(const rel of changes)console.log('CHANGE '+rel);
if (mode==='dry-run')process.exit(0);
const backup = path.join(output,'PreviousVersions',
  'Language-'+new Date().toISOString().replace(/[:.]/g,'-'));
fs.mkdirSync(backup,{recursive:true});
const changed = [];
try{
  for(const rel of changes){
    const old = path.join(dest,rel), next = path.join(source,rel);
    const archived = path.join(backup,rel);
    fs.mkdirSync(path.dirname(archived),{recursive:true});
    if(fs.existsSync(old))fs.copyFileSync(old,archived);
    fs.copyFileSync(next,old);
    if(digest(next)!==digest(old))throw new Error('SHA-256 mismatch: '+rel);
    changed.push(rel);
  }
  if ((fs.existsSync(stateFile) ? digest(stateFile) : null)!==stateHash)
    throw new Error('Runtime Launcher language state changed during deployment');
  const proof = {
    mode, deployedAt:new Date().toISOString(), backup, source, destination:dest,
    changed:changed.map(rel=>({path:rel,sha256:digest(path.join(dest,rel))})),
    checked:files.length, coverageKeys:Object.keys(JSON.parse(fs.readFileSync(path.join(source,'l10n','en-US.json'),'utf8').replace(/^\uFEFF/,''))).length
  };
  const evidenceFile = path.join(output,'LANGUAGE-DEPLOY-20261024.json');
  fs.writeFileSync(evidenceFile,JSON.stringify(proof,null,2)+'\n');
  console.log('DEPLOY_OK changed='+changed.length+' sha256_verified='+changed.length+
    ' backup='+backup+' proof='+evidenceFile);
}catch(error){
  console.error('ROLLBACK_ATTEMPT '+error.message);
  for(const rel of changed.reverse()){
    const saved=path.join(backup,rel),installed=path.join(dest,rel);
    if(fs.existsSync(saved))fs.copyFileSync(saved,installed);
  }
  throw error;
}
