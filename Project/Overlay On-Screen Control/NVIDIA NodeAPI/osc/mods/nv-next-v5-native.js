/* ShadowPlay NEXT V5 native NVIDIA OSC adapter.
 * No DOM clones: all geometry is applied to the original md-menu + menu-button.
 * Disabled by default. V4 / old Group Layout A refuse ownership while enabled.
 */
(function (window, document) {
  'use strict';
  var M = window.NvShadowPlayLayoutV5, legacy = window.NvUnifiedLayoutV4;
  if (!M || !legacy) return;

  var FLAG = 'DulukaNextNativeLayoutV5Enabled';
  var KEY = 'DulukaNextNativeLayoutV5';
  var rootSelector = 'nv-main-menu:not([data-customize-preview]) .main-menu-container';
  var controls = {}, lastRoot = null, saved = null, session = null, raf = null;
  var settleRemaining = 0, lastGeometry = null, lastError = '';
  legacy.CONTROLS.forEach(function (meta) { controls[meta.key] = meta; });

  function read(name) {
    try { return window.localStorage.getItem(name); } catch (error) { return null; }
  }
  function enabled() { return read(FLAG) === '1'; }
  function storage() {
    if (saved) return M.clone(saved);
    try {
      var raw = read(KEY);
      if (raw) {
        var parsed = JSON.parse(raw);
        M.validate(parsed); saved = M.clone(parsed);
      }
    } catch (error) { lastError = 'Stored NEXT layout invalid; using NVIDIA defaults'; }
    if (!saved) saved = M.defaultLayout();
    return M.clone(saved);
  }
  function current() { return session && session.isEditing() ? session.get() : storage(); }
  function editing() {
    return !!(session && session.isEditing() &&
      document.documentElement.classList.contains('nv-live-layout-editing'));
  }
  function property(element, name, value) {
    if (element.style.getPropertyValue(name) !== value ||
        element.style.getPropertyPriority(name) !== 'important')
      element.style.setProperty(name, value, 'important');
  }
  function namedTile(root, key) {
    var info = controls[key];
    return info ? root.querySelector('md-menu.' + info.name) : null;
  }
  function variant(cell, s) {
    var loc = M.locate(s, cell.groupId);
    var block = loc.group.blocks[cell.block];
    if (cell.type === 'big') return 'big';
    if (cell.type === 'small') return 'small';
    if (block.layout === 'single') return 'row';
    if (block.layout === 'detailLeft') return cell.slot === 0 ? 'row' : 'small';
    return cell.slot === 1 ? 'row' : 'small';
  }
  function motionBlocked() {
    var motion = window.__dulukaOscMotion, s;
    if (!motion) return false;
    if (motion.getState) {
      s = motion.getState();
      if (s && s.target === 'closed') return true;
    }
    return !!(motion.phase && motion.phase !== 'idle');
  }
  function getNativeRoot() {
    return document.querySelector(rootSelector);
  }
  function geometry(root, preMotion) {
    if (!enabled() || !root || !root.querySelector) return null;
    if (!preMotion && motionBlocked()) return null;
    var rr = root.getBoundingClientRect();
    if (!rr || rr.width < 100 || rr.height < 10) return null;
    var bar = root.querySelector('md-menu-bar');
    if (!bar) return null;
    var s = current(), g = M.geometry(s, editing() ? 'edit' : 'normal',
      Math.max(320, (window.innerWidth || 1920) - 80));
    var nativeKeys = M.CONTROLS, actual = nativeKeys.filter(function (key) {
      return !!namedTile(root, key);
    });
    // Never partially reflow: all 12 original NVIDIA tiles must be linked.
    if (actual.length !== nativeKeys.length) return null;

    var width = g.width || 240;
    property(bar, 'width', width + 'px');
    property(bar, 'min-width', width + 'px');
    property(bar, 'height', '240px');
    var anchor = bar.parentElement || bar.offsetParent;
    if (!anchor || !anchor.getBoundingClientRect) return null;
    var stableX = anchor.getBoundingClientRect().left + (bar.offsetLeft || 0);
    if (!isFinite(stableX)) return null;
    var desiredX = ((window.innerWidth || 1920) - g.width * g.scale) / 2;
    property(bar, 'transform-origin', 'top left');
    property(bar, 'transform', 'translateX(' + (desiredX - stableX) +
      'px) scale(' + g.scale + ')');
    bar.classList.add('nv-next-v5-bar', 'nv-unified-bar');

    var layout = {};
    g.cells.forEach(function (cell) { layout[cell.control] = cell; });
    actual.forEach(function (key) {
      var el = namedTile(root, key), cell = layout[key];
      el.classList.add('nv-next-v5-tile', 'nv-unified-tile', 'nv-flex-tile');
      el.setAttribute('data-nv-next-control', key);
      property(el, 'position', 'absolute');
      if (!cell) {
        property(el, 'display', 'none');
        el.setAttribute('aria-hidden', 'true');
        return;
      }
      var v = variant(cell, s);
      property(el, 'display', 'block');
      property(el, 'left', cell.x + 'px');
      property(el, 'top', cell.y + 'px');
      property(el, 'right', 'auto');
      property(el, 'width', cell.width + 'px');
      property(el, 'height', cell.height + 'px');
      el.removeAttribute('aria-hidden');
      el.classList.toggle('nv-side-small-tile', v === 'small');
      el.classList.toggle('nv-side-full-tile', v !== 'small');
      el.classList.toggle('nv-unified-big', v === 'big');
      var info = el.querySelector('.nv-side-native-info');
      if (info) info.style.display = v === 'row' ? 'block' : 'none';
    });
    document.documentElement.classList.add('nv-next-v5-on');
    lastRoot = root;
    lastGeometry = g;
    return g;
  }
  function sync(preMotion) {
    return geometry(getNativeRoot(), !!preMotion);
  }
  function startSettle() {
    if (!enabled() || raf !== null || !window.requestAnimationFrame) return;
    settleRemaining = 30;
    function frame() {
      raf = null;
      if (!enabled()) return;
      var root = getNativeRoot();
      var result = geometry(root, false);
      if (result && --settleRemaining <= 0) return;
      if (settleRemaining <= 0) return;
      raf = window.requestAnimationFrame(frame);
    }
    raf = window.requestAnimationFrame(frame);
  }
  function enable(value) {
    // Switching the coordinate owner must happen on a clean CEF page load.
    if (value) window.localStorage.setItem(FLAG, '1');
    else window.localStorage.removeItem(FLAG);
    return { enabledOnNextReload: !!value, reloadRequired: true };
  }
  function set(next) {
    M.validate(next);
    if (editing()) throw Error('Use edit session while editing');
    window.localStorage.setItem(KEY, JSON.stringify(next));
    saved = M.clone(next);
    startSettle();
    return storage();
  }
  function beginEdit() {
    if (!enabled()) throw Error('Enable NEXT V5 before editing');
    if (session && session.isEditing()) return session.get();
    session = M.createEditSession(storage(), function (state) {
      // Persistence happens at Done, never during a draft.
      window.localStorage.setItem(KEY, JSON.stringify(state));
      saved = M.clone(state);
    });
    startSettle();
    return session.get();
  }
  function edit(fn) {
    if (!session || !session.isEditing()) throw Error('Not editing');
    var draft = session.apply(fn);
    startSettle();
    return draft;
  }
  function undo() {
    if (!session || !session.isEditing()) throw Error('Not editing');
    var next = session.undo(); startSettle(); return next;
  }
  function done() {
    if (!session || !session.isEditing()) return current();
    var savedState = session.done(); session = null; startSettle();
    return savedState;
  }
  function cancel() {
    if (!session || !session.isEditing()) return current();
    var original = session.cancel(); session = null; startSettle();
    return original;
  }
  function onRoute() {
    if (!enabled()) return;
    if (!getNativeRoot()) { lastRoot = null; lastGeometry = null; return; }
    startSettle();
  }
  var api = window.__nvNextLayoutV5 = {
    storageKey: KEY, flagKey: FLAG, enabled: enabled, enable: enable,
    get: current, getSaved: storage, set: set,
    beginEdit: beginEdit, edit: edit, undo: undo, done: done, cancel: cancel,
    sync: sync, prepareForMotion: function (root) { return geometry(root || getNativeRoot(), true); },
    geometry: function () { return lastGeometry; },
    lastError: function () { return lastError; },
    model: M
  };
  if (window.angular && window.angular.module) {
    window.angular.module('main').run(['$rootScope', function ($rootScope) {
      $rootScope.$on('$stateChangeSuccess', onRoute);
      $rootScope.$on('$viewContentLoaded', onRoute);
    }]);
  }
  if (window.setInterval) {
    window.setInterval(function () {
      if (!enabled()) return;
      var root = getNativeRoot();
      if (root && root !== lastRoot) startSettle();
      // Edit view toggle changes which empty Groups may be rendered.
      if (root && (!lastGeometry || (editing() && lastGeometry.boxes.length !== M.count(current()))))
        startSettle();
    }, 360);
  }
})(window, document);
