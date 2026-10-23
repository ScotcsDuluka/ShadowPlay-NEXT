/* ShadowPlay NEXT V5 native NVIDIA OSC adapter.
 * No DOM clones: all geometry is applied to the original md-menu + menu-button.
 * Disabled by default. V4 / old Group Layout A refuse ownership while enabled.
 */
(function (window, document) {
  'use strict';
  var M = window.NvShadowPlayLayoutV5, legacy = window.NvUnifiedLayoutV4;
  if (!M || !legacy) return;

  // Some shipped OSC indexes load the adapter without its stylesheet. Keep
  // this import beside its owner so native buttons always fill their Slots.
  if (document.head && document.createElement && document.getElementById &&
      !document.getElementById('nv-next-v5-native-style')) {
    var nativeStyle=document.createElement('link');
    nativeStyle.id='nv-next-v5-native-style';nativeStyle.rel='stylesheet';
    nativeStyle.href='/mods/nv-next-v5-native.css?v=20261023-owner4';
    document.head.appendChild(nativeStyle);
  }

  var FLAG = 'DulukaNextNativeLayoutV5Enabled';
  var KEY = 'DulukaNextNativeLayoutV5';
  var rootSelector = 'nv-main-menu:not([data-customize-preview]) .main-menu-container';
  var controls = {}, lastRoot = null, saved = null, session = null, raf = null, previewState = null;
  var settleRemaining = 0, lastGeometry = null, lastError = '';
  // Editing ownership is transient until Done. The legacy editor keeps its
  // persisted flag and NVIDIA node identities untouched during Cancel.
  var temporaryOwner = false, snapshots = [], createdInfo = [];
  var barProperties = ['width','min-width','height','position','transform-origin','transform'];
  var tileProperties = ['position','display','left','top','right','width','height'];
  var tileClasses = ['nv-next-v5-tile','nv-unified-tile','nv-flex-tile',
    'nv-side-small-tile','nv-side-full-tile','nv-unified-big'];
  function remember(element, props, classes, attrs) {
    if (!element || snapshots.some(function (s) { return s.element === element; })) return;
    snapshots.push({
      element: element,
      props: props.map(function (key) {
        return { key:key, value:element.style.getPropertyValue(key),
          priority:element.style.getPropertyPriority(key) };
      }),
      classes: (classes || []).map(function (key) {
        return { key:key, had:element.classList.contains(key) };
      }),
      attrs: (attrs || []).map(function (key) {
        return { key:key, value:element.getAttribute ? element.getAttribute(key) : null };
      })
    });
  }
  function restoreNative() {
    if (raf !== null && window.cancelAnimationFrame) window.cancelAnimationFrame(raf);
    raf = null; settleRemaining = 0;
    snapshots.slice().reverse().forEach(function (s) {
      s.props.forEach(function (prop) {
        if (prop.value) s.element.style.setProperty(prop.key,prop.value,prop.priority || '');
        else if (s.element.style.removeProperty) s.element.style.removeProperty(prop.key);
        else s.element.style.setProperty(prop.key,'');
      });
      s.classes.forEach(function (item) {
        s.element.classList.toggle(item.key, item.had);
      });
      s.attrs.forEach(function (item) {
        if (item.value === null) s.element.removeAttribute(item.key);
        else s.element.setAttribute(item.key,item.value);
      });
    });
    snapshots = [];
    createdInfo.forEach(function(el){if(el.parentNode)el.parentNode.removeChild(el);});
    createdInfo = [];
    if (document.documentElement.classList.remove)
      document.documentElement.classList.remove('nv-next-v5-on');
    lastRoot = null; lastGeometry = null;
  }
  function editOwnership(value) {
    temporaryOwner = !!value;
    if (!enabled()) restoreNative();
    else startSettle();
    if (window.__nvUnifiedV4RequestNativeSync) window.__nvUnifiedV4RequestNativeSync();
    return enabled();
  }
  legacy.CONTROLS.forEach(function (meta) { controls[meta.key] = meta; });

  function read(name) {
    try { return window.localStorage.getItem(name); } catch (error) { return null; }
  }
  function enabled() { return temporaryOwner || read(FLAG) === '1'; }
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
  function renderState() { return previewState ? M.clone(previewState) : current(); }
  function preview(next) {
    if(next) { if(!editing())throw Error('Preview requires Edit Mode');M.validate(next); }
    previewState=next?M.clone(next):null;startSettle();
  }
  function editing() {
    return !!(session && session.isEditing());
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
  function nativeInfo(root, el, key) {
    var info=el.querySelector('.nv-side-native-info');
    if (['Performance','Mic','Camera'].indexOf(key)<0 || !document.createElement) return info;
    var button=el.querySelector('button.menu-button');
    if (!button) return info;
    if (!info) {
      info=document.createElement('span');info.className='nv-side-native-info';
      info.appendChild(document.createElement('strong'));
      info.appendChild(document.createElement('small'));
      button.appendChild(info);createdInfo.push(info);
    }
    var scope=window.angular&&window.angular.element(root).scope(), tile;
    while(scope&&!scope.mainMenu)scope=scope.$parent;
    tile=scope&&scope.mainMenu.tiles&&scope.mainMenu.tiles[key];
    info.firstChild.textContent={Performance:'Performance',Mic:'Microphone',Camera:'Camera'}[key];
    info.lastChild.textContent=tile&&(tile.shortcut||(tile.state&&tile.state[tile.currentState]||{}).status||'')||'';
    return info;
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
    var s = renderState(), g = M.geometry(s, editing() ? 'edit' : 'normal',
      Math.max(320, (window.innerWidth || 1920) - 80));
    var nativeKeys = M.CONTROLS, actual = nativeKeys.filter(function (key) {
      return !!namedTile(root, key);
    });
    // Never partially reflow: all 12 original NVIDIA tiles must be linked.
    if (actual.length !== nativeKeys.length) return null;

    var width = g.width || 240;
    remember(bar, barProperties, ['nv-next-v5-bar','nv-unified-bar']);
    // md-menu must resolve left/top against its actual parent md-menu-bar,
    // never against the shifted native main-menu-container.
    property(bar, 'position', 'relative');
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
      remember(el, tileProperties, tileClasses, ['data-nv-next-control','data-nv-next-variant','aria-hidden']);
      var infoBefore = nativeInfo(root, el, key);
      if (infoBefore) remember(infoBefore, ['display']);
      el.classList.add('nv-next-v5-tile', 'nv-unified-tile', 'nv-flex-tile');
      el.setAttribute('data-nv-next-control', key);
      property(el, 'position', 'absolute');
      if (!cell) {
        property(el, 'display', 'none');
        el.setAttribute('aria-hidden', 'true');
        return;
      }
      var v = variant(cell, s);
      el.setAttribute('data-nv-next-variant', v);
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
      if (--settleRemaining <= 0) return;
      if (settleRemaining <= 0) return;
      raf = window.requestAnimationFrame(frame);
    }
    raf = window.requestAnimationFrame(frame);
  }
  function enable(value) {
    if (value) window.localStorage.setItem(FLAG, '1');
    else window.localStorage.removeItem(FLAG);
    if (!enabled()) restoreNative();
    else startSettle();
    if (window.__nvUnifiedV4RequestNativeSync) window.__nvUnifiedV4RequestNativeSync();
    return { enabledOnNextReload: !!value, reloadRequired: false };
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
    previewState=null;
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
    previewState=null;
    var draft = session.apply(fn);
    startSettle();
    return draft;
  }
  function undo() {
    if (!session || !session.isEditing()) throw Error('Not editing');
    previewState=null;
    var next = session.undo(); startSettle(); return next;
  }
  function done() {
    if (!session || !session.isEditing()) return current();
    previewState=null;
    var savedState = session.done(); session = null; startSettle();
    return savedState;
  }
  function cancel() {
    if (!session || !session.isEditing()) return current();
    previewState=null;
    var original = session.cancel(); session = null; startSettle();
    return original;
  }
  function onRoute() {
    if (!enabled()) return;
    if (!getNativeRoot()) { restoreNative(); return; }
    startSettle();
  }
  var api = window.__nvNextLayoutV5 = {
    storageKey: KEY, flagKey: FLAG, enabled: enabled, enable: enable,
    editOwnership: editOwnership, restoreNative: restoreNative,
    get: current, getSaved: storage, set: set,
    renderState: renderState, preview: preview,
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
  if(window.addEventListener)window.addEventListener('resize',startSettle);
})(window, document);
