(function () {
  'use strict';

  var endpoint = '/Notifier/v.1.0/settings';
  var obsSettingsEndpoint = '/OBS/v.1.0/settings';
  var obsStatusEndpoint = '/OBS/v.1.0/status';
  var obsTestEndpoint = '/OBS/v.1.0/test';
  var panelId = 'nv-notifier-settings';
  var obsStatusTimer = null;
  var panelVersion = '15';
  var obsStatusRequest = null;
  var timer = null;
  var observer = null;
  var panelScope = null;

  function notificationPage() {
    return /#\/base\/preferences\/notifications(?:[/?]|$)/.test(window.location.hash);
  }

  function setStatus(node, text, error) {
    node.textContent = text;
    node.classList.toggle('highlights-item-disabled', Boolean(error));
  }

  function fetchWithTimeout(url, options, timeoutMs) {
    var limit = Number(timeoutMs) > 0 ? Number(timeoutMs) : 8000;
    var controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
    var requestOptions = {};
    Object.keys(options || {}).forEach(function (key) { requestOptions[key] = options[key]; });
    if (controller) requestOptions.signal = controller.signal;
    var timer;
    var request = fetch(url, requestOptions);
    var timeout = new Promise(function (_, reject) {
      timer = window.setTimeout(function () {
        if (controller) controller.abort();
        reject(new Error('Request timed out after ' + limit + ' ms: ' + url));
      }, limit);
    });
    return Promise.race([request, timeout]).then(function (response) {
      window.clearTimeout(timer);
      return response;
    }, function (error) {
      window.clearTimeout(timer);
      throw error;
    });
  }
  function addPanel(host) {
    var panel = document.createElement('div');
    panel.setAttribute('layout', 'column');
    panel.className = 'notif-row';
    panel.id = panelId;
    panel.setAttribute('data-nv-ui-version', panelVersion);
    panel.setAttribute('aria-label', 'Notifier settings');

    var heading = document.createElement('div');
    heading.className = 'notif-header';
    heading.textContent = 'Notifier layout';
    panel.appendChild(heading);

    var row = document.createElement('div');
    row.className = 'notif-item-row layout-align-space-between-center layout-row';
    row.setAttribute('layout', 'row');
    row.setAttribute('layout-align', 'space-between center');
    row.setAttribute('layout-wrap', '');

    var injector = window.angular && window.angular.element(document.documentElement).injector();
    var scope = injector ? injector.get('$rootScope').$new() : null;
    if (scope) {
      scope.obsListenEnabled = false;
      scope.obsSettingsLoaded = false;
      scope.obsSaving = false;
      scope.obsTesting = false;
      scope.syncingObsSetting = false;
      scope.obsHost = '127.0.0.1';
      scope.obsPort = 4455;
      scope.obsPassword = '';
    }

    var select = document.createElement('md-select');
    select.id = 'nv-notifier-slot-count';
    select.className = 'nv-notifier-slot-select';
    select.setAttribute('ng-model', 'notifierSlotCount');
    select.setAttribute('ng-change', 'saveNotifierSlots()');
    select.setAttribute('ng-disabled', '!notifierSlotsLoaded || notifierSlotsSaving');
    select.setAttribute('aria-label', 'Notification slots');
    select.setAttribute('flex', 'none');
    [
      ['1', '1 slot'],
      ['2', '2 slots'],
      ['3', '3 slots']
    ].forEach(function (item) {
      var option = document.createElement('md-option');
      option.setAttribute('ng-value', item[0]);
      option.textContent = item[1];
      select.appendChild(option);
    });
    var caption = document.createElement('span');
    caption.className = 'notif-text';
    caption.textContent = 'Number of toast slots';
    row.appendChild(caption);
    row.appendChild(select);

    var status = document.createElement('span');
    status.className = 'notif-text';
    status.setAttribute('role', 'status');
    status.setAttribute('aria-live', 'polite');
    panel.appendChild(row);
    panel.appendChild(status);
    var savedSlotCount = 2;
    if (scope) {
      scope.notifierSlotCount = savedSlotCount;
      scope.notifierSlotsLoaded = false;
      scope.notifierSlotsSaving = false;
      scope.saveNotifierSlots = function () {
        var slotCount = Number(scope.notifierSlotCount);
        if (!scope.notifierSlotsLoaded || scope.notifierSlotsSaving ||
            !Number.isInteger(slotCount) || slotCount < 1 || slotCount > 3 ||
            slotCount === savedSlotCount) return;

        scope.notifierSlotsSaving = true;
        setStatus(status, 'Saving…', false);
        fetchWithTimeout(endpoint, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ slotCount: slotCount })
        }).then(function (response) {
          if (!response.ok) {
            return response.json().then(function (error) {
              throw new Error(error.message || 'Unable to save notifier settings');
            });
          }
          return response.json();
        }).then(function (settings) {
          if (!Number.isInteger(settings.slotCount) ||
              settings.slotCount < 1 || settings.slotCount > 3) {
            throw new Error('Server returned an invalid slot count');
          }
          if (window.Notifier) window.Notifier.setSlotCount(settings.slotCount);
          savedSlotCount = settings.slotCount;
          setStatus(status, 'Saved', false);
          scope.$evalAsync(function () {
            scope.notifierSlotCount = savedSlotCount;
            scope.notifierSlotsSaving = false;
          });
        }).catch(function (error) {
          setStatus(status, error.message, true);
          scope.$evalAsync(function () {
            scope.notifierSlotCount = savedSlotCount;
            scope.notifierSlotsSaving = false;
          });
        });
      };
    }

    var obsPanel = document.createElement('div');
    obsPanel.setAttribute('layout', 'column');
    obsPanel.className = 'notif-row nv-obs-panel';
    obsPanel.id = 'nv-obs-settings';
    var obsHeading = document.createElement('div');
    obsHeading.className = 'notif-header';
    obsHeading.textContent = 'OBS notifications';
    obsPanel.appendChild(obsHeading);
    var obsDescription = document.createElement('span');
    obsDescription.className = 'notif-text';
    obsDescription.textContent = 'Show OSC notifications when OBS recording starts, pauses, resumes, or stops, and when replay buffer or streaming starts or stops. Control these in OBS.';
    obsPanel.appendChild(obsDescription);
    var obsDescriptionSpacer = document.createElement('div');
    obsDescriptionSpacer.className = 'highlights-separator-small';
    obsDescriptionSpacer.setAttribute('aria-hidden', 'true');
    obsPanel.appendChild(obsDescriptionSpacer);

    var obsSwitchRow = document.createElement('div');
    obsSwitchRow.className = 'notif-item-row layout-align-space-between-center layout-row nv-obs-switch-row';
    obsSwitchRow.setAttribute('layout', 'row');
    obsSwitchRow.setAttribute('layout-align', 'space-between center');
    var obsSwitchLabel = document.createElement('span');
    obsSwitchLabel.className = 'notif-text';
    obsSwitchLabel.textContent = 'Listen for OBS events';
    var obsEnabled = document.createElement('md-switch');
    obsEnabled.className = 'settings-switch';
    obsEnabled.setAttribute('md-invert', '');
    obsEnabled.setAttribute('ng-model', 'obsListenEnabled');
    obsEnabled.setAttribute('ng-disabled', '!obsSettingsLoaded || obsSaving');
    obsEnabled.setAttribute('aria-label', 'Listen for OBS events');
    obsSwitchRow.appendChild(obsSwitchLabel);
    obsSwitchRow.appendChild(obsEnabled);
    obsPanel.appendChild(obsSwitchRow);

    var obsControls = document.createElement('div');
    obsControls.id = 'nv-obs-controls';
    obsControls.setAttribute('layout', 'row');
    obsControls.setAttribute('layout-wrap', '');
    obsControls.setAttribute('layout-align', 'start start');

    function addObsInput(id, label, type, model, flex) {
      var field = document.createElement('md-input-container');
      field.className = 'nv-obs-field nv-obs-field-' + id;
      field.setAttribute('flex', flex);
      field.setAttribute('flex-xs', '100');
      var fieldLabel = document.createElement('label');
      fieldLabel.textContent = label;
      var input = document.createElement('input');
      input.id = id;
      input.className = 'md-input';
      input.type = type;
      input.setAttribute('ng-model', model);
      input.setAttribute('aria-label', label);
      input.setAttribute('ng-disabled', '!obsSettingsLoaded || obsSaving');
      field.appendChild(fieldLabel);
      field.appendChild(input);
      obsControls.appendChild(field);
      return input;
    }

    var hostInput = addObsInput('nv-obs-host', 'Host', 'text', 'obsHost', '38');
    hostInput.autocomplete = 'off';
    var portInput = addObsInput('nv-obs-port', 'Port', 'text', 'obsPort', '18');
    portInput.setAttribute('inputmode', 'numeric');
    portInput.setAttribute('pattern', '[0-9]*');
    portInput.autocomplete = 'off';
    var passwordInput = addObsInput('nv-obs-password', 'WebSocket password', 'password', 'obsPassword', '44');
    passwordInput.autocomplete = 'new-password';

    var obsStatus = document.createElement('span');
    obsStatus.className = 'notif-text';
    obsStatus.setAttribute('role', 'status');
    obsStatus.setAttribute('aria-live', 'polite');
    obsStatus.textContent = 'Loading OBS settings…';

    var obsActions = document.createElement('div');
    obsActions.className = 'nv-obs-actions';
    obsActions.setAttribute('layout', 'row');
    obsActions.setAttribute('layout-wrap', '');
    obsActions.setAttribute('layout-align', 'start center');
    function addObsButton(label, styleClass) {
      var button = document.createElement('md-button');
      button.type = 'button';
      button.className = 'osc-button nv-obs-button md-raised ' + styleClass;
      button.setAttribute('ng-disabled', '!obsSettingsLoaded || obsSaving || obsTesting');
      button.textContent = label;
      obsActions.appendChild(button);
      return button;
    }
    var saveObsButton = addObsButton('Save settings', 'osc-button-green');
    var testObsButton = addObsButton('Test connection', 'osc-button-gray');
    var clearPasswordButton = addObsButton('Clear saved password', 'osc-button-gray');
    var obsDetails = document.createElement('div');
    obsDetails.setAttribute('layout', 'column');
    obsDetails.appendChild(obsControls);
    obsDetails.appendChild(obsActions);
    obsDetails.appendChild(obsStatus);
    var obsSectionSpacer = document.createElement('div');
    obsSectionSpacer.className = 'highlights-separator-small';
    obsSectionSpacer.setAttribute('aria-hidden', 'true');
    obsDetails.appendChild(obsSectionSpacer);
    obsPanel.appendChild(obsDetails);
    host.appendChild(panel);
    host.appendChild(obsPanel);
    panelScope = scope;
    if (injector && panelScope) {
      injector.get('$compile')(panel)(panelScope);
      injector.get('$compile')(obsPanel)(panelScope);
    }

    setStatus(status, 'Loading…', false);
    fetchWithTimeout(endpoint).then(function (response) {
      if (!response.ok) throw new Error('Unable to load notifier settings (' + response.status + ')');
      return response.json();
    }).then(function (settings) {
      if (!Number.isInteger(settings.slotCount) || settings.slotCount < 1 || settings.slotCount > 3) {
        throw new Error('Server returned an invalid slot count');
      }
      savedSlotCount = settings.slotCount;
      scope.$evalAsync(function () {
        scope.notifierSlotCount = savedSlotCount;
        scope.notifierSlotsLoaded = true;
      });
      setStatus(status, '', false);
    }).catch(function (error) {
      setStatus(status, error.message, true);
    });

    var savedObsEnabled = false;
    function setObsStatus(text, failed) {
      obsStatus.textContent = text;
      obsStatus.classList.toggle('highlights-item-disabled', Boolean(failed));
    }

    function refreshObsStatus() {
      if (obsStatusRequest) return obsStatusRequest;
      var request = fetchWithTimeout(obsStatusEndpoint).then(function (response) {
        if (!response.ok) throw new Error('Unable to read NvContainer OBS status (' + response.status + ')');
        return response.json();
      }).then(function (runtime) {
        if (runtime.enabled && runtime.connected) {
          setObsStatus('Connected · ' + runtime.host + ':' + runtime.port, false);
        } else if (runtime.enabled) {
          setObsStatus('Disconnected · ' + (runtime.error || 'waiting for OBS'), true);
        } else { setObsStatus('Disabled', false); }
        return runtime;
      });
      obsStatusRequest = request.then(function (runtime) {
        obsStatusRequest = null;
        return runtime;
      }, function (error) {
        obsStatusRequest = null;
        setObsStatus(error.message, true);
        throw error;
      });
      return obsStatusRequest;
    }
    function saveObsSettings(clearPassword) {
      var port = Number(panelScope.obsPort);
      if (!/^\d+$/.test(String(panelScope.obsPort)) ||
          !Number.isInteger(port) || port < 1 || port > 65535) {
        setObsStatus('Port must be an integer from 1 to 65535', true);
        return Promise.reject(new Error('Invalid OBS port'));
      }
      panelScope.obsSaving = true;
      panelScope.$applyAsync();
      var settings = { enabled: panelScope.obsListenEnabled, host: panelScope.obsHost.trim(), port: port };
      if (panelScope.obsPassword) settings.password = panelScope.obsPassword;
      if (clearPassword) settings.clearPassword = true;
      setObsStatus('Saving settings…', false);
      return fetchWithTimeout(obsSettingsEndpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(settings)
      }).then(function (response) {
        if (!response.ok) return response.json().then(function (error) {
          throw new Error(error.message || 'Unable to save OBS settings');
        });
        return response.json();
      }).then(function (saved) {
        panelScope.syncingObsSetting = true;
        panelScope.obsListenEnabled = saved.enabled === true;
        panelScope.obsHost = saved.host;
        panelScope.obsPort = Number(saved.port);
        panelScope.obsPassword = '';
        savedObsEnabled = panelScope.obsListenEnabled;
        panelScope.$applyAsync(function () { panelScope.syncingObsSetting = false; });
        return refreshObsStatus().then(function (runtime) {
          return { runtime: runtime };
        }, function (statusError) {
          return { statusError: statusError.message };
        });
      }).catch(function (error) {
        panelScope.syncingObsSetting = true;
        panelScope.obsListenEnabled = savedObsEnabled;
        panelScope.$applyAsync(function () { panelScope.syncingObsSetting = false; });
        setObsStatus('Unable to save settings: ' + error.message, true);
        throw error;
      }).then(function (outcome) {
        if (!outcome) return;
        if (outcome.statusError) {
          setObsStatus('Settings saved, but OBS status refresh failed: ' + outcome.statusError, true);
          return;
        }
        var runtime = outcome.runtime;
        if (runtime) setObsStatus(runtime.enabled && runtime.connected ?
          'Connected · ' + runtime.host + ':' + runtime.port :
          runtime.enabled ? 'Disconnected · ' + (runtime.error || 'waiting for OBS') : 'Saved · Disabled',
        runtime.enabled && !runtime.connected);
      }).finally(function () {
        panelScope.obsSaving = false;
        panelScope.$applyAsync();
      });
    }
    if (panelScope) panelScope.$watch('obsListenEnabled', function (value, previous) {
      if (!panelScope.obsSettingsLoaded || panelScope.syncingObsSetting ||
          value === previous || value === savedObsEnabled) return;
      saveObsSettings(false).catch(function () {});
    });
    fetchWithTimeout(obsSettingsEndpoint).then(function (response) {
      if (!response.ok) throw new Error('Unable to load OBS settings (' + response.status + ')');
      return response.json();
    }).then(function (settings) {
      if (!panelScope) return;
      panelScope.syncingObsSetting = true;
      panelScope.obsListenEnabled = settings.enabled === true;
      savedObsEnabled = panelScope.obsListenEnabled;
      panelScope.obsHost = settings.host || '127.0.0.1';
      panelScope.obsPort = Number(settings.port || 4455);
      panelScope.obsPassword = '';
      panelScope.$applyAsync(function () {
        panelScope.obsSettingsLoaded = true;
        panelScope.syncingObsSetting = false;
      });
      return refreshObsStatus();
    }).catch(function (error) {
      setObsStatus(error.message, true);
    });
    if (obsStatusTimer) clearInterval(obsStatusTimer);
    obsStatusTimer = setInterval(function () {
      if (!panel.isConnected) {
        clearInterval(obsStatusTimer);
        obsStatusTimer = null;
        return;
      }
      refreshObsStatus().catch(function () {});
    }, 3000);
    saveObsButton.addEventListener('click', function () {
      saveObsSettings(false).catch(function () {});
    });
    clearPasswordButton.addEventListener('click', function () {
      saveObsSettings(true).catch(function () {});
    });
    testObsButton.addEventListener('click', function () {
      panelScope.obsTesting = true;
      panelScope.$applyAsync();
      setObsStatus('Testing OBS connection…', false);
      fetchWithTimeout(obsTestEndpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: '{}'
      }).then(function (response) {
        return response.json().then(function (result) {
          if (!response.ok || result.ok !== true) throw new Error(result.message || result.error || 'OBS connection test failed');
          setObsStatus('Connected · OBS ' + (result.version || 'version available'), false);
        });
      }).catch(function (error) {
        setObsStatus(error.message, true);
      }).then(function () {
        panelScope.obsTesting = false;
        panelScope.$applyAsync();
        refreshObsStatus().catch(function () {});
      });
    });
  }

  function initialize() {
    if (!notificationPage()) return;
    if (!window.angular || !window.angular.element(document.documentElement).injector()) return;
    var host = document.querySelector('nv-preferences-notifications .notif-item-container');
    if (!host) return;
    var existing = document.getElementById(panelId);
    if (existing && existing.getAttribute('data-nv-ui-version') === panelVersion &&
        document.getElementById('nv-obs-settings')) return;
    if (obsStatusTimer) clearInterval(obsStatusTimer);
    obsStatusTimer = null;
    if (panelScope) panelScope.$destroy();
    panelScope = null;
    if (existing && existing.parentNode) existing.parentNode.removeChild(existing);
    var oldObsPanel = document.getElementById('nv-obs-settings');
    if (oldObsPanel && oldObsPanel.parentNode) oldObsPanel.parentNode.removeChild(oldObsPanel);
    addPanel(host);
  }

  function watchPage() {
    initialize();
    if (observer) return;
    observer = new MutationObserver(initialize);
    observer.observe(document.body, { childList: true, subtree: true });
  }

  function stopWatching() {
    if (observer) observer.disconnect();
    observer = null;
    if (obsStatusTimer) clearInterval(obsStatusTimer);
    obsStatusTimer = null;
    if (panelScope) panelScope.$destroy();
    panelScope = null;
    var panel = document.getElementById(panelId);
    if (panel && panel.parentNode) panel.parentNode.removeChild(panel);
    var obsPanel = document.getElementById('nv-obs-settings');
    if (obsPanel && obsPanel.parentNode) obsPanel.parentNode.removeChild(obsPanel);
  }

  window.addEventListener('hashchange', function () {
    if (notificationPage()) watchPage();
    else stopWatching();
  });

  if (notificationPage()) watchPage();
  else {
    timer = window.setInterval(function () {
      if (notificationPage()) {
        window.clearInterval(timer);
        timer = null;
        watchPage();
      }
    }, 250);
  }
})();
