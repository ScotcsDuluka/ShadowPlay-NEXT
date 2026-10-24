/* Magisk-inspired core module directory + scoped visual configuration.
 * CEF 73 compatible. Service lifecycle stays exclusively in app.js/native host.
 */
(function (window, document) {
  'use strict';
  var $ = function (id) { return document.getElementById(id); };
  var i18n = window.LauncherI18n;
  var tabs = ['Power','Core','Config','Addons'];
  var modules = [
    ['Container','NVIDIA Container'],['Overlay','On-Screen Control'],
    ['NodeApi','NVIDIA NodeAPI'],['Capture','NVIDIA Capture'],
    ['Graphics','NVIDIA Graphics'],['Helper','ShadowPlay Helper'],
    ['Adapter','OSC Adapter'],['Hook','Duluka Hook'],
    ['Ansel','Ansel Adapter'],['Ffmpeg','FFmpeg'],['Config','NVIDIA Config']
  ];
  var valid = {};
  for (var k = 0; k < modules.length; k++) valid[modules[k][0]] = modules[k][1];
  var lastState = window.__quickState || null;
  var busy = false;
  var busyTimer = null;
  var layoutCookie = 'spnext_modules_layout_v1';
  function tr(key) { return i18n ? i18n.t(key) : key; }
  function setMessage(id, key) {
    var node = $(id);
    if (!node) return;
    node.setAttribute('data-i18n', key);
    node.textContent = tr(key);
  }
  function cookieValue(key) {
    var parts = String(document.cookie || '').split(';');
    for (var i = 0; i < parts.length; i++) {
      var pair = parts[i].trim().split('=');
      if (pair[0] === key) return pair.slice(1).join('=');
    }
    return '';
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
  // The native appearance manager is reused INSIDE Config Module.
  // Never place visual profiles in Add-ons: there are no extra runtime modules.
  var appearance = $('appearanceSettings'), slot = $('quickAppearanceSlot');
  if (appearance && slot) slot.appendChild(appearance);

  function setLayout(value) {
    var chosen = value === 'grid' ? 'grid' : 'list';
    var grid = $('quickCoreGrid');
    if (grid) grid.classList.toggle('compact', chosen === 'grid');
    if ($('quickModuleLayout')) $('quickModuleLayout').value = chosen;
    document.cookie = layoutCookie + '=' + chosen +
      '; Max-Age=31536000; Path=/; SameSite=Lax';
  }
  var layout = $('quickModuleLayout');
  if (layout) {
    setLayout(cookieValue(layoutCookie));
    layout.addEventListener('change', function () { setLayout(this.value); });
  }
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
    for (var i = 0; i < modules.length; i++) {
      var id = modules[i][0], node = $('quickStatus' + id);
      if (!node) continue;
      var state = moduleState(mods && mods[id]);
      node.className = 'quick-module-state ' + state.cls;
      node.textContent = tr(state.key);
      node.setAttribute('data-i18n', state.key);
    }
    paintConfigStatus();
  }
  function paintConfigStatus() {
    var id = window.__quickModuleTarget || 'Container';
    var state = moduleState(lastState && lastState.modules && lastState.modules[id]);
    var node = $('quickConfigStatus');
    if (node) {
      node.className = 'quick-config-state ' + state.cls;
      setMessage('quickConfigStatus', state.key);
    }
  }
  function selectModule(id) {
    if (!Object.prototype.hasOwnProperty.call(valid,id)) return false;
    window.__quickModuleTarget = id;
    var target = $('quickConfigTarget');
    if (target) target.value = id;
    var name = $('quickConfigName');
    if (name) name.textContent = valid[id];
    paintConfigStatus();
    if (typeof window.__onModuleAppearanceTargetChanged === 'function')
      window.__onModuleAppearanceTargetChanged();
    return true;
  }
  function openConfig(id) {
    if (!selectModule(id)) return;
    show('Config');
  }
  window.ShadowPlayModuleUI = {
    openConfig: openConfig,
    selectedModule: function () { return window.__quickModuleTarget || 'Container'; },
    showCore: function () { show('Core'); }
  };
  var picker = $('quickConfigTarget');
  if (picker) picker.addEventListener('change', function () { selectModule(this.value); });
  var back = $('quickGoCore');
  if (back) back.addEventListener('click', function () { show('Core'); });
  var grid = $('quickCoreGrid');
  if (grid) grid.addEventListener('click', function (event) {
    var node = event && event.target;
    while (node && node !== grid) {
      if (node.getAttribute && node.getAttribute('data-config-module')) {
        openConfig(node.getAttribute('data-config-module'));
        return;
      }
      node = node.parentElement;
    }
  });
  selectModule('Container');

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
    var runtimeKey = runtime.running && runtime.ready ? 'quickServiceReady'
      : runtime.running ? 'quickServiceWaiting' : 'quickServiceStopped';
    setMessage('quickEngineStatus', runtimeKey);
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
    oldSwitch.click(); // Original app.js owns LAUNCHER_SET_OVERLAY and rollback.
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
