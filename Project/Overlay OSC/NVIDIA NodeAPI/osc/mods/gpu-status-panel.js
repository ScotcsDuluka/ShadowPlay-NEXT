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
    var enabled = panel.querySelector('#nv-gpu-alerts-enabled');
    enabled.checked = alertsEnabled();

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
      row.className = 'nv-gpu-game-row';
      var title = document.createElement('strong');
      title.textContent = gameName(event);
      var state = document.createElement('span');
      state.textContent = event.state;
      state.className = 'nv-gpu-state nv-gpu-state-' + String(event.state).toLowerCase();
      var adapters = (event.adapters || []).map(function (adapter) {
        return displayGpuName(adapter.name) || safeAdapterId(adapter) || 'Unknown adapter';
      }).join(' + ');
      var detail = document.createElement('span');
      detail.className = 'nv-gpu-detail';
      detail.textContent = adapters || event.reason || 'No runtime adapter evidence';
      var preference = latestPreference[exePath];
      var pref = document.createElement('span');
      pref.className = 'nv-gpu-preference';
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
      row.className = 'nv-gpu-adapter-row';
      var name = document.createElement('strong');
      name.textContent = displayGpuName(adapter.name) || 'GPU adapter';
      var meta = document.createElement('span');
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
    if (!onPreferencesModsPage()) {
      if (panel && panel.parentNode) panel.parentNode.removeChild(panel);
      panel = null;
      return;
    }
    var host = document.querySelector('.preferences-root-panel');
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
      '<div class="nv-gpu-heading">GPU settings and runtime status</div>' +
      '<label class="nv-gpu-toggle"><span>GPU notifications</span>' +
      '<input id="nv-gpu-alerts-enabled" type="checkbox"></label>' +
      '<p class="nv-gpu-description">Control GPU notification toasts and review runtime evidence and applied Windows preferences.</p>' +
      '<div class="nv-gpu-section-title">Game runtime status</div>' +
      '<div class="nv-gpu-games" aria-live="polite"></div>' +
      '<div class="nv-gpu-section-title">Observed adapters</div>' +
      '<div class="nv-gpu-adapters"></div>';
    var toggle = panel.querySelector('#nv-gpu-alerts-enabled');
    toggle.checked = alertsEnabled();
    toggle.addEventListener('change', function () {
      try {
        window.localStorage.setItem(alertsKey, toggle.checked ? 'true' : 'false');
        toggle.setAttribute('aria-checked', String(toggle.checked));
      } catch (error) {
        console.error('[gpu-settings] could not save notification preference', error);
        toggle.checked = alertsEnabled();
      }
    });
    host.appendChild(panel);
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
