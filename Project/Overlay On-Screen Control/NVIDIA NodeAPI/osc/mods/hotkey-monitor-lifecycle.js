(function (root, factory) {
  'use strict';
  if (typeof module === 'object' && module.exports) module.exports = factory;
  else if (root.angular) {
    var injector = root.angular.element(root.document).injector();
    if (injector) factory(injector);
    else root.angular.module('main').run(['$injector', factory]);
  }
})(typeof window === 'undefined' ? this : window, function installMonitorLifecycle(injector, options) {
  'use strict';
  var shadow = injector.get('shadowPlayService');
  if (shadow.__nvMonitorLifecycle) return shadow.__nvMonitorLifecycle;
  var http = injector.get('$http'), q = injector.get('$q');
  var state = injector.get('$state'), scope = injector.get('$rootScope');
  var interval = injector.get('$interval');
  var win = injector.get('$window'), previousSession = null;
  var pageStartedAt = options && options.clock ? options.clock() : Date.now();
  var session = pageStartedAt.toString(36) + '_' + Math.random().toString(36).slice(2);
  try {
    previousSession = win.sessionStorage.getItem('DulukaOscHotkeyPageSession');
    win.sessionStorage.setItem('DulukaOscHotkeyPageSession', session);
  } catch (_) { }
  var sequence = 0, revision = 0, started = false, disposed = false, pending = q.when();
  function editing() { return state.current && state.current.name === 'main.preferences.keyboard-shortcuts'; }
  function post(enable) {
    return http.post('/ShadowPlay/v.1.0/Hotkey/Monitor', {
      enable: !!enable, pageSession: session, pageStartedAtUnixMs: pageStartedAt,
      pagePreviousSession: previousSession, sequence: ++sequence, pageStart: !started
    }, { timeout: 700 }).then(function () { started = true; });
  }
  function update(enable) {
    var version = ++revision;
    pending = pending.catch(function () {}).then(function () {
      if (disposed || version !== revision) return;
      return post(enable);
    });
    return pending;
  }
  // Use the existing NVIDIA service and route. No additional key listener or UI.
  shadow.setHotkeyMonitoringEnabled = function (enable) {
    return update(!!enable);
  };
  function reconcile() { return update(!editing()); }
  var offState = scope.$on('$stateChangeSuccess', function () { reconcile().catch(function () {}); });
  var timer = interval(function () { reconcile().catch(function () {}); }, 1000, 0, false);
  var offDestroy = scope.$on('$destroy', function () {
    disposed = true;
    interval.cancel(timer);
    offState();
    offDestroy();
    // A destroyed Angular root cannot still capture a shortcut. Its newer
    // sequence releases only this page's session, never a replacement editor.
    post(true).catch(function () {});
  });
  shadow.__nvMonitorLifecycle = { session: session };
  reconcile().catch(function () {});
  return shadow.__nvMonitorLifecycle;
});
