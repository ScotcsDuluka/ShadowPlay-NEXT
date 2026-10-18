(function () {
  'use strict';

  function rewriteVideoSource(element) {
    if (!element || !element.getAttribute) return;
    var source = element.getAttribute('src');
    if (typeof source !== 'string') return;

    var filename;
    if (/^file:/i.test(source)) {
      try {
        var fileUrl = new URL(source);
        filename = decodeURIComponent(fileUrl.pathname);
      } catch (err) {
        return;
      }
      if (/^\/[a-z]:\//i.test(filename)) filename = filename.slice(1);
    } else if (/^[a-z]:[\\/]/i.test(source)) {
      filename = source;
    } else {
      return;
    }

    var mediaUrl = new URL('/Gallery/v.1.0/Media', window.location.href);
    mediaUrl.searchParams.set('file', filename);
    element.setAttribute('src', mediaUrl.href);

    var video = element.tagName === 'VIDEO' ? element : element.parentElement;
    if (video && video.tagName === 'VIDEO') video.load();
  }

  function rewriteVideoSources(node) {
    if (!node || node.nodeType !== 1) return;
    if (node.matches('video[src], source[src]')) rewriteVideoSource(node);
    var sources = node.querySelectorAll('video[src], source[src]');
    for (var i = 0; i < sources.length; i++) rewriteVideoSource(sources[i]);
  }

  rewriteVideoSources(document.documentElement);
  new MutationObserver(function (mutations) {
    for (var i = 0; i < mutations.length; i++) {
      if (mutations[i].type === 'attributes') {
        rewriteVideoSource(mutations[i].target);
      } else {
        for (var j = 0; j < mutations[i].addedNodes.length; j++) {
          rewriteVideoSources(mutations[i].addedNodes[j]);
        }
      }
    }
  }).observe(document.documentElement, {
    childList: true,
    attributes: true,
    attributeFilter: ['src'],
    subtree: true
  });
})();
