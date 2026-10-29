/* ShadowPlay NEXT Launcher MP4 intro — isolated, fail-open, CEF 73 / ES5.
 * Plays the GitHub Loading.mp4, then reveals the existing quick launcher.
 * Never invokes native actions and never stores personal account data.
 */
(function (window, document) {
  'use strict';
  var intro = document.getElementById('launcherIntro');
  var video = document.getElementById('launcherIntroVideo');
  var skip = document.getElementById('launcherIntroSkip');
  var progress = document.getElementById('launcherIntroProgress');
  if (!intro || !video || !skip) return;

  var done = false;
  var played = false;
  var safetyTimer = null;
  var startTimer = null;
  var removeTimer = null;
  var state = {started:false, finished:false, reason:'waiting'};
  window.__launcherIntroState = state; // test-only telemetry; no server calls.

  function finish(reason) {
    if (done) return;
    done = true;
    state.finished = true;
    state.reason = reason || 'closed';
    clearTimeout(safetyTimer);
    clearTimeout(startTimer);
    try { video.pause(); } catch (ignore) {}
    intro.classList.add('intro-leaving');
    document.body.classList.remove('intro-visible');
    skip.setAttribute('tabindex', '-1');
    intro.setAttribute('aria-hidden', 'true');
    removeTimer = setTimeout(function () {
      intro.style.display = 'none';
    }, 400);
  }
  function progressUpdate() {
    if (done || !progress) return;
    var duration = video.duration, current = video.currentTime;
    if (isFinite(duration) && duration > 0 && isFinite(current)) {
      var percent = Math.max(0, Math.min(100, (current / duration) * 100));
      progress.style.width = percent.toFixed(2) + '%';
    }
  }
  skip.addEventListener('click', function () { finish('skipped'); });
  document.addEventListener('keydown', function (event) {
    if (done) return;
    if (event.key === 'Escape') { event.preventDefault(); finish('escape'); }
    if (event.key === 'Tab') { event.preventDefault(); skip.focus(); }
  }, true);
  video.addEventListener('playing', function () {
    if (done) return;
    played = true;
    state.started = true;
    state.reason = 'playing';
    clearTimeout(startTimer);
  });
  video.addEventListener('timeupdate', progressUpdate);
  video.addEventListener('ended', function () { finish('ended'); });
  video.addEventListener('error', function () { finish('video-error'); });
  video.addEventListener('stalled', function () {
    // Timer handles an unrecoverable stall. Transient stalls are OK.
    state.reason = played ? 'buffering' : 'loading';
  });

  // Keep the main launcher usable even with unsupported MP4 codecs,
  // missing files, failed autoplay or missing media events.
  safetyTimer = setTimeout(function () { finish('safety-timeout'); }, 7000);
  startTimer = setTimeout(function () {
    if (!played) finish('startup-timeout');
  }, 3000);
  document.body.classList.add('intro-visible');
  skip.focus();
  try {
    video.muted = true; // autoplay is intentionally silent.
    video.volume = 0;
    video.defaultMuted = true;
    var result = video.play();
    if (result && typeof result.catch === 'function') {
      result.catch(function () { finish('autoplay-blocked'); });
    }
  } catch (error) {
    finish('play-exception');
  }
})(window, document);
