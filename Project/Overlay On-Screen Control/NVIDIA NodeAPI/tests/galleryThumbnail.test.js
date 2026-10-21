const test=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),vm=require('vm'),crypto=require('crypto'),os=require('os');
const source=fs.readFileSync(path.join(__dirname,'../NvGalleryAPI.js'),'utf8');
test('concurrent thumbnail callers all complete and release the job after generation',async()=>{
 const code=source.slice(source.indexOf('function thumbnailDataUri('),source.indexOf('function fallbackRecentFiles('));let ready=false,runs=0;
 const pending={};const fakeFs={readFile(file,cb){setImmediate(()=>ready?cb(null,Buffer.from('png')):cb({code:'ENOENT'}));},mkdir(dir,options,cb){cb(null)},rename(a,b,cb){ready=true;cb(null)},unlink(file,cb){cb(null)}};
 const context={fs:fakeFs,path,crypto,os,thumbnailJobs:pending,VIDEO_EXTENSIONS:{'.mp4':true},findFfmpegPath:()=>'/ffmpeg',execFile(file,args,opts,cb){runs++;setImmediate(()=>cb(null))},process:{pid:123},_logger:{error(){}}};vm.createContext(context);vm.runInContext(code,context);
 const get=()=>new Promise((r,j)=>context.thumbnailDataUri('/video.mp4',{size:42,mtimeMs:1},160,(err,data)=>err?j(err):r(data)));
 const values=await Promise.all([get(),get(),get()]);assert.equal(values.length,3);assert(values.every(x=>x==='data:image/png;base64,cG5n'));assert.equal(runs,1);assert.equal(Object.keys(pending).length,0);assert.equal(await get(),values[0]);assert.equal(runs,1);
});
test('deployed runtime resolves its bundled Capture FFmpeg',()=>{
 const code=source.slice(source.indexOf('function findFfmpegPath('),source.indexOf('function prepareRecordingPlayback('));
 const dir=path.resolve(__dirname,'../../../..','build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI');
 const context={fs,path,__dirname:dir,process:{env:{}},_logger:{error(){}}};vm.createContext(context);vm.runInContext(code,context);
 assert.equal(context.findFfmpegPath(),path.resolve(dir,'../../NVIDIA Capture/FFmpeg/ffmpeg.exe'));
});

test('seek preparation encodes only eight seconds at the requested position',async()=>{
 const code=source.slice(source.indexOf('function prepareRecordingPlayback('),source.indexOf('function thumbnailDataUri('));let args;
 const mockFs={stat(file,cb){if(file.endsWith('ffprobe.exe')||file.includes('.tmp.webm'))cb(null,{isFile:()=>true,size:42});else cb({code:'ENOENT'})},mkdir(dir,opts,cb){cb(null)},unlink(file,cb){cb({code:'ENOENT'})},rename(a,b,cb){cb(null)}};
 const context={fs:mockFs,path,crypto,os,process:{pid:123},playbackJobs:{},findFfmpegPath:()=>'/ffmpeg.exe',execFile(file,argv,opts,cb){if(file.endsWith('ffprobe.exe'))cb(null,JSON.stringify({streams:[{codec_type:'video'}]}));else{args=argv;cb(null)}}};
 vm.createContext(context);vm.runInContext(code,context);
 await new Promise((r,j)=>context.prepareRecordingPlayback({filename:'/clip.mp4',stats:{size:123,mtimeMs:1}},(err,result)=>err?j(err):r(result),1200,'video'));
 assert.equal(args[args.indexOf('-ss')+1],'1200');assert(args.indexOf('-ss')<args.indexOf('-i'));
 assert.equal(args[args.indexOf('-t')+1],'8');assert.equal(args[args.indexOf('-threads')+1],'2');
 assert(args.includes('-an'));assert(!args.includes('0:a:0?'));
 await new Promise((r,j)=>context.prepareRecordingPlayback({filename:'/clip.mp4',stats:{size:123,mtimeMs:1}},(err,result)=>err?j(err):r(result),1200,'audio'));
 assert(args.includes('-vn'));assert.equal(args[args.indexOf('-c:a')+1],'libvorbis');
 assert.equal(Object.keys(context.playbackJobs).length,0);
});

test('folder media entries retain the native type used by the Gallery editor',async()=>{
 const code=source.slice(source.indexOf('function fallbackFolderListing('),source.indexOf('function readMediaFiles('));
 const context={path,VIDEO_EXTENSIONS:{'.mp4':true},IMAGE_EXTENSIONS:{'.png':true},fs:{readdir(dir,opts,cb){cb(null,['clip.mp4','shot.png','notes.txt'].map(name=>({name,isDirectory:()=>false,isFile:()=>true})))}}};
 vm.createContext(context);vm.runInContext(code,context);
 const listing=await new Promise((r,j)=>context.fallbackFolderListing({directory:'/recordings'},(err,data)=>err?j(err):r(data)));
 assert.equal(listing.files.find(f=>f.name==='clip.mp4').type,'video');assert.equal(listing.files.find(f=>f.name==='shot.png').type,'image');
 assert.equal(listing.files.find(f=>f.name==='clip.mp4').subtype,'Normal');assert.equal(listing.files.find(f=>f.name==='notes.txt').type,undefined);
});
