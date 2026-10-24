(function () {
  'use strict';

  // OSC Gallery playback bridge. Convert local file: URLs from NVIDIA's
  // existing Video Editor into read-only HTTP WebM segments.
  function filePath(value) {
    if (typeof value !== 'string') return null;
    if (/^[a-z]:[\\/]/i.test(value)) return value;
    if (!/^file:/i.test(value)) return null;
    try {
      var url = new URL(value);
      if (url.protocol !== 'file:' || (url.hostname && url.hostname !== 'localhost')) return null;
      var name = decodeURIComponent(url.pathname);
      return /^\/[a-z]:\//i.test(name) ? name.slice(1) : null;
    } catch (_ignore) { return null; }
  }

  function start(video, filename) {
    var old = video.__nvSegmentPlayback;
    if (old && !old.disposed && old.file === filename) return;
    if (old) old.dispose();

    var disposed = false, failed = false, fetching = false;
    var media = null, videoBuffer = null, audioBuffer = null;
    var objectUrl = null, controller = null, info = null;
    var generation = 0, inFlightStart = -1, appended = Object.create(null);
    var listeners = [];
    var state = {
      file: filename,
      disposed: false,
      dispose: function () {
        if (disposed) return;
        disposed = state.disposed = true;
        generation++;
        if (controller) controller.abort();
        controller = null;
        listeners.forEach(function (item) { item[0].removeEventListener(item[1], item[2]); });
        listeners = [];
        if (objectUrl) URL.revokeObjectURL(objectUrl);
        if (video.__nvSegmentPlayback === state) video.__nvSegmentPlayback = null;
      }
    };
    video.__nvSegmentPlayback = state;

    function listen(target, event, handler) {
      target.addEventListener(event, handler);
      listeners.push([target, event, handler]);
    }
    function fail(error) {
      if (disposed || failed || (error && error.name === 'AbortError')) return;
      failed = true;
      console.error('Gallery segment playback failed', error);
      video.dispatchEvent(new CustomEvent('nv-gallery-playback-error'));
    }
    function bufferedAt(buffer, time) {
      if (!buffer) return false;
      for (var i = 0; i < buffer.buffered.length; i++) {
        if (buffer.buffered.start(i) <= time + 0.08 &&
            buffer.buffered.end(i) > time + 0.08) return true;
      }
      return false;
    }
    function currentSegment() {
      return Math.floor(Math.max(0, video.currentTime || 0) / info.segmentDuration) *
        info.segmentDuration;
    }
    function cancelPending() {
      if (!fetching) return;
      generation++;
      fetching = false;
      inFlightStart = -1;
      if (controller) controller.abort();
      controller = null;
    }
    function onSeek() {
      if (disposed || failed || !info) return;
      var target = currentSegment();
      if (fetching && target !== inFlightStart) cancelPending();
      pump();
    }
    function pump() {
      if (disposed || failed || !info || !videoBuffer) return;
      if (!document.documentElement.contains(video)) { state.dispose(); return; }
      var time = Number(video.currentTime) || 0;
      if (!isFinite(time) || time < 0) time = 0;

      // A seek must win against old prefetch requests. Never append stale
      // media after a different timeline position is requested.
      if (fetching && currentSegment() !== inFlightStart &&
          !bufferedAt(videoBuffer, time)) cancelPending();
      if (fetching || videoBuffer.updating || (audioBuffer && audioBuffer.updating)) return;

      var cutoff = Math.floor((time - 32) / info.segmentDuration) * info.segmentDuration;
      if (cutoff > 0 && videoBuffer.buffered.length &&
          videoBuffer.buffered.start(0) < cutoff - 0.1) {
        Object.keys(appended).forEach(function (key) {
          if (Number(key) + info.segmentDuration <= cutoff) delete appended[key];
        });
        try {
          videoBuffer.remove(0, cutoff);
          if (audioBuffer && audioBuffer.buffered.length &&
              audioBuffer.buffered.start(0) < cutoff - 0.1) {
            audioBuffer.remove(0, cutoff);
          }
        } catch (error) { fail(error); }
        return;
      }

      var start = currentSegment();
      // Keep a small buffer ahead, but prioritize a fresh seek.
      if (bufferedAt(videoBuffer, time)) {
        var end = time;
        for (var i = 0; i < videoBuffer.buffered.length; i++) {
          if (videoBuffer.buffered.start(i) <= time + 0.08 &&
              videoBuffer.buffered.end(i) > time + 0.08) {
            end = videoBuffer.buffered.end(i);
            break;
          }
        }
        if (end - time > 12) return;
        start = Math.floor((end + 0.1) / info.segmentDuration) * info.segmentDuration;
      }
      if (start >= info.duration) return;
      if (appended[start]) {
        // May have been evicted by the media engine after a seek.
        if (bufferedAt(videoBuffer, start + 0.2)) return;
        delete appended[start];
      }

      fetching = true;
      inFlightStart = start;
      var token = ++generation;
      controller = window.AbortController ? new AbortController() : null;
      var options = controller ? { signal: controller.signal } : {};
      var base = new URL('/Gallery/v.1.0/Media', location.href);
      base.searchParams.set('file', filename);
      base.searchParams.set('segment', String(start));
      function get(track) {
        var endpoint = new URL(base.href);
        endpoint.searchParams.set('track', track);
        return fetch(endpoint.href, options).then(function (response) {
          if (!response.ok) throw new Error('Gallery ' + track + ' segment HTTP ' + response.status);
          return response.arrayBuffer();
        });
      }
      Promise.all([get('video'), info.audio ? get('audio') : Promise.resolve(null)]).then(function (parts) {
        if (disposed || token !== generation) return;
        if (videoBuffer.updating || (audioBuffer && audioBuffer.updating)) return;
        // No SourceBuffer.abort() here: aborting before every append resets
        // the parser and can break playback after seeking.
        videoBuffer.appendBuffer(parts[0]);
        if (audioBuffer && parts[1]) audioBuffer.appendBuffer(parts[1]);
        appended[start] = true;
      }).catch(function (error) {
        if (token === generation) fail(error);
      }).then(function () {
        if (disposed || token !== generation) return;
        fetching = false;
        controller = null;
        inFlightStart = -1;
        pump();
      });
    }

    var url = new URL('/Gallery/v.1.0/PlaybackInfo', location.href);
    url.searchParams.set('file', filename);
    fetch(url.href).then(function (response) {
      if (!response.ok) throw new Error('Gallery playback info HTTP ' + response.status);
      return response.json();
    }).then(function (data) {
      if (disposed) return;
      if (!data || !(data.duration > 0) || !(data.segmentDuration > 0) ||
          !isFinite(data.duration) || !isFinite(data.segmentDuration)) {
        throw new Error('Invalid Gallery playback metadata');
      }
      info = data;
      var videoMime = 'video/webm; codecs="vp8"';
      var audioMime = 'audio/webm; codecs="vorbis"';
      if (!window.MediaSource || !MediaSource.isTypeSupported(videoMime) ||
          (info.audio && !MediaSource.isTypeSupported(audioMime))) {
        throw new Error('CEF MediaSource WebM codec is unsupported');
      }
      media = new MediaSource();
      listen(media, 'sourceopen', function () {
        if (disposed || videoBuffer) return;
        try {
          videoBuffer = media.addSourceBuffer(videoMime);
          videoBuffer.mode = 'segments';
          videoBuffer.timestampOffset = -0.05;
          listen(videoBuffer, 'updateend', pump);
          listen(videoBuffer, 'error', fail);
          if (info.audio) {
            audioBuffer = media.addSourceBuffer(audioMime);
            audioBuffer.mode = 'segments';
            audioBuffer.timestampOffset = -0.05;
            listen(audioBuffer, 'updateend', pump);
            listen(audioBuffer, 'error', fail);
          }
          media.duration = info.duration;
          pump();
        } catch (error) { fail(error); }
      });
      listen(media, 'error', fail);
      listen(video, 'timeupdate', pump);
      listen(video, 'seeking', onSeek);
      listen(video, 'loadedmetadata', function () {
        if (!disposed && video.autoplay) {
          var playing = video.play();
          if (playing && playing.catch) playing.catch(function () {});
        }
      });
      objectUrl = URL.createObjectURL(media);
      video.src = objectUrl;
      video.load();
    }).catch(fail);
  }

  function rewrite(element) {
    if (!element || !element.getAttribute) return;
    var source = element.getAttribute('src');
    var video = element.tagName === 'VIDEO' ? element : element.parentElement;
    if (!video || video.tagName !== 'VIDEO') return;
    var filename = filePath(source);
    if (filename) start(video, filename);
    else if ((!source || source.indexOf('blob:') !== 0) &&
             video.__nvSegmentPlayback && !video.__nvSegmentPlayback.disposed) {
      video.__nvSegmentPlayback.dispose();
    }
  }
  function scan(node) {
    if (!node || node.nodeType !== 1) return;
    if (node.matches('video[src],source[src]')) rewrite(node);
    var list = node.querySelectorAll('video[src],source[src]');
    for (var i = 0; i < list.length; i++) rewrite(list[i]);
  }
  scan(document.documentElement);
  new MutationObserver(function (changes) {
    for (var i = 0; i < changes.length; i++) {
      var change = changes[i];
      if (change.type === 'attributes') rewrite(change.target);
      else {
        for (var j = 0; j < change.addedNodes.length; j++) scan(change.addedNodes[j]);
        for (var k = 0; k < change.removedNodes.length; k++) {
          var node = change.removedNodes[k];
          if (node.nodeType !== 1) continue;
          var videos = node.tagName === 'VIDEO' ? [node] : node.querySelectorAll('video');
          for (var v = 0; v < videos.length; v++) {
            if (videos[v].__nvSegmentPlayback) videos[v].__nvSegmentPlayback.dispose();
          }
        }
      }
    }
  }).observe(document.documentElement, {
    childList: true, attributes: true, attributeFilter: ['src'], subtree: true
  });
})();
