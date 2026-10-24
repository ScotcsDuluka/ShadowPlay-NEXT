(function () {
  'use strict';

  var settingsUrl = '/Duluka/v.1.0/GameFilter/Settings';
  function createFilterSettingsModel(http) {
    var saved = null, retryRequest = null, inFlight = null, version = 0, disposed = false;
    var model = {mode:'reshade',allowConnection:true,connectionManager:true,
      loaded:false,busy:false,status:'Loading settings…',error:'',retryLabel:''};
    function configuration(value) {
      return {mode:value.mode,allowConnection:value.allowConnection,connectionManager:value.connectionManager};
    }
    function valid(value) {
      return value && ['reshade','duluka','nvidia'].indexOf(value.mode) >= 0 &&
        typeof value.allowConnection === 'boolean' && typeof value.connectionManager === 'boolean' &&
        typeof value.bridgeEnabled === 'boolean' &&
        value.bridgeEnabled === (value.mode === 'reshade' && value.allowConnection && value.connectionManager);
    }
    function describe(value) {
      if (value.mode === 'duluka') return 'Duluka Freestyle is currently unavailable.';
      if (value.mode === 'nvidia') return 'NVIDIA Freestyle is currently unavailable.';
      if (!value.allowConnection) return 'Game filter is not allowed to connect to ReShade.';
      if (!value.connectionManager) return 'The ReShade connection manager is off.';
      return 'Game filter can connect to ReShade in supported games.';
    }
    function restore(value) {
      model.mode = value.mode;
      model.allowConnection = value.allowConnection;
      model.connectionManager = value.connectionManager;
    }
    function start(kind, payload) {
      if (disposed) return Promise.resolve(false);
      if (model.busy) return inFlight || Promise.resolve(false);
      var requestVersion = ++version;
      model.busy = true; model.error = ''; model.retryLabel = '';
      model.status = kind === 'load' ? 'Loading settings…' : 'Saving…';
      if (kind === 'load') model.loaded = false;
      function active() { return !disposed && version === requestVersion; }
      function failed() {
        if (!active()) return false;
        if (saved) restore(saved);
        model.status = saved ? describe(saved) : 'Settings are not loaded.';
        model.error = kind === 'load' ? 'Could not load settings. Please try again.' :
          'Could not save settings. Your last saved settings have been restored.';
        model.retryLabel = kind === 'load' ? 'Retry loading' : 'Retry saving';
        retryRequest = {kind:kind,payload:payload};
        return false;
      }
      var request;
      try {
        request = kind === 'load' ? http.get(settingsUrl, {cache:false,timeout:5000}) :
          http.post(settingsUrl, configuration(payload), {timeout:5000});
      } catch (error) {
        failed(); model.busy = false;
        return Promise.resolve(false);
      }
      inFlight = request.then(function (response) {
        if (!active()) return false;
        if (!valid(response && response.data)) return failed();
        saved = configuration(response.data);
        restore(saved); model.loaded = true; retryRequest = null;
        model.status = describe(saved);
        return true;
      }, failed).finally(function () {
        if (active()) { model.busy = false; inFlight = null; }
      });
      return inFlight;
    }
    model.load = function () { return start('load'); };
    model.save = function () {
      if (!model.loaded || disposed) return Promise.resolve(false);
      return start('save', configuration(model));
    };
    model.retry = function () {
      if (!retryRequest) return Promise.resolve(false);
      return start(retryRequest.kind, retryRequest.payload);
    };
    model.dispose = function () { disposed = true; version++; };
    return model;
  }

  var filterSettingsTemplate = [
    '<md-tabs md-dynamic-height md-no-ink aria-label="Photo mode and Game filter settings">',
    '<md-tab label="Game filter"><div class="nv-freestyle-content" aria-busy="{{filterSettings.busy}}">',
    '<div class="nv-freestyle-row">',
    '<label id="nv-freestyle-mode-label" class="settings-switch-label">Freestyle mode</label>',
    '<md-select class="osc-select nv-freestyle-select" ng-model="filterSettings.mode" ng-change="filterSettings.save()" ng-disabled="filterSettings.busy || !filterSettings.loaded" aria-labelledby="nv-freestyle-mode-label" hover-focus>',
    '<md-option value="reshade">ReShade Freestyle</md-option>',
    '<md-option value="duluka">Duluka Freestyle</md-option>',
    '<md-option value="nvidia">NVIDIA Freestyle</md-option>',
    '</md-select></div>',
    '<section ng-if="filterSettings.mode===\'reshade\'">',
    '<div class="nv-freestyle-row"><div class="nv-freestyle-label">',
    '<label id="nv-reshade-allow-label" class="settings-switch-label">Allow Game filter to connect to ReShade</label>',
    '<p id="nv-reshade-allow-help" class="settings-describe">Read and change ReShade effects from Game filter. Turning this off leaves your ReShade effects running.</p>',
    '</div><md-switch md-invert class="settings-switch" ng-model="filterSettings.allowConnection" ng-change="filterSettings.save()" ng-disabled="filterSettings.busy || !filterSettings.loaded" aria-labelledby="nv-reshade-allow-label" aria-describedby="nv-reshade-allow-help" hover-focus></md-switch></div>',
    '<div class="nv-freestyle-row"><div class="nv-freestyle-label">',
    '<label id="nv-reshade-manager-label" class="settings-switch-label">ReShade connection manager</label>',
    '<p id="nv-reshade-manager-help" class="settings-describe">Discover and communicate with ReShade in supported games. This must be on for Game filter to connect.</p>',
    '</div><md-switch md-invert class="settings-switch" ng-model="filterSettings.connectionManager" ng-change="filterSettings.save()" ng-disabled="filterSettings.busy || !filterSettings.loaded" aria-labelledby="nv-reshade-manager-label" aria-describedby="nv-reshade-manager-help" hover-focus></md-switch></div>',
    '<div class="nv-freestyle-row"><div class="nv-freestyle-label">',
    '<span class="settings-switch-label">Install ReShade</span>',
    '<p id="nv-reshade-install-help" class="settings-describe">Inspect a game, review its Graphics API and installation evidence, then use official ReShade Setup.</p>',
    '</div><button type="button" class="osc-button osc-button-gray" ng-click="filterSettings.openReShadeManager()" aria-describedby="nv-reshade-install-help" hover-focus>Open ReShade Manager</button></div>',
    '</section>',
    '<div class="nv-freestyle-feedback">',
    '<p class="settings-describe" role="status" aria-live="polite" aria-atomic="true">{{filterSettings.status}}</p>',
    '<div ng-if="filterSettings.error"><p class="settings-text-warning" role="alert">{{filterSettings.error}}</p>',
    '<button type="button" class="osc-button osc-button-gray" ng-click="filterSettings.retry()" ng-disabled="filterSettings.busy" hover-focus>{{filterSettings.retryLabel}}</button></div>',
    '</div></div></md-tab>',
    '<md-tab label="Photo mode"><div class="nv-freestyle-content">',
    '<div class="preferences-root-label"><p>Photo mode</p></div>',
    '<p class="settings-describe">This feature is currently unavailable.</p>',
    '</div></md-tab></md-tabs>'
  ].join('');

  if (typeof module !== 'undefined' && module.exports)
    module.exports = {createFilterSettingsModel:createFilterSettingsModel,filterSettingsTemplate:filterSettingsTemplate};
  if (typeof document === 'undefined') return;

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
        if (section) {
          if (section.__nvScope && !section.__nvScope.$$destroyed) section.__nvScope.$destroy();
          section.remove();
        }
      });
      return;
    }
    var host = document.querySelector('.preferences-root-panel');
    if (!host || host.querySelector('#nv-filter-settings')) return;
    var injector, parent, scope, settings, model;
    try {
      injector = window.angular && window.angular.element(document.documentElement).injector();
      parent = window.angular && window.angular.element(host).scope();
      if (!injector || !parent) return;
      scope = parent.$new();
      settings = document.createElement('section');
      settings.id = 'nv-filter-settings';
      settings.innerHTML = filterSettingsTemplate;
      settings.__nvScope = scope;
      model = scope.filterSettings = createFilterSettingsModel(injector.get('$http'));
      model.openReShadeManager = function () {
        if (window.NvReShadeManager && window.NvReShadeManager.open)
          return window.NvReShadeManager.open();
        model.error = 'ReShade Manager UI is not loaded. Check the OSC mods.';
        scope.$evalAsync();
        return false;
      };
      scope.$on('$destroy', model.dispose);
      injector.get('$compile')(settings)(scope);
      host.appendChild(settings);
      host.classList.add('nv-filter-settings-host');
      model.load();
      scope.$evalAsync();
    } catch (error) {
      if (model) model.dispose();
      if (scope && !scope.$$destroyed) scope.$destroy();
      if (settings) settings.remove();
      host.classList.remove('nv-filter-settings-host');
      console.error('[game-filter-settings] could not initialize', error);
    }
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
