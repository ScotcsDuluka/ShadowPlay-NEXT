#pragma once
#include <cstdint>
#include <mutex>
#include <string>

namespace osc {
// The HWND owns native visibility. Page callbacks acknowledge that intent;
// while convergence is pending, an old route cannot reverse a newer keypress.
class VisibilityConvergence {
public:
    struct Action { std::uint64_t generation; bool open; bool pending; bool recovering = false; std::uint64_t deadlineMs = 0; };
    static constexpr std::uint64_t ConvergenceTimeoutMs = 6000;
    Action Request(bool open, std::uint64_t nowMs = 0) {
        std::lock_guard<std::mutex> lock(mutex_);
        state_ = {state_.generation + 1, open, true};
        state_.deadlineMs = nowMs + ConvergenceTimeoutMs;
        return state_;
    }
    Action Snapshot() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return state_;
    }
    bool IsCurrent(const Action& action) const {
        const auto current = Snapshot();
        return current.pending && current.generation == action.generation &&
            current.open == action.open;
    }
    bool Observe(bool open, std::uint64_t generation) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (generation != state_.generation || (state_.pending && open != state_.open))
            return false;
        if (!state_.pending && open != state_.open)
            state_ = {state_.generation + 1, open, false};
        return true;
    }
    bool ObserveRoute(bool observedOpen, bool currentRouteOpen, std::uint64_t generation) {
        // An address callback can post before an ACK but arrive after it. Its
        // visibility must still agree with the browser's current actual URL.
        return observedOpen == currentRouteOpen && Observe(observedOpen, generation);
    }
    bool Acknowledge(bool open, std::uint64_t generation) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!state_.pending || generation != state_.generation || open != state_.open)
            return false;
        state_.pending = false;
        return true;
    }
    bool Fail(std::uint64_t generation) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!state_.pending || state_.recovering || generation != state_.generation) return false;
        // Withdraw pixels/input and hold CLOSED against late page callbacks.
        // A new real user request starts a fresh recovery attempt.
        state_ = {state_.generation + 1, false, true, true};
        return true;
    }
    bool Expired(std::uint64_t nowMs) const {
        const auto state = Snapshot();
        return state.pending && !state.recovering && nowMs >= state.deadlineMs;
    }
    void Stop() {
        std::lock_guard<std::mutex> lock(mutex_);
        state_ = {state_.generation + 1, false, false};
    }
private:
    mutable std::mutex mutex_;
    Action state_{0, false, false};
};

inline std::string VisibilityPageScript(const VisibilityConvergence::Action& action) {
    // Keep this actual generated script available to the Node VM regression.
    std::string js = R"OSCJS((function(g,desired){
var s=window.__nvOscNativeVisibility;
if(!s)s=window.__nvOscNativeVisibility={generation:-1,timer:0,running:false,openPending:false};
if(g<s.generation||(g===s.generation&&s.running))return;

if(s.timer)clearTimeout(s.timer);
s.generation=g;s.running=true;s.tries=0;s.resume=null;s.closeChecks=0;s.routeRepairIssued=false;s.finalCloseIssued=false;s.visualOpenIssued=false;s.reissues=0;
function current(){return s.generation===g&&s.running;}
function later(){if(!current())return;if(s.timer)clearTimeout(s.timer);s.timer=setTimeout(run,100);}
function run(){
 if(!current())return;
 if(s.timer){clearTimeout(s.timer);s.timer=0;}
 if(++s.tries>50){s.running=false;s.resume=null;console.error('OSC visibility convergence unavailable generation='+g);
  if(typeof window.cefQuery==='function')window.cefQuery({request:JSON.stringify({command:'QUERY_OSC_PAGE_ACTION_FAILED',generation:String(g),open:desired})});
  return;}
 try{
  var i=window.angular&&angular.element(document).injector();
  if(!i){later();return;}
  var d=i.get('oscDisplayService'),r=i.get('$rootScope'),st=i.get('$state');
  if(!s.listening){s.listening=true;
   r.$on('$viewContentLoaded',function(){
    if(s.openPending&&st.current&&st.current.name!=='base')s.openPending=false;
    if(s.resume)s.resume();
   });
   window.addEventListener('beforeunload',function(){if(s.timer)clearTimeout(s.timer);s.running=false;s.resume=null;});
  }
  s.resume=run;
  var open=!!(st.current&&st.current.name&&st.current.name!=='base');
  var motion=window.__dulukaOscMotion,m=motion&&motion.getState?motion.getState():null;
  function apply(fn){if(r.$$phase)fn();else r.$apply(fn);}
  if(open===desired&&!s.openPending){
   if(desired&&m&&(m.target!=='open'||m.requestedTarget!=='open')){
    if(!s.visualOpenIssued&&typeof motion.open==='function'){s.visualOpenIssued=true;apply(function(){motion.open();});}
    later();return;
   }
   if(desired&&m&&m.phase!=='idle'){later();return;}
   if(!desired&&!s.finalCloseIssued){s.finalCloseIssued=true;apply(function(){d.closeOSC();});later();return;}
   if(typeof window.cefQuery!=='function'){later();return;}
   s.running=false;s.resume=null;
   window.cefQuery({request:JSON.stringify({command:'QUERY_OSC_PAGE_ACTION_ACK',generation:String(g),open:desired}),
    onFailure:function(){console.error('OSC visibility acknowledgement failed generation='+g);}});
   return;
  }
  apply(function(){if(!current())return;
   if(desired){
    if(!s.openPending){s.openPending=true;s.reissues=0;d.openOSC();}
    // A lost silent open (swallowed intent, dropped promise) must never wedge
    // openPending for every later generation. Re-issue at 2s intervals, twice,
    // while the route still has not left base; each new generation restarts
    // this bounded counter.
    else if(s.tries>=20&&s.tries%20===0&&(s.reissues||0)<2){++s.reissues;console.warn('OSC open intent re-issued generation='+g+' try='+s.tries);d.openOSC();}
   }
   else {
    // Original closeOSC may no-op while its private opening flag is set,
    // including a controller exception before $viewContentLoaded. Let the
    // existing Motion adapter finish first, then repair only a stale route.
    if(!s.openPending||open)d.closeOSC();
    if(open&&!st.transition){
     ++s.closeChecks;m=motion&&motion.getState?motion.getState():null;
     var visualClosed=!m||(m.phase==='idle'&&m.target==='closed'&&m.requestedTarget==='closed');
     if(s.closeChecks>=5&&visualClosed&&!s.routeRepairIssued&&typeof st.go==='function'){
      s.routeRepairIssued=true;s.openPending=false;st.go('base');
     }
    }
   }
  });
  later();
 }catch(e){console.error('OSC page action failed: '+e.message);later();}
}
run();
})(@GENERATION@,@OPEN@);)OSCJS";
    const auto replace = [&js](const std::string& key, const std::string& value) {
        js.replace(js.find(key), key.size(), value);
    };
    replace("@GENERATION@", std::to_string(action.generation));
    replace("@OPEN@", action.open ? "true" : "false");
    return js;
}
}
