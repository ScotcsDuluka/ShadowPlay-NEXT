'use strict';
const fs=require('fs'),path=require('path');
const osc=path.resolve(__dirname,'../..');
const manifest=JSON.parse(fs.readFileSync(path.join(osc,'Module/module-manifest.json'),'utf8'));
const assetMap=new Map(manifest.assets.map(a=>[a.source,a]));
const ownerMap=Object.fromEntries(manifest.assets.flatMap(a=>[[a.source,a.owner],[a.module,a.owner]]));
const mapFile=path.join(__dirname,'module-map.js');
const mapText="/* Generated ownership map for optional, diagnostic-only script gate. */\n(function(window){'use strict';window.__spnModuleOwners=Object.freeze("+JSON.stringify(ownerMap)+");})(window);\n";
const presets={
 'all':{omit:[],filename:'index.module.html'},
 'core-only':{omit:Object.keys(manifest.moduleGroups),filename:'index.core-only.html'},
 'without-v5':{omit:['mainmenu.freestyle-v5'],filename:'index.module-no-v5.html'},
 'without-motion':{omit:['overlay.motion'],filename:'index.module-no-motion.html'}
};
function generate(name){
 if(!Object.prototype.hasOwnProperty.call(presets,name))throw Error('Unknown profile '+name);
 const profile=presets[name],disabled=new Set(profile.omit);
 const source=fs.readFileSync(path.join(osc,'index.html'),'utf8');
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
 const anchor='<script src="/cefquery-shim.js"></script>';
 if(updated.split(anchor).length!==2)throw Error('CEF shim anchor missing/multiplied');
 const marker=anchor+'\n  <script src="/Module/Core/module-map.js"></script>\n  <script src="/Module/Core/module-runtime.js?profile='+encodeURIComponent(name)+'&omit='+encodeURIComponent(profile.omit.join(','))+'"></script>';
 const html=updated.replace(anchor,marker),output=path.join(osc,profile.filename);
 return {name,output,html,taggedAssets,activeAssets,omitted:profile.omit};
}
function writeProfile(name){
 const result=generate(name);
 if(fs.existsSync(result.output)&&!fs.readFileSync(result.output,'utf8').includes('/Module/Core/module-runtime.js?profile='))throw Error('Refusing to replace unknown '+result.output);
 fs.writeFileSync(mapFile,mapText,'utf8');
 fs.writeFileSync(result.output,result.html,'utf8');
 const {html,...summary}=result;
 return summary;
}
if(require.main===module){
 try{for(const name of process.argv.length>2?process.argv.slice(2):Object.keys(presets))console.log(JSON.stringify(writeProfile(name)))}
 catch(e){console.error(e.stack||String(e));process.exitCode=1}
}
module.exports={generate,writeProfile,presets,manifest};
