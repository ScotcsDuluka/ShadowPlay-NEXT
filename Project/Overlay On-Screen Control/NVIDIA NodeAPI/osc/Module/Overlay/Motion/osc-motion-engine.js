/* Shared reversible state engine for the preview and native OSC adapters. */
(function(root,factory){var api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else{root.createMotionController=api.createMotionController;root.evaluateMotionBezier=api.evaluateBezier;}})(typeof window!=='undefined'?window:this,function(){
  'use strict';
  function evaluateBezier(progress,points){
    if(progress<=0)return 0;if(progress>=1)return 1;
    function coordinate(t,a,b){var u=1-t;return 3*u*u*t*a+3*u*t*t*b+t*t*t;}
    var low=0,high=1,t=.5;
    for(var i=0;i<40;i++){t=(low+high)/2;var x=coordinate(t,points[0],points[2]);if(Math.abs(x-progress)<1e-11)break;if(x<progress)low=t;else high=t;}
    return coordinate(t,points[1],points[3]);
  }
  function createMotionController(options){
    var o=options||{},request=o.requestFrame,cancelFrame=o.cancelFrame,now=o.now;
    var startX=o.startX===undefined?-1722:o.startX,exitX=o.exitX===undefined?1722:o.exitX;
    function valid(value,min,max,fallback){return typeof value==='number'&&isFinite(value)?Math.max(min,Math.min(max,value)):fallback;}
    function validBezier(value){return Array.isArray(value)&&value.length===4&&value.every(function(n){return typeof n==='number'&&isFinite(n);})?[valid(value[0],0,1,0),valid(value[1],-2,3,0),valid(value[2],0,1,1),valid(value[3],-2,3,1)]:null;}
    var baseDuration=valid(o.duration,50,2000,520),baseCurve=valid(o.curve,.4,4,2);
    var timing={open:{duration:baseDuration,curve:baseCurve},closed:{duration:baseDuration,curve:baseCurve}};
    var initialBezier=validBezier(o.bezier);if(initialBezier){timing.open.bezier=initialBezier.slice();timing.closed.bezier=initialBezier.slice();}
    var pose={x:startX,scale:.24,opacity:0,barY:-88,barOpacity:0,bgProgress:0};
    var phase='idle',target='closed',requestedTarget='closed',progress=1,frame=null,version=0;
    function copy(value){var result=Object.assign({},value);if(value.bezier)result.bezier=value.bezier.slice();return result;}
    function ease(progress,config){return config.bezier?evaluateBezier(progress,config.bezier):Math.pow(progress,config.curve);}
    function endpoint(next){return next==='open'?{x:0,scale:1,opacity:1,barY:0,barOpacity:1,bgProgress:1}:{x:exitX,scale:.24,opacity:0,barY:-88,barOpacity:0,bgProgress:0};}
    function getTiming(){return {open:copy(timing.open),closed:copy(timing.closed)};}
    function state(){return {pose:copy(pose),phase:phase,target:target,requestedTarget:requestedTarget,progress:progress,easedProgress:ease(progress,timing[target==='open'?'open':'closed']),timing:getTiming()};}
    function paint(){if(o.render)o.render(copy(pose),state());}
    function invalidate(){version++;if(frame!==null)cancelFrame(frame);frame=null;}
    function equal(a,b){return Object.keys(a).every(function(key){return Math.abs(a[key]-b[key])<.000001;});}
    function go(next,force){
      var end=endpoint(next);
      if(!force&&next===target&&phase!=='idle')return;
      if(next===target&&phase==='idle'&&(equal(pose,end)||(next==='closed'&&pose.opacity===0&&pose.bgProgress===0)))return;
      var wasClosed=phase==='idle'&&target==='closed'&&pose.opacity===0&&pose.bgProgress===0;
      invalidate();target=next;progress=0;
      if(next==='open'&&wasClosed)pose={x:startX,scale:.24,opacity:0,barY:-88,barOpacity:0,bgProgress:0};
      var from=copy(pose),token=version,started=now();
      phase=next==='open'?'opening':'closing';
      var distance=Math.max(Math.abs(end.x-from.x)/Math.max(1,Math.abs(startX),Math.abs(exitX)),Math.abs(end.scale-from.scale)/.76,Math.abs(end.bgProgress-from.bgProgress),Math.abs(end.barOpacity-from.barOpacity));
      var selected=copy(timing[next==='open'?'open':'closed']);
      var time=Math.max(50,Math.min(Math.max(1100,selected.duration),selected.duration*Math.sqrt(distance)));
      function finish(){if(token!==version)return;pose=end;progress=1;phase='idle';frame=null;paint();if(o.loopInputs&&requestedTarget!==next){go(requestedTarget);return;}if(o.onFinish)o.onFinish(next);}
      if(o.reducedMotion&&o.reducedMotion()){finish();return;}
      paint();
      function tick(){
        if(token!==version)return;frame=null;
        var raw=Math.min(1,Math.max(0,(now()-started)/time));
        if(raw>=1){finish();return;}
        progress=raw;var eased=ease(raw,selected);
        Object.keys(pose).forEach(function(key){pose[key]=from[key]+(end[key]-from[key])*eased;});
        ['opacity','barOpacity','bgProgress'].forEach(function(key){pose[key]=Math.max(0,Math.min(1,pose[key]));});pose.scale=Math.max(.05,Math.min(2,pose.scale));
        paint();frame=request(tick);
      }
      frame=request(tick);
    }
    function command(next){requestedTarget=next;if(o.loopInputs&&phase!=='idle')return;go(next);}
    function setTiming(value){
      value=value&&typeof value==='object'?value:{};var changed=false;
      ['open','closed'].forEach(function(key){var input=value[key]&&typeof value[key]==='object'?value[key]:{},previous=timing[key];var next={duration:valid(input.duration,50,2000,previous.duration),curve:valid(input.curve,.4,4,previous.curve)};var bezier=validBezier(input.bezier);if(bezier)next.bezier=bezier;else if(previous.bezier&&!(typeof input.curve==='number'&&isFinite(input.curve)))next.bezier=previous.bezier.slice();if(next.duration!==previous.duration||next.curve!==previous.curve||JSON.stringify(next.bezier)!==JSON.stringify(previous.bezier))changed=true;timing[key]=next;});
      if(changed&&phase!=='idle')go(target,true);else paint();return getTiming();
    }
    var api={open:function(){command('open');},close:function(){command('closed');},toggle:function(){command(requestedTarget==='open'?'closed':'open');},cancel:function(){invalidate();phase='idle';requestedTarget=target;paint();},getState:state,getTiming:getTiming,setTiming:setTiming};
    paint();return api;
  }
  return {createMotionController:createMotionController,evaluateBezier:evaluateBezier};
});
