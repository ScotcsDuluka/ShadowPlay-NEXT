/* Read-only Core Modules. Names/descriptions are bundled fallback; status is local.
 * A server module catalog requires a real read-only API; never fake one.
 * Only the suite-level ShadowPlay power control remains.
 * CEF 73 compatible (ES5).
 */
(function (window, document) {
  'use strict';
  var $ = function (id) { return document.getElementById(id); };
  var tabs = ['Power','Core','Addons'];
  var moduleKeys = ['Container','Overlay','NodeApi','Capture','Graphics','Helper',
                    'Adapter','Hook','Ansel','Ffmpeg','Config'];
  var lastState = window.__quickState || null;
  var busy = false, busyTimer = null;
  function tr(key) {
    return window.LauncherI18n ? window.LauncherI18n.t(key) : key;
  }
  function setMessage(id, key) {
    var node = $(id);
    if (!node) return;
    node.setAttribute('data-i18n', key);
    node.textContent = tr(key);
  }
  function show(tab) {
    for (var i = 0; i < tabs.length; i++) {
      var name = tabs[i], active = name === tab;
      var page = $('quickPage' + name), button = $('quickNav' + name);
      if (page) page.classList.toggle('active', active);
      if (button) {
        button.classList.toggle('active', active);
        if (active) button.setAttribute('aria-current', 'page');
        else button.removeAttribute('aria-current');
      }
    }
    if ($('quickMain')) $('quickMain').scrollTop = 0;
  }
  for (var t = 0; t < tabs.length; t++) {
    (function (name) {
      var button = $('quickNav' + name);
      if (button) button.addEventListener('click', function () { show(name); });
    })(tabs[t]);
  }
  // The desktop Launcher provides local process/file telemetry; do not
  // represent it as authoritative server-owned catalog/version metadata.
  function moduleState(info) {
    if (!info || typeof info.installed !== 'boolean')
      return {key:'moduleWaiting',cls:'unknown'};
    if (!info.installed) return {key:'moduleMissing',cls:'missing'};
    if (info.processTracking === false) return {key:'moduleInstalled',cls:'installed'};
    if (info.processTracking === true && typeof info.running === 'boolean')
      return info.running ? {key:'running',cls:'running'}
                          : {key:'stopped',cls:'stopped'};
    return {key:'moduleInstalled',cls:'installed'};
  }
  function paintModules(mods) {
    for (var i = 0; i < moduleKeys.length; i++) {
      var id = moduleKeys[i], node = $('quickStatus' + id);
      if (!node) continue;
      var status = moduleState(mods && mods[id]);
      node.className = 'quick-module-state ' + status.cls;
      node.textContent = tr(status.key);
      node.setAttribute('data-i18n', status.key);
    }
  }
  function paint(state) {
    if (!state) return;
    lastState = state;
    paintModules(state.modules);
    var enabled = state.overlayEnabled === true;
    var known = typeof state.overlayEnabled === 'boolean';
    var toggle = $('quickPowerToggle');
    var card = document.querySelector && document.querySelector('.quick-power-card');
    if (card) card.classList.toggle('on', known && enabled);
    setMessage('quickPowerState', known ? (enabled ? 'quickIsOn' : 'quickIsOff') : 'connecting');
    setMessage('quickPowerNote', known ? 'quickPowerExplain' : 'quickRuntimeUnknown');
    if (toggle) {
      toggle.disabled = !known || busy || typeof window.cefQuery !== 'function';
      toggle.setAttribute('aria-pressed', enabled ? 'true' : 'false');
      toggle.setAttribute('data-i18n', enabled ? 'quickPowerOff' : 'quickPowerOn');
      toggle.textContent = tr(enabled ? 'quickPowerOff' : 'quickPowerOn');
    }
    var runtime = state.overlayApi || {};
    var key = runtime.running && runtime.ready ? 'quickServiceReady'
      : runtime.running ? 'quickServiceWaiting' : 'quickServiceStopped';
    setMessage('quickEngineStatus', key);
  }
  window.onQuickLauncherState = paint;
  if (lastState) paint(lastState);
  else paintModules(null);
  var power = $('quickPowerToggle');
  if (power) power.addEventListener('click', function () {
    if (busy || !lastState || typeof lastState.overlayEnabled !== 'boolean') return;
    var oldSwitch = $('tglOverlay');
    if (!oldSwitch || typeof oldSwitch.click !== 'function') return;
    busy = true;
    power.disabled = true;
    oldSwitch.click(); // app.js owns the suite-level LAUNCHER_SET_OVERLAY.
    if (busyTimer) clearTimeout(busyTimer);
    busyTimer = setTimeout(function () {
      busy = false;
      paint(window.__quickState || lastState);
    }, 1400);
  });
  var openOverlay = $('quickOpenOverlay');
  if (openOverlay) openOverlay.addEventListener('click', function () {
    var oldButton = $('btnOpenOverlay');
    if (oldButton && typeof oldButton.click === 'function') oldButton.click();
  });
  var oldLanguageHook = window.onLauncherLanguageChanged;
  window.onLauncherLanguageChanged = function () {
    if (typeof oldLanguageHook === 'function') oldLanguageHook();
    if (lastState) paint(lastState);
  };
})(window, document);
