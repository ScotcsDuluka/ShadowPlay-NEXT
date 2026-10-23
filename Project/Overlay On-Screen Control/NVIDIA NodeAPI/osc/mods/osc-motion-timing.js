/* Approved OSC curves; user animation preferences never modify layout data. */
(function(window){
  'use strict';
  var timing=window.__dulukaOscMotionTiming={
    closed:{bezier:[.6681970923837989,.04272720336914062,.00500000000000001,1],duration:240},
    open:{bezier:[0,.9524242401123044,.27999999999999997,1],duration:500}
  };
  var key='DulukaOscMotionSettings',listeners=[],error='';
  var defaults={enabled:true,mode:'osc',backgroundFade:true,topAnimation:true,
    mainMenuAnimation:true,openDuration:500,closeDuration:240};
  function clone(value){return JSON.parse(JSON.stringify(value));}
  function normalize(input,base){
    input=input&&typeof input==='object'&&!Array.isArray(input)?input:{};
    var next=clone(base);
    ['enabled','backgroundFade','topAnimation','mainMenuAnimation'].forEach(function(name){
      if(typeof input[name]==='boolean')next[name]=input[name];
    });
    if(input.mode==='osc'||input.mode==='default')next.mode=input.mode;
    ['openDuration','closeDuration'].forEach(function(name){
      if(typeof input[name]==='number'&&isFinite(input[name]))next[name]=Math.round(Math.max(50,Math.min(2000,input[name])));
    });
    return next;
  }
  var current=clone(defaults);
  try{current=normalize(JSON.parse(window.localStorage&&window.localStorage.getItem(key)||'null'),defaults);}
  catch(ignore){error='Could not load animation settings; using defaults.';}
  function updateTiming(){timing.open.duration=current.openDuration;timing.closed.duration=current.closeDuration;}
  updateTiming();
  function set(input){
    var next=normalize(input,current);
    try{
      if(!window.localStorage)throw Error('Storage unavailable');
      window.localStorage.setItem(key,JSON.stringify(next));
    }catch(ignore){error='Could not save animation settings.';return false;}
    var changed=JSON.stringify(next)!==JSON.stringify(current);
    current=next;updateTiming();error='';
    if(changed)listeners.slice().forEach(function(listener){try{listener(clone(current));}catch(ignore){}});
    return true;
  }
  var api=window.__dulukaOscMotionSettings={
    storageKey:key,get:function(){return clone(current);},set:set,
    reset:function(){return set(defaults);},
    subscribe:function(listener){
      if(typeof listener!=='function')return function(){};
      listeners.push(listener);return function(){listeners=listeners.filter(function(item){return item!==listener;});};
    }
  };
  Object.defineProperty(api,'error',{get:function(){return error;}});
})(window);
