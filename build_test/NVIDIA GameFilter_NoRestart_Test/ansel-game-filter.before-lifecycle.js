(function (angular) {
  'use strict';

  if (!angular) return;

  function install($injector) {
    function supportedGameRequired() {
      $injector.get('oscNotificationService').show($injector.get('NOTIFIER_SELECTIONS').WARNING_SUPPORTED_GAME_REQUIRED);
      return false;
    }
    if (!window.__dulukaGameEditorRouteGuardInstalled) {
      window.__dulukaGameEditorRouteGuardInstalled = true;
      $injector.get('$rootScope').$on('$stateChangeStart', function (event, toState, params) {
        if (!toState || (toState.name !== 'mods' && toState.name !== 'nvcamera')) return;
        if ($injector.get('nvCameraService').__anselConnected) return;
        event.preventDefault();
        $injector.get('$http').get('/Duluka/v.1.0/GameFilter/State', {cache:false,timeout:1800}).then(function (response) {
          if (response.data && response.data.connected) {
            $injector.get('nvCameraService').__anselConnected = true;
            $injector.get('$state').go(toState.name, params);
          } else supportedGameRequired();
        }, supportedGameRequired);
      });
    }
    if (!window.__dulukaEditorCloseInstalledV2) {
      window.__dulukaEditorCloseInstalledV2 = true;
      document.addEventListener('click', function (event) {
        var button = event.target.closest && event.target.closest('.mods-close-button, .mods-done-button');
        if (!button) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        // Closing is local UI work; native cleanup must not hold the window.
        $injector.get('oscDisplayService').closeOSC();
        $injector.get('$state').go('base');
        $injector.get('cefService').query({command:'QUERY_WIN_CLOSE_OSC'});
      }, true);
    }
    if ($injector.has && $injector.has('cefService') && !window.__dulukaPerformanceBridgeInstalled) {
      window.__dulukaPerformanceBridgeInstalled = true;
      var cef = $injector.get('cefService');
      var nativeQuery = cef.nativeQuery;
      cef.nativeQuery = function (plugin, command, args) {
        if (plugin !== 'OverClocking' && plugin !== 'OverClockingNvc') return nativeQuery.apply(this, arguments);
        return $injector.get('$http').post('/Duluka/v.1.0/Performance/Query', {command:command,args:args || {}}).then(function (response) {
          return JSON.stringify(response.data);
        });
      };
      var performance = $injector.get('octoolService');
      var performanceReady;
      function preparePerformance() {
        if (!performanceReady) performanceReady = performance.getGPUInfoAndFillData().then(function () {
          performance.createPerfMetricsSets();
        }).catch(function (error) { performanceReady = null; throw error; });
        return performanceReady;
      }
      performance.launchOCToolMenu = function () {
        return preparePerformance().then(function () {
          return $injector.get('$state').go('octoolmenu', {lastState:'main.main-menu'});
        });
      };
      preparePerformance().then(function () {
        var state = $injector.get('$state');
        if (state.current.name === 'octoolmenu') state.go('octoolmenu', state.params, {reload:true});
      }, function (error) { console.error('[performance] GPU initialization failed', error); });
    }
    // Keep the local editor discoverable before the first hotkey launch. The
    // legacy main menu caches NVIDIA cloud availability independently.
    if (!window.__dulukaAnselMenuInstalledV7) {
      window.__dulukaAnselMenuInstalledV7 = true;
      var allowNavigation = function () { return true; };
      var hideDiscovery = function () { return false; };
      var editorClicks = {};
      ['Ansel', 'Mods'].forEach(function (key) {
        editorClicks[key] = function () {
          if (key === 'Mods') return $injector.get('nvCameraService').launchUIForMods('uiTrigger');
          return $injector.get('$http').get('/Duluka/v.1.0/GameFilter/State', {cache:false,timeout:1800}).then(function (response) {
            if (response.data && response.data.connected)
              return $injector.get('$state').go(window.__dulukaOscInterface.editorRoutes[key], { lastState: 'base' });
            return supportedGameRequired();
          }, supportedGameRequired);
        };
      });
      function refreshLocalMenu() {
        var policy = window.__dulukaOscInterface;
        if (!policy || !policy.fullInterface || !policy.navigationEnabled) return;
        var element = document.querySelector && document.querySelector('nv-main-menu');
        var preferencesElement = document.querySelector && document.querySelector('nv-preferences-menu');
        if (!element && !preferencesElement) return;
        var menu = element && angular.element(element).controller('nvMainMenu');
        var preferences = preferencesElement && angular.element(preferencesElement).controller('nvPreferencesMenu');
        // The user wants the full OSC interface visible, including entries
        // whose underlying service is currently unavailable.
        ['Screenshot', 'Ansel', 'Mods'].forEach(function (key) {
          if (menu && menu.tiles && menu.tiles[key]) menu.tiles[key].extraclass = 'three-left';
        });
          if (preferences && policy.preferencesEnabled) {
            preferences.tiles.forEach(function (tile) {
              tile.visible = true;
              tile.precondition = allowNavigation;
            });
          }
          if (!menu || !menu.tiles) return;
          // The screenshot tile replaces the legacy discovery hint.
          menu.showDiscoveryBanner = hideDiscovery;
          policy.menus.forEach(function (key) {
            if (!menu.tiles[key]) return;
            menu.tiles[key].visible = true;
            menu.tiles[key].enabled = true;
            var states = menu.tiles[key].state || {};
            Object.keys(states).forEach(function (state) {
              (states[state].items || []).forEach(function (item) {
                item.visible = true;
                item.enabled = true;
              });
            });
          });
          ['Ansel', 'Mods'].forEach(function (key) {
            if (menu.tiles[key]) menu.tiles[key].click = editorClicks[key];
          });
      }
      // Apply cached backend policy in the same Angular update that creates
      // the controller. No two-second timer or HTTP roundtrip on menu entry.
      $injector.get('$rootScope').$watch(function () { refreshLocalMenu(); });
      $injector.get('$http').get('/Duluka/v.1.0/OSC/Interface', { cache: false, timeout: 1800 }).then(function (response) {
        window.__dulukaOscInterface = response.data;
        refreshLocalMenu();
      });
      refreshLocalMenu();
    }
    if (!window.__dulukaSupportedGameGateInstalled) {
      window.__dulukaSupportedGameGateInstalled = true;
      var checkingGame = false;
      function checkSupportedGame() {
        if (checkingGame) return;
        checkingGame = true;
        $injector.get('$http').get('/Duluka/v.1.0/GameFilter/State', {cache:false,timeout:1800}).then(function (response) {
          $injector.get('nvCameraService').__anselConnected = !!(response.data && response.data.connected);
        }, function () {
          $injector.get('nvCameraService').__anselConnected = false;
          var state = $injector.get('$state');
          if (state.current && (state.current.name === 'mods' || state.current.name === 'nvcamera')) {
            $injector.get('oscDisplayService').closeOSC();
            state.go('base');
            $injector.get('cefService').query({command:'QUERY_WIN_CLOSE_OSC'});
          }
        }).finally(function () { checkingGame = false; });
      }
      $injector.get('$interval')(checkSupportedGame, 2000);
      checkSupportedGame();
    }
    if (window.__dulukaAnselUiInstalled) return;
    window.__dulukaAnselUiInstalled = true;
    function adoptInitialRuntime(camera, state, $injector) {
      if (!state.stack.length || camera.UIRunning) return;
      var storage = $injector.get('$window').localStorage;
      var stored;
      try { stored = JSON.parse(storage.getItem('ModsSlotStorage') || '{}'); }
      catch (error) { return; } // Preserve malformed user data for diagnosis.
      var slots = camera.persistSlots[state.executable] || stored[state.executable];
      if (slots && slots.some(function (slot) { return slot.activeFilterStack && slot.activeFilterStack.length; })) {
        // Reopening an existing game must also protect the initial Off call.
        // Select the preset matching the currently running ReShade stack.
        var matching = slots.filter(function (slot) {
          return JSON.stringify((slot.activeFilterStack || []).map(function (filter) { return filter.id; })) === JSON.stringify(state.stack);
        })[0];
        if (matching) {
          camera.anselInitialSlot = matching.id;
          camera.lastActiveSlot = camera.lastSlot = matching.id - 1;
        }
        return;
      }
      slots = slots || [1, 2, 3, 4].map(function (id) {
        return { id: id, filters: [], activeFilterStack: [], activeIndex: -1 };
      });
      var models = state.filters.map(function (filter) {
        var saved = {};
        filter.controls.forEach(function (control) { saved[filter.id + '#' + control.id] = control.currentValue.slice(); });
        return { id: filter.id, title: filter.name, savedConfigs: saved, controls: [], visibleInCombo: state.stack.indexOf(filter.id) < 0 };
      });
      slots[0].filters = models;
      slots[0].activeFilterStack = state.stack.map(function (id) { return models.filter(function (filter) { return filter.id === id; })[0]; }).filter(Boolean);
      slots[0].activeFilter = slots[0].activeFilterStack[0];
      slots[0].activeIndex = slots[0].activeFilter ? 0 : -1;
      camera.persistSlots[state.executable] = slots;
      camera.lastActiveSlot = camera.lastSlot = 0;
      camera.anselInitialSlot = 1;
      stored[state.executable] = slots;
      $injector.get('oscDisplayService').setLocalStorage('ModsSlotStorage', JSON.stringify(stored));
      var data;
      try { data = JSON.parse(storage.getItem('ModsDataStorage') || '{}'); } catch (error) { data = {}; }
      data[state.executable] = { lastSlot: 0, modsIsEnabled: true };
      $injector.get('oscDisplayService').setLocalStorage('ModsDataStorage', JSON.stringify(data));
    }
    function runWithOfflineAvailability(invoke, context, args, $injector) {
      // The legacy UI uses NVIDIA cloud allowlists to gate entry. A connected
      // local ReShade runtime is the authority for our adapter instead. Do not
      // grant availability merely because Container is in UI test mode.
      var $http = $injector.get('$http');
      return $http.get('/Duluka/v.1.0/GameFilter/State', { cache: false, timeout: 2500 }).then(function (response) {
        if (!response.data || response.data.connected !== true) {
          $injector.get('nvCameraService').__anselConnected = false;
          return supportedGameRequired();
        }
        var camera = $injector.get('nvCameraService');
        camera.__anselConnected = true;
        adoptInitialRuntime(camera, response.data, $injector);
        return camera.setModsStatusToNvcamera(true).then(function () {
          return camera.checkReshadeSupport();
        }).then(function () {
          return invokeWithLocalAvailability(invoke, context, args, $injector);
        });
      }, function () {
        $injector.get('nvCameraService').__anselConnected = false;
        return supportedGameRequired();
      });
    }

    function invokeWithLocalAvailability(invoke, context, args, $injector) {
        var oscService = $injector.get('oscService');
        var wasOnline = oscService.onlineState.online;
        if (wasOnline !== true) return invoke.apply(context, args);

        var $rootScope = $injector.get('$rootScope');
        var $timeout = $injector.get('$timeout');
        var restored = false;
        var removeRouteListener;
        var restoreTimeout;

        function restoreOnlineState() {
          if (restored) return;
          restored = true;
          if (removeRouteListener) removeRouteListener();
          if (restoreTimeout) $timeout.cancel(restoreTimeout);
          oscService.onlineState.online = wasOnline;
        }

        removeRouteListener = $rootScope.$on('$stateChangeSuccess', function (event, toState) {
          if (toState && /(?:^|\.)(?:mods|nvcamera)$/.test(toState.name)) {
            restoreOnlineState();
          }
        });
        restoreTimeout = $timeout(restoreOnlineState, 15000, false);
        // This is a local entry check. Broadcasting an offline transition
        // reinitializes NVIDIA cloud capabilities and disables live effects.
        oscService.onlineState.online = false;

        var result;
        try {
          result = invoke.apply(context, args);
        } catch (error) {
          restoreOnlineState();
          throw error;
        }
        if (result && typeof result.finally === 'function') result.finally(restoreOnlineState);
        return result;
    }

    (function ($delegate) {
      var trigger = $delegate.trigger;
      if (typeof trigger !== 'function') {
        throw new Error('eventAggregator.trigger is unavailable');
      }

      $delegate.trigger = function (eventName) {
        if (eventName === 'Hotkey_ModsShowUI') {
          return runWithOfflineAvailability(trigger, $delegate, arguments, $injector);
        }
        return trigger.apply($delegate, arguments);
      };
      return $delegate;
    })($injector.get('eventAggregator'));

    (function ($delegate) {
      // Legacy cloud initialization can disable the NVIDIA capability after
      // switching offline. A verified local runtime supplies this capability.
      ['isOn', 'isModsOn', 'hasVariableAvailability'].forEach(function (name) {
        var original = $delegate[name];
        $delegate[name] = function () {
          return !!$delegate.__anselConnected || (original && original.apply(this, arguments));
        };
      });
      var launchUIForMods = $delegate.launchUIForMods;
      if (typeof launchUIForMods !== 'function') {
        throw new Error('nvCameraService.launchUIForMods is unavailable');
      }

      $delegate.launchUIForMods = function () {
        return runWithOfflineAvailability(launchUIForMods, $delegate, arguments, $injector);
      };
      // The original UI starts a new game in its Off slot. Import the actual
      // ReShade stack once when no OSC preset exists, without switching effects
      // off simply because the editor was opened.
      var enableModsMode = $delegate.enableModsMode;
      var applySlot = $delegate.applySlot;
      var importing = false;
      $delegate.enableModsMode = function (enabled) {
        importing = enabled && !!$delegate.anselInitialSlot;
        var result = enableModsMode.apply($delegate, arguments);
        if (importing) return result.finally(function () { importing = false; $delegate.anselInitialSlot = null; });
        return result;
      };
      $delegate.applySlot = function (slot) {
        if (importing && !slot) slot = $delegate.modsSlots[$delegate.anselInitialSlot - 1];
        return applySlot.call($delegate, slot);
      };
      var addCurrentFilter = $delegate.modsHelper.addCurrentFilter;
      $delegate.modsHelper.addCurrentFilter = function (slot) {
        return addCurrentFilter.apply(this, arguments).then(function (result) {
          if (slot && slot.activeFilterStack.length)
            $delegate.getCurrentFilterAttribute(slot.activeFilterStack.length - 1);
          return result;
        });
      };
      // One in-flight slider update per control; retain the newest pending
      // value rather than building a queue of obsolete drag positions.
      var setAttribute = $delegate.setAttributeV1;
      var updates = {};
      $delegate.setAttributeV1 = function (id, slot, control, value) {
        var args = Array.prototype.slice.call(arguments);
        if (Array.isArray(value)) args[3] = value.slice();
        var key = JSON.stringify([id, slot, control]);
        var update = updates[key] || (updates[key] = { busy: false, scheduled: false, waiters: [] });
        var deferred = $injector.get('$q').defer();
        update.args = args;
        update.waiters.push(deferred);
        function flush() {
          update.scheduled = false;
          update.busy = true;
          var next = update.args, waiters = update.waiters;
          update.args = null; update.waiters = [];
          setAttribute.apply($delegate, next).then(function (response) {
            waiters.forEach(function (waiter) { waiter.resolve(response); });
          }, function (error) {
            waiters.forEach(function (waiter) { waiter.reject(error); });
          }).finally(function () {
            update.busy = false;
            if (update.args) schedule(); else delete updates[key];
          });
        }
        function schedule() {
          if (update.busy || update.scheduled) return;
          update.scheduled = true;
          $injector.get('$timeout')(flush, 16, false);
        }
        schedule();
        return deferred.promise;
      };
      return $delegate;
    })($injector.get('nvCameraService'));
  }
  var injector = angular.element(document).injector();
  if (injector) install(injector);
  else angular.module('main').run(['$injector', install]);
})(window.angular);
