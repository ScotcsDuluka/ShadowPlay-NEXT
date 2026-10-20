(function () {
  'use strict';

  var panelId = 'nv-gpu-settings';
  var alertsKey = 'nv-gpu-notifications-enabled';
  var runtimeEvent = '/Duluka/v1/GpuRuntimeState';
  var preferenceEvent = '/Duluka/v1/GpuPreferenceApplied';
  var inventoryEvent = '/Duluka/v1/GpuAdapterInventory';
  var latestRuntime = Object.create(null);
  var latestPreference = Object.create(null);
  var latestInventory = null;
  var registered = false;
  var panel = null;

    function showFilterSettings() {
    if (!onPreferencesModsPage()) {
      Array.prototype.forEach.call(document.querySelectorAll('.nv-filter-settings-host'), function (previous) {
        previous.classList.remove('nv-filter-settings-host');
        var section = previous.querySelector('#nv-filter-settings');
        if (section) { if (section.__nvScope) section.__nvScope.$destroy(); section.remove(); }
      });
      return;
    }
    var host = document.querySelector('.preferences-root-panel');
    if (!host || host.querySelector('#nv-filter-settings')) return;
    host.classList.add('nv-filter-settings-host');
    var settings = document.createElement('section');
    settings.id = 'nv-filter-settings';
    settings.innerHTML = '<md-tabs md-dynamic-height md-no-ink><md-tab label="Game filter"><md-content class="md-padding">' +
      '<div class="preferences-root-label"><p>Game filter</p></div>' +
      '<div class="layout-row"><span class="settings-switch-label">Freestyle mode</span><md-select ng-model="filterSettings.mode" ng-change="filterSettings.save()" ng-disabled="filterSettings.busy || !filterSettings.loaded" aria-label="Freestyle mode"><md-option value="reshade">ReShade Freestyle</md-option><md-option value="duluka">Duluka Freestyle</md-option><md-option value="nvidia">NVIDIA Freestyle</md-option></md-select></div>' +
      '<section ng-if="filterSettings.mode===\'reshade\'">' +
      '<div class="layout-row"><span class="settings-switch-label">Allow Game filter to connect to ReShade</span><md-switch md-invert class="settings-switch" ng-model="filterSettings.allowConnection" ng-change="filterSettings.save()" ng-disabled="filterSettings.busy || !filterSettings.loaded" aria-label="Allow Game filter to connect to ReShade"></md-switch></div>' +
      '<div class="settings-describe">Allow Game filter to read and change ReShade effects. Disconnecting does not turn off ReShade effects.</div>' +
      '<div class="layout-row"><span class="settings-switch-label">ReShade connection manager</span><md-switch md-invert class="settings-switch" ng-model="filterSettings.connectionManager" ng-change="filterSettings.save()" ng-disabled="filterSettings.busy || !filterSettings.loaded" aria-label="ReShade connection manager"></md-switch></div>' +
      '<div class="settings-describe">Run the connection manager that discovers and communicates with ReShade.</div>' +
      '<section class="preferences-item-container"><div class="broadcast-lvl1-label">Install ReShade</div><div class="settings-describe">The installer is not available yet.</div><button type="button" class="osc-button osc-button-gray" disabled>Install ReShade</button></section></section>' +
      '<div ng-if="filterSettings.mode!==\'reshade\'" class="settings-describe">This Freestyle mode is currently unavailable.</div>' +
      '<div class="settings-describe" role="status">{{filterSettings.status}}</div>' +
      '</md-content></md-tab><md-tab label="Photo mode"><md-content class="md-padding"><div class="preferences-root-label"><p>Photo mode</p></div><div class="settings-describe">This feature is currently unavailable.</div></md-content></md-tab></md-tabs>';
    host.appendChild(settings);
    var injector = window.angular.element(document.documentElement).injector();
    var scope = window.angular.element(host).scope().$new();
    settings.__nvScope = scope;
    var model = scope.filterSettings = {mode:'reshade',allowConnection:true,connectionManager:true,busy:false,loaded:false,status:'Loading settings…'};
    var saved;
    function apply(value) {
      model.mode = value.mode;
      model.allowConnection = value.allowConnection;
      model.connectionManager = value.connectionManager;
      saved = {mode:value.mode,allowConnection:value.allowConnection,connectionManager:value.connectionManager};
      model.status = value.bridgeEnabled ? 'ReShade connection enabled.' : 'ReShade connection inactive.';
    }
    model.save = function () {
      if (!model.loaded || model.busy) return;
      model.busy = true; model.status = 'Saving…';
      injector.get('$http').post('/Duluka/v.1.0/GameFilter/Settings', {mode:model.mode,allowConnection:model.allowConnection,connectionManager:model.connectionManager}).then(function (response) {
        if (settings.isConnected) apply(response.data);
      }, function () {
        if (saved) { model.mode=saved.mode; model.allowConnection=saved.allowConnection; model.connectionManager=saved.connectionManager; }
        model.status = 'Could not save settings. Please try again.';
      }).finally(function () { model.busy = false; });
    };
    injector.get('$compile')(settings)(scope);
    injector.get('$http').get('/Duluka/v.1.0/GameFilter/Settings', {cache:false,timeout:2500}).then(function (response) {
      if (!settings.isConnected) return;
      apply(response.data); model.loaded = true;
    }, function () { model.status = 'Could not load settings. Reopen this page to try again.'; });
  }
function onPreferencesModsPage() {
    return /#\/base\/preferences\/mods(?:[/?]|$)/.test(window.location.hash) ||
      document.body.getAttribute('data-nv-route') === 'preferences-mods';
  }

  function alertsEnabled() {
    try { return window.localStorage.getItem(alertsKey) !== 'false'; }
    catch (error) { return true; }
  }

  function gameName(event) {
    if (event.displayName) return event.displayName;
    var parts = String(event.exePath || '').split(/[\\/]/);
    return parts[parts.length - 1] || 'Unknown game';
  }

  function safeAdapterId(adapter) {
    return adapter && typeof adapter.adapterId === 'string' ? adapter.adapterId : '';
  }

  function displayGpuName(name) {
    return String(name || '').replace(/\s*\[GPU\s*\d+\]\s*/gi, ' ').trim();
  }

  function render() {
    if (!panel || !panel.isConnected) return;
    if (panel.__nvScope) panel.__nvScope.gpuAlerts.enabled = alertsEnabled();

    var games = panel.querySelector('.nv-gpu-games');
    games.textContent = '';
    var paths = Object.keys(latestRuntime).sort(function (a, b) {
      return gameName(latestRuntime[a]).localeCompare(gameName(latestRuntime[b]));
    });
    if (!paths.length) {
      var empty = document.createElement('div');
      empty.className = 'nv-gpu-empty';
      empty.textContent = 'No runtime GPU results have arrived yet. Results appear after a configured game is checked.';
      games.appendChild(empty);
    }
    paths.forEach(function (exePath) {
      var event = latestRuntime[exePath];
      var row = document.createElement('div');
      row.className = 'nv-gpu-game-row preferences-item-container layout-column';
      var title = document.createElement('strong');
      title.textContent = gameName(event);
      var state = document.createElement('span');
      state.textContent = event.state;
      state.className = 'nv-gpu-state nv-gpu-state-' + String(event.state).toLowerCase();
      var adapters = (event.adapters || []).map(function (adapter) {
        return displayGpuName(adapter.name) || safeAdapterId(adapter) || 'Unknown adapter';
      }).join(' + ');
      var detail = document.createElement('span');
      detail.className = 'nv-gpu-detail settings-describe';
      detail.textContent = adapters || event.reason || 'No runtime adapter evidence';
      var preference = latestPreference[exePath];
      var pref = document.createElement('span');
      pref.className = 'nv-gpu-preference settings-describe';
      pref.textContent = preference
        ? 'Windows preference: ' + (preference.adapterName || preference.adapterId) +
          ' (not proof of runtime use)'
        : 'Windows preference: not reported';
      row.appendChild(title);
      row.appendChild(state);
      row.appendChild(detail);
      row.appendChild(pref);
      games.appendChild(row);
    });

    var adapterList = panel.querySelector('.nv-gpu-adapters');
    adapterList.textContent = '';
    var inventory = latestInventory && Date.now() - Date.parse(latestInventory.capturedUtc) < 120000
      ? latestInventory : null;
    var adapterRows = inventory ? inventory.adapters : [];
    if (!adapterRows.length) {
      var noAdapters = document.createElement('div');
      noAdapters.className = 'nv-gpu-empty';
      noAdapters.textContent = latestInventory
        ? 'The last adapter inventory is stale; waiting for a fresh snapshot.'
        : 'No adapter inventory snapshot has arrived yet.';
      adapterList.appendChild(noAdapters);
    }
    adapterRows.forEach(function (adapter) {
      var row = document.createElement('div');
      row.className = 'nv-gpu-adapter-row preferences-item-container layout-column';
      var name = document.createElement('strong');
      name.textContent = displayGpuName(adapter.name) || 'GPU adapter';
      var meta = document.createElement('span');
      meta.className = 'settings-describe';
      var memory = adapter.memoryUsedMiB != null && adapter.memoryTotalMiB != null
        ? Math.round(adapter.memoryUsedMiB) + ' / ' + Math.round(adapter.memoryTotalMiB) + ' MiB'
        : 'unavailable';
      meta.textContent = 'ID: ' + (adapter.adapterId || 'unavailable') +
        ' · PCI: ' + (adapter.pciBusId || 'unavailable') +
        ' · LUID: ' + ((adapter.dxgiLuids || []).join(', ') || 'unavailable') +
        ' · Utilization: ' + (adapter.utilizationPercent != null
          ? Math.round(adapter.utilizationPercent) + '%' : 'unavailable') +
        ' · VRAM: ' + memory;
      row.appendChild(name);
      row.appendChild(meta);
      adapterList.appendChild(row);
    });
  }

  function showPanel() {
    showFilterSettings();
    if (!/#\/base\/preferences\/game-profiles(?:[?#]|$)/.test(window.location.hash)) {
      if (panel && panel.parentNode) { if (panel.__nvScope) panel.__nvScope.$destroy(); panel.parentNode.removeChild(panel); }
      panel = null;
      return;
    }
    var host = document.querySelector('#nv-gpu-profiles-page .nv-gpu-profiles-panel');
    if (!host) return;
    var existing = document.getElementById(panelId);
    if (existing) {
      panel = existing;
      render();
      return;
    }

    panel = document.createElement('section');
    panel.id = panelId;
    panel.setAttribute('aria-label', 'GPU settings and runtime status');
    panel.innerHTML =
      '<div class="broadcast-lvl1-label">GPU settings and runtime status</div>' +
      '<div class="layout-row"><span class="settings-switch-label">GPU notifications</span>' +
      '<md-switch md-invert class="settings-switch" ng-model="gpuAlerts.enabled" ng-change="gpuAlerts.save()" aria-label="GPU notifications"></md-switch></div>' +
      '<div class="settings-describe">Control GPU notification toasts and review runtime evidence and applied Windows preferences.</div>' +
      '<details class="nv-gpu-diagnostics"><summary>Diagnostics</summary>' +
      '<div class="nv-gpu-section-title">Game runtime status</div>' +
      '<div class="nv-gpu-games" aria-live="polite"></div>' +
      '<div class="nv-gpu-section-title">Observed adapters</div>' +
      '<div class="nv-gpu-adapters"></div></details>';
    var injector = window.angular.element(document.documentElement).injector();
    var scope = window.angular.element(host).scope().$new();
    panel.__nvScope = scope;
    scope.gpuAlerts = {enabled:alertsEnabled(),save:function () {
      try {
        window.localStorage.setItem(alertsKey, scope.gpuAlerts.enabled ? 'true' : 'false');
      } catch (error) {
        console.error('[gpu-settings] could not save notification preference', error);
        scope.gpuAlerts.enabled = alertsEnabled();
      }
    }};
    host.appendChild(panel);
    injector.get('$compile')(panel)(scope);
    render();
  }

  function acceptRuntime(event) {
    if (!event || typeof event.exePath !== 'string' || typeof event.state !== 'string') return;
    latestRuntime[event.exePath.toLowerCase()] = event;
    render();
  }

  function acceptPreference(event) {
    if (!event || typeof event.exePath !== 'string' || typeof event.adapterId !== 'string') return;
    latestPreference[event.exePath.toLowerCase()] = event;
    render();
  }

  function acceptInventory(event) {
    if (!event || !Array.isArray(event.adapters) || typeof event.capturedUtc !== 'string') return;
    latestInventory = event;
    render();
  }

  function connect() {
    try {
      var injector = window.angular &&
        window.angular.element(document.documentElement).injector();
      if (!injector) return false;
      var socketService = injector.get('socketService');
      var eventAggregator = injector.get('eventAggregator');
      if (!registered) {
        eventAggregator.on('nv-gpu-panel-runtime', acceptRuntime);
        eventAggregator.on('nv-gpu-panel-preference', acceptPreference);
        eventAggregator.on('nv-gpu-panel-inventory', acceptInventory);
        eventAggregator.on('connect', function () {
          socketService.register(runtimeEvent, 'nv-gpu-panel-runtime');
          socketService.register(preferenceEvent, 'nv-gpu-panel-preference');
          socketService.register(inventoryEvent, 'nv-gpu-panel-inventory');
        });
        registered = true;
      }
      socketService.register(runtimeEvent, 'nv-gpu-panel-runtime');
      socketService.register(preferenceEvent, 'nv-gpu-panel-preference');
      socketService.register(inventoryEvent, 'nv-gpu-panel-inventory');
      return true;
    } catch (error) {
      console.error('[gpu-settings] could not subscribe to GPU events', error);
      return false;
    }
  }

  var attempts = 0;
  var readyTimer = setInterval(function () {
    if (connect() || ++attempts >= 40) clearInterval(readyTimer);
  }, 250);
  var observer = new MutationObserver(function (mutations) {
    var outsidePanelChanged = mutations.some(function (mutation) {
      return !panel || !panel.contains(mutation.target);
    });
    if (outsidePanelChanged) showPanel();
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('hashchange', showPanel);
  setInterval(render, 15000);
  showPanel();
})();
