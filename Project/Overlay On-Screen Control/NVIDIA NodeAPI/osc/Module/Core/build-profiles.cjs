'use strict';
const fs=require('fs'),path=require('path'),crypto=require('node:crypto');
const osc=path.resolve(__dirname,'../..');
const manifestPath=path.join(osc,'Module/module-manifest.json');
const manifest=JSON.parse(fs.readFileSync(manifestPath,'utf8'));
const assetMap=new Map(manifest.assets.map(a=>[a.source,a]));
const ownerMap=Object.fromEntries(manifest.assets.flatMap(a=>[[a.source,a.owner],[a.module,a.owner]]));
const mapFile=path.join(__dirname,'module-map.js');
const mapText="/* Generated ownership map for optional, diagnostic-only script gate. */\n(function(window){'use strict';window.__spnModuleOwners=Object.freeze("+JSON.stringify(ownerMap)+");})(window);\n";
// index.legacy.html is the pristine NVIDIA+mods bootstrap (rollback target and
// generator source). index.html is the generated production page after the
// 2026-10-29 Mods shutdown; never hand-edit it, regenerate instead.
const legacyName='index.legacy.html';
function legacySource(){
 const file=path.join(osc,legacyName);
 if(!fs.existsSync(file))throw Error('Missing '+legacyName+' — restore it before regenerating profiles');
 return fs.readFileSync(file,'utf8');
}
// Mirror of the NodeAPI GET /mods/list allowlist (the retired host-injection
// contract). Keep in sync with the env-gated branch in NodeAPI index.js.
const legacyAllowlist=['ansel-game-filter.js','base.css','folder-browser-navigation.js','gallery-media-source.js','gpu-profiles.js','gpu-profiles.css','gpu-status-panel.css','gpu-status-panel.js','notification-settings.js','notifications.css','overlays.css','preferences.css','privacy-control.css','providers.css','reshade-manager.css','reshade-manager.js','video-capture-options.css','video-capture-options.js'];
const presets={
 'all':{omit:[],filename:'index.module.html'},
 'core-only':{omit:Object.keys(manifest.moduleGroups),filename:'index.core-only.html'},
 'without-v5':{omit:['mainmenu.freestyle-v5'],filename:'index.module-no-v5.html'},
 'without-motion':{omit:['overlay.motion'],filename:'index.module-no-motion.html'},
 'production':{omit:[],filename:'index.production.html',production:true}
};
function generate(name){
 if(!Object.prototype.hasOwnProperty.call(presets,name))throw Error('Unknown profile '+name);
 const profile=presets[name],disabled=new Set(profile.omit);
 const source=legacySource();
 let taggedAssets=0,activeAssets=0;
 const updated=source.replace(/<(?:script|link)\b[^>]*?(?:src|href)=["'][^"']+["'][^>]*>(?:<\/script>)?/gi,function(tag){
   const match=tag.match(/(?:src|href)=["']([^"']+)["']/i);if(!match)return tag;
   const url=match[1],base=url.split('?')[0];
   if(name==='core-only'&&base==='/nvcamera-test-mode.js')return '';
   if(!base.startsWith('/mods/'))return tag;
   const asset=assetMap.get(base);if(!asset)throw Error('Missing owner '+base);
   taggedAssets++;if(disabled.has(asset.owner))return '';
   activeAssets++;return tag.replace(url,asset.module+url.substring(base.length));
 });
 let html;
 if(profile.production){
   if(/\/mods\//i.test(updated))throw Error('production profile must not reference /mods/');
   const closer='</body>';
   if(updated.split(closer).length!==2)throw Error('body closer missing/multiplied');
   const loader='\n    <script src="/Module/Core/late-extensions.js"></script>\n';
   html=updated.replace(closer,loader+closer);
 }else{
   const anchor='<script src="/cefquery-shim.js"></script>';
   if(updated.split(anchor).length!==2)throw Error('CEF shim anchor missing/multiplied');
   const marker=anchor+'\n  <script src="/Module/Core/module-map.js"></script>\n  <script src="/Module/Core/module-runtime.js?profile='+encodeURIComponent(name)+'&omit='+encodeURIComponent(profile.omit.join(','))+'"></script>';
   html=updated.replace(anchor,marker);
 }
 const output=path.join(osc,profile.filename);
 return {name,output,html,taggedAssets,activeAssets,omitted:profile.omit};
}
function writeProfile(name){
 const result=generate(name);
 if(!presets[name].production&&fs.existsSync(result.output)&&!fs.readFileSync(result.output,'utf8').includes('/Module/Core/module-runtime.js?profile='))throw Error('Refusing to replace unknown '+result.output);
 if(name==='all')fs.writeFileSync(mapFile,mapText,'utf8');
 fs.writeFileSync(result.output,result.html,'utf8');
 const {html,...summary}=result;
 return summary;
}
// Assets the retired host injection used to load but the legacy page does not
// reference statically. The production page loads them through late-extensions.js
// at the same post-bootstrap timing from /Module/ paths.
function lateAssets(){
 const source=legacySource();
 const referenced=new Set();
 for(const a of manifest.assets){
  const base=a.source;
  if(source.includes(base+'"')||source.includes(base+'?')||source.includes(a.module+'"')||source.includes(a.module+'?'))referenced.add(base);
 }
 return legacyAllowlist.filter(function(name){
  const src='/mods/'+name;
  const asset=assetMap.get(src);
  if(!asset)throw Error('Allowlist asset missing from manifest: '+src);
  return !referenced.has(src);
 }).map(function(name){
  const asset=assetMap.get('/mods/'+name);
  const stat=fs.statSync(path.join(osc,asset.module.replace(/^\//,'')));
  return {name,source:asset.source,module:asset.module,v:String(Math.round(stat.mtimeMs))+'-'+String(stat.size)};
 });
}
function writeLateManifest(){
 const assets=lateAssets();
 const payload={generatedFrom:legacyName,note:'Post-bootstrap extension loader manifest; generated by build-profiles.cjs, do not hand-edit.',assets:assets};
 fs.writeFileSync(path.join(__dirname,'late-manifest.json'),JSON.stringify(payload,null,2)+'\n','utf8');
 return {count:assets.length,output:'Module/Core/late-manifest.json'};
}
function sha(file){return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');}
// Recompute manifest asset hashes from the current Source mods content and
// record the legacy page hash. Used after a deliberate Source/Build content sync.
function refreshHashes(){
 let changed=[];
 for(const a of manifest.assets){
  const next=sha(path.join(osc,a.source.replace(/^\//,'')));
  if(next!==a.sha256){changed.push(a.source);a.sha256=next;}
 }
 const legacySha=sha(path.join(osc,legacyName));
 if(manifest.legacyIndexSha!==legacySha){if(manifest.legacyIndexSha!==undefined)changed.push(legacyName);manifest.legacyIndexSha=legacySha;}
 fs.writeFileSync(manifestPath,JSON.stringify(manifest,null,2)+'\n','utf8');
 return {changed,legacyIndexSha:legacySha};
}
if(require.main===module){
 try{
  const args=process.argv.slice(2);
  if(args.includes('--refresh-hashes')){console.log(JSON.stringify(refreshHashes()));}
  else if(args.includes('--late-manifest')){console.log(JSON.stringify(writeLateManifest()));}
  else{for(const name of args.length?args:Object.keys(presets))console.log(JSON.stringify(writeProfile(name)));}
 }
 catch(e){console.error(e.stack||String(e));process.exitCode=1}
}
module.exports={generate,writeProfile,presets,manifest,legacyAllowlist,lateAssets,writeLateManifest,refreshHashes};
