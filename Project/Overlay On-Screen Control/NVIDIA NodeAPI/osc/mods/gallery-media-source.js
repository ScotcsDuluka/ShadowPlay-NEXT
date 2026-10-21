(function () {
  'use strict';
  function filePath(source) {
    if (/^[a-z]:[\\/]/i.test(source)) return source;
    if (!/^file:/i.test(source)) return null;
    try {var name=decodeURIComponent(new URL(source).pathname);return /^\/[a-z]:\//i.test(name)?name.slice(1):name;} catch(ignore){return null;}
  }
  function start(video,filename) {
    if(video.__nvSegmentPlayback&&video.__nvSegmentPlayback.file===filename)return;
    if(video.__nvSegmentPlayback)video.__nvSegmentPlayback.dispose();
    var disposed=false,buffer=null,audioBuffer=null,media=null,objectUrl=null,fetching=false,abort=null,appended={},failed=false;
    var state={file:filename,dispose:function(){disposed=true;if(abort)abort.abort();video.removeEventListener('timeupdate',pump);video.removeEventListener('seeking',pump);if(objectUrl)URL.revokeObjectURL(objectUrl);}};
    video.__nvSegmentPlayback=state;
    var info=null;
    function fail(error){if(disposed||error&&error.name==='AbortError')return;failed=true;console.error('Gallery playback failed',error);video.dispatchEvent(new CustomEvent('nv-gallery-playback-error'));}
    function pump(){
      if(disposed||failed||fetching||!buffer||buffer.updating||audioBuffer&&audioBuffer.updating||!info)return;
      if(!document.documentElement.contains(video)){state.dispose();return;}
      var time=video.currentTime||0,start=Math.floor(time/info.segmentDuration)*info.segmentDuration;
      var cutoff=Math.floor((time-32)/info.segmentDuration)*info.segmentDuration;
      if(cutoff>0&&buffer.buffered.length&&buffer.buffered.start(0)<cutoff-.1){
        Object.keys(appended).forEach(function(key){if(Number(key)+info.segmentDuration<=cutoff)delete appended[key];});
        buffer.remove(0,cutoff);if(audioBuffer&&audioBuffer.buffered.length)audioBuffer.remove(0,cutoff);return;
      }
      for(var i=0;i<buffer.buffered.length;i++){
        if(buffer.buffered.start(i)<=time+.1&&buffer.buffered.end(i)>time+.1){
          if(buffer.buffered.end(i)-time>12)return;
          start=Math.floor((buffer.buffered.end(i)+.1)/info.segmentDuration)*info.segmentDuration;break;
        }
      }
      if(start>=info.duration||appended[start])return;
      fetching=true;abort=window.AbortController?new AbortController():null;
      var url=new URL('/Gallery/v.1.0/Media',location.href);url.searchParams.set('file',filename);url.searchParams.set('segment',String(start));
      function get(track){var endpoint=new URL(url.href);endpoint.searchParams.set('track',track);return fetch(endpoint.href,abort?{signal:abort.signal}:{}).then(function(response){if(!response.ok)throw Error('Video segment unavailable');return response.arrayBuffer();});}
      Promise.all([get('video'),info.audio?get('audio'):Promise.resolve(null)]).then(function(parts){
        if(disposed)return;
        buffer.abort();buffer.timestampOffset=-0.05;buffer.appendBuffer(parts[0]);
        if(audioBuffer){audioBuffer.abort();audioBuffer.timestampOffset=-0.05;audioBuffer.appendBuffer(parts[1]);}
        appended[start]=true;
      }).catch(fail).then(function(){fetching=false;abort=null;if(!disposed)pump();});
    }
    var url=new URL('/Gallery/v.1.0/PlaybackInfo',location.href);url.searchParams.set('file',filename);
    fetch(url.href).then(function(response){if(!response.ok)throw Error('Video information unavailable');return response.json();}).then(function(data){
      if(disposed)return;info=data;
      var mime='video/webm; codecs="vp8"';
      if(!window.MediaSource||!MediaSource.isTypeSupported(mime))throw Error('Video format is unsupported');
      media=new MediaSource();
      media.addEventListener('sourceopen',function(){
        if(disposed||buffer)return;
        buffer=media.addSourceBuffer(mime);buffer.mode='segments';
        if(info.audio){audioBuffer=media.addSourceBuffer('audio/webm; codecs="vorbis"');audioBuffer.mode='segments';audioBuffer.addEventListener('updateend',pump);audioBuffer.addEventListener('error',fail);}
        media.duration=info.duration;
        buffer.addEventListener('updateend',pump);buffer.addEventListener('error',fail);pump();
      });
      objectUrl=URL.createObjectURL(media);video.src=objectUrl;video.load();
      video.addEventListener('timeupdate',pump);video.addEventListener('seeking',pump);
      video.addEventListener('loadedmetadata',function resume(){video.removeEventListener('loadedmetadata',resume);if(!disposed){var play=video.play();if(play&&play.catch)play.catch(function(){});}});
    }).catch(fail);
  }
  function rewrite(element){
    if(!element||!element.getAttribute)return;
    var source=element.getAttribute('src');if(!source)return;
    var filename=filePath(source);
    var video=element.tagName==='VIDEO'?element:element.parentElement;
    if(!filename||!video||video.tagName!=='VIDEO')return;
    start(video,filename);
  }
  function scan(node){if(!node||node.nodeType!==1)return;if(node.matches('video[src],source[src]'))rewrite(node);var all=node.querySelectorAll('video[src],source[src]');for(var i=0;i<all.length;i++)rewrite(all[i]);}
  scan(document.documentElement);
  new MutationObserver(function(changes){for(var i=0;i<changes.length;i++){var change=changes[i];if(change.type==='attributes')rewrite(change.target);else{for(var j=0;j<change.addedNodes.length;j++)scan(change.addedNodes[j]);for(var k=0;k<change.removedNodes.length;k++){var node=change.removedNodes[k];if(node.nodeType!==1)continue;var videos=node.tagName==='VIDEO'?[node]:node.querySelectorAll('video');for(var v=0;v<videos.length;v++)if(videos[v].__nvSegmentPlayback)videos[v].__nvSegmentPlayback.dispose();}}}}).observe(document.documentElement,{childList:true,attributes:true,attributeFilter:['src'],subtree:true});
})();
