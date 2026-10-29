/* ShadowPlay NEXT video intro regression. Mock CEF73 DOM; no GUI/network. */
'use strict';
const assert=require('node:assert/strict'),fs=require('fs'),vm=require('vm'),path=require('path');
const base=path.resolve(__dirname,'../ui');
const source=fs.readFileSync(path.join(base,'intro.js'),'utf8');
const html=fs.readFileSync(path.join(base,'index.html'),'utf8');
function run(){
 const eventMap={};
 function node(){
   const listeners={},classes=new Set();
   return {style:{},attrs:{},classList:{add:v=>classes.add(v),remove:v=>classes.delete(v),contains:v=>classes.has(v)},
      addEventListener:(key,fn)=>{(listeners[key]||(listeners[key]=[])).push(fn)},
      dispatch:(key,event)=>{(listeners[key]||[]).forEach(fn=>fn(event||{}))},
      setAttribute:function(key,value){this.attrs[key]=value},
      focus:function(){this.focused=true}
   };
 }
 const intro=node(),video=node(),skip=node(),progress=node(),body=node();
 video.play=()=>Promise.resolve();video.pause=()=>{video.paused=true};
 video.duration=2.54;video.currentTime=0;
 const byId={launcherIntro:intro,launcherIntroVideo:video,launcherIntroSkip:skip,launcherIntroProgress:progress};
 const timers=new Map();let seq=0;
 const ctx={window:{},document:{body,getElementById:id=>byId[id]||null,
    addEventListener:(event,fn)=>{(eventMap[event]||(eventMap[event]=[])).push(fn)}},
    setTimeout:(fn,ms)=>{const id=++seq;timers.set(id,{fn,ms});return id},
    clearTimeout:id=>timers.delete(id),
    isFinite,isNaN,Math};
 vm.runInNewContext(source,ctx,{filename:'intro.js'});
 return {ctx,intro,video,skip,progress,body,timers,eventMap,
    advance:ms=>{const entries=[...timers].filter(([id,t])=>t.ms<=ms);entries.forEach(([id,t])=>{timers.delete(id);t.fn()})}};
}
assert.ok(html.includes('src="media/Loading.webm"') && html.includes('src="media/Loading.mp4"'));
assert.ok(fs.statSync(path.join(base,'media','Loading.mp4')).size>500000);
assert.ok(fs.statSync(path.join(base,'media','Loading.webm')).size>5000);
{
 const a=run();assert.equal(a.skip.focused,true);assert.equal(a.body.classList.contains('intro-visible'),true);
 a.video.dispatch('playing');assert.equal(a.ctx.window.__launcherIntroState.started,true);
 a.video.currentTime=1.27;a.video.dispatch('timeupdate');assert.equal(a.progress.style.width,'50.00%');
 a.video.dispatch('ended');assert.equal(a.ctx.window.__launcherIntroState.reason,'ended');
 assert.equal(a.body.classList.contains('intro-visible'),false);assert.equal(a.video.paused,true);
}
{
 const a=run();a.skip.dispatch('click');assert.equal(a.ctx.window.__launcherIntroState.reason,'skipped');
 a.video.dispatch('ended');assert.equal(a.ctx.window.__launcherIntroState.reason,'skipped');
}
{
 const a=run();a.video.dispatch('error');assert.equal(a.ctx.window.__launcherIntroState.reason,'video-error');
}
{
 const a=run();a.advance(3000);assert.equal(a.ctx.window.__launcherIntroState.reason,'startup-timeout');
}
{
 const a=run();a.video.dispatch('playing');a.advance(7000);assert.equal(a.ctx.window.__launcherIntroState.reason,'safety-timeout');
}
{
 const a=run();const evt={key:'Escape',preventDefault(){this.prevented=true}};
 a.eventMap.keydown[0](evt);assert.equal(a.ctx.window.__launcherIntroState.reason,'escape');assert.equal(evt.prevented,true);
}
console.log('intro-video.test.cjs PASS: source, play/end, 50% progress, skip, error, startup/safety timeouts, Escape');
