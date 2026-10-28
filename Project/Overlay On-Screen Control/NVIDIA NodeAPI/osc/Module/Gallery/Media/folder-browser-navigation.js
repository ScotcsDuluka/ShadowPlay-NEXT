(function () {
  'use strict';

  var cardSelector = '.recordings-folder-browser-content-item';
  var doubleClickDelayMs = 500;
  var lastClick = null;
  var galleryLayout = null;

  function findController(scope) {
    for (var depth = 0; scope && depth < 12; depth++, scope = scope.$parent) {
      if (scope.controller &&
          typeof scope.controller.doubleClickFolder === 'function') {
        return { controller: scope.controller, index: scope.$index, scope: scope };
      }
    }
    return null;
  }

  function syncGalleryLayout() {
    var panel = document.querySelector('#gallery.gallery-central-div');
    var layout = panel && panel.parentNode;
    if (layout === galleryLayout) return;
    if (galleryLayout) galleryLayout.classList.remove('nv-gallery-fullscreen-layout');
    galleryLayout = layout;
    if (galleryLayout) galleryLayout.classList.add('nv-gallery-fullscreen-layout');
  }

  syncGalleryLayout();
  new MutationObserver(syncGalleryLayout).observe(document.documentElement, {
    childList: true,
    subtree: true
  });

  document.addEventListener('click', function (event) {
    var card = event.target;
    while (card && card !== document &&
        !(card.matches && card.matches(cardSelector))) {
      card = card.parentNode;
    }
    if (!card || card === document || !window.angular) return;

    var selected = findController(window.angular.element(card).scope());
    if (!selected || typeof selected.index !== 'number') return;

    var now = Date.now();
    var isDoubleClick = lastClick &&
      lastClick.card === card &&
      lastClick.controller === selected.controller &&
      lastClick.index === selected.index &&
      now - lastClick.time <= doubleClickDelayMs;
    lastClick = isDoubleClick ? null : {
      card: card,
      controller: selected.controller,
      index: selected.index,
      time: now
    };
    if (!isDoubleClick) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    selected.scope.$evalAsync(function () {
      if (selected.controller.dirFolderData[selected.index]) {
        selected.controller.doubleClickFolder(selected.index);
      }
    });
  }, true);
})();
