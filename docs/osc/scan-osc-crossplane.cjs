'use strict';
// Cross-plane string inventory only: no services started or mutations performed.
const fs = require('fs'), path=require('path');
const project = path.resolve(__dirname,'../../Project');
const nodeRoot=path.join(project,'Overlay On-Screen Control','NVIDIA NodeAPI');
function source(relative){return fs.readFileSync(path.join(project,relative),'utf8');}
function uniq(content,regex){return [...new Set([...content.matchAll(regex)].map(x=>x[1]||x[0]))].sort();}
const floor=uniq(source('Overlay On-Screen Control/NVIDIA NodeAPI/shims/route-floor.js'),
  /['"`](\/(?:Duluka|ShadowPlay|NvCamera|OBS|Notifier|SDK|v1\/)[a-zA-Z0-9\/_\.\-]+)['"`]/g);
const container=uniq(source('NVIDIA Container/Components/Diagnostics/ContainerTestApi.cs'),
  /["](\/v1\/[a-zA-Z0-9\/_\.\-]+)["]/g);
const capture=uniq(source('NVIDIA Capture/src/[API]/CaptureRestApiServer.vb'),
  /["](\/(?:health|state|engines|displays|record\/start|record\/stop|display\/select|screenshot|encoders\/verify)[a-zA-Z0-9\/_\.\-]*)["]/g);
const cpp=source('Overlay On-Screen Control/NVIDIA OSC/src/osc_main.cpp');
const native=uniq(cpp,/\b(QUERY_[A-Z0-9_]+)\b/g);
const modsPath=path.join(nodeRoot,'osc','mods');
const frontend=[];
for(const filename of fs.readdirSync(modsPath).filter(x=>x.endsWith('.js'))) {
  const text=fs.readFileSync(path.join(modsPath,filename),'utf8');
  const endpoints=uniq(text,/['"`](\/(?:Duluka|ShadowPlay|NvCamera|Gallery|OBS|Notifier|Backend|SDK|v1\/)[a-zA-Z0-9\/_\.\-]+)['"`]/g);
  if(endpoints.length) frontend.push({filename,endpoints});
}
frontend.sort((a,b)=>a.filename.localeCompare(b.filename));
const summary=[
 '# OSC / Backend — Cross-plane Static Inventory',
 '',
 'Generated '+new Date().toISOString()+' from Project source. This is a **source-string** index, not proof of actual route ownership, allowed HTTP verb or runtime availability.',
 '',
 '## 1. NodeAPI route-floor and compatibility path literals',
 '',
 'Found '+floor.length+' unique strings (includes REST facades and upstream target literals; not a route declaration count).',
 '',
 ...floor.map(x=>'- `'+x+'`'),
 '',
 '## 2. NvContainer HTTP API route strings',
 '',
 'Found '+container.length+' concrete literal paths in `Components/Diagnostics/ContainerTestApi.cs`; C# switch/prefix conditions may add patterns.',
 '',
 ...container.map(x=>'- `'+x+'`'),
 '',
 '## 3. NvCapture worker API route strings',
 '',
 'Found '+capture.length+' known API literals in `src/API/CaptureRestApiServer.vb`.',
 '',
 ...capture.map(x=>'- `'+x+'`'),
 '',
 '## 4. Native OSC CEF query command symbols',
 '',
 'Found '+native.length+' unique `QUERY_*` symbols in `osc_main.cpp` (includes implementation and references; not all are separate handlers).',
 '',
 ...native.map(x=>'- `'+x+'`'),
 '',
 '## 5. UI mods referencing path literals',
 '',
 ...frontend.flatMap(x=>['### '+x.filename+' ('+x.endpoints.length+')', ...x.endpoints.map(y=>'- `'+y+'`'), '']),
 '## Interpretation',
 '',
 'Use this index together with `OSC-BACKEND-ROUTE-INVENTORY-20261025.md` (336 direct NodeAPI method/path declarations) and `OSC-BACKEND-FULL-ARCHITECTURE-AUDIT-20261025.md` (runtime readiness and safety). Some routes build paths dynamically, so a string scan is necessarily incomplete.',
 ''
].join('\n');
const out=path.join(__dirname,'OSC-BACKEND-CROSSPLANE-INVENTORY-20261025.md');
fs.writeFileSync(out,summary,'utf8');
console.log(JSON.stringify({floor:floor.length,container:container.length,capture:capture.length,nativeQueries:native.length,frontendModules:frontend.length,frontendReferences:frontend.reduce((s,x)=>s+x.endpoints.length,0),out},null,2));
