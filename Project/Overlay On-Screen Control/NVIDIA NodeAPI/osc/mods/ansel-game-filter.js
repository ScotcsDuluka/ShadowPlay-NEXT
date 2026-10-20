(function (angular) {
  'use strict';

  if (!angular) return;

  function install($injector) {
    var selectionStorageKey = 'DulukaModsSelectionStorage';
    function readSelections() {
      try { return JSON.parse($injector.get('$window').localStorage.getItem(selectionStorageKey) || '{}'); }
      catch (error) { return {}; }
    }
    function selectedSlot(camera) {
      var choices = readSelections(), choice = choices && choices[camera.__dulukaModsExecutable];
      return Number.isInteger(choice) && choice >= -1 && choice < camera.NUM_MODS_SLOTS ? choice : undefined;
    }
    function mergeSelections(value) {
      var data, choices = readSelections();
      try { data = JSON.parse(value); } catch (error) { return value; }
      if (!data || typeof data !== 'object' || Array.isArray(data)) return value;
      Object.keys(data).forEach(function (game) {
        var choice = choices && choices[game];
        if (!Number.isInteger(choice) || choice < -1 || choice > 2 ||
            !data[game] || typeof data[game] !== 'object' || Array.isArray(data[game])) return;
        data[game].modsIsEnabled = choice !== -1;
        if (choice !== -1) data[game].lastSlot = choice;
      });
      return JSON.stringify(data);
    }
    if (!window.__dulukaModsSelectionInstalled) {
      window.__dulukaModsSelectionInstalled = true;
      var selectionDisplay = $injector.get('oscDisplayService');
      var setSelectionStorage = selectionDisplay.setLocalStorage;
      selectionDisplay.setLocalStorage = function (key, value) {
        // Native shutdown can write its old enabled flag after an explicit Off.
        if (key === 'ModsDataStorage') value = mergeSelections(value);
        return setSelectionStorage.call(this, key, value);
      };
      var selectionCamera = $injector.get('nvCameraService');
      var shutdownSelectionUI = selectionCamera.shutdownModsUI;
      if (typeof shutdownSelectionUI === 'function') {
        selectionCamera.shutdownModsUI = function () {
          var game = selectionCamera.__dulukaModsExecutable, slots;
          // Native keep-effects shutdown skips its private preset writer.
          // Save once at close, rather than serializing controls while dragging.
          if (game && Array.isArray(selectionCamera.modsSlots)) {
            try {
              slots = JSON.parse($injector.get('$window').localStorage.getItem('ModsSlotStorage') || '{}');
              if (slots && typeof slots === 'object' && !Array.isArray(slots)) {
                slots[game] = selectionCamera.modsSlots;
                selectionDisplay.setLocalStorage('ModsSlotStorage', JSON.stringify(slots));
              }
            } catch (error) { console.error('[game-filter] Preset save failed:', error); }
          }
          return shutdownSelectionUI.apply(this, arguments);
        };
      }
      window.addEventListener('click', function (event) {
        var button = event.target.closest && event.target.closest('mods-menu .mods-slot');
        if (!button) return;
        var camera = $injector.get('nvCameraService'), game = camera.__dulukaModsExecutable;
        if (!game || !camera.__anselConnected) return;
        var buttons = button.closest('mods-menu').querySelectorAll('.mods-slot');
        var index = Array.prototype.indexOf.call(buttons, button);
        if (index < 0 || index > camera.NUM_MODS_SLOTS) return;
        var choices = readSelections();
        if (!choices || typeof choices !== 'object' || Array.isArray(choices)) return;
        choices[game] = index === camera.NUM_MODS_SLOTS ? -1 : index;
        camera.__dulukaEntryPrepared = false;
        camera.__dulukaEntrySlot = null;
        selectionDisplay.setLocalStorage(selectionStorageKey, JSON.stringify(choices));
        var data;
        try { data = JSON.parse($injector.get('$window').localStorage.getItem('ModsDataStorage') || '{}'); }
        catch (error) { return; }
        if (!data || typeof data !== 'object' || Array.isArray(data)) return;
        if (!data[game] || typeof data[game] !== 'object' || Array.isArray(data[game]))
          data[game] = {lastSlot:camera.lastSlot};
        selectionDisplay.setLocalStorage('ModsDataStorage', JSON.stringify(data));
      }, true);
    }
    function rememberRuntime(camera, state) {
      var identity = state && state.connected === true ? JSON.stringify([state.pid, state.session, state.generation]) : null;
      if (camera.__dulukaModsRuntime !== identity) {
        camera.__dulukaModsPrepared = null;
        camera.__dulukaEntrySlot = null;
        camera.__dulukaEntryPrepared = false;
      }
      camera.__dulukaModsRuntime = identity;
      camera.__dulukaModsExecutable = identity && state.executable || '';
      camera.__anselConnected = !!identity;
    }
    function canReuseMods(camera) {
      var slot = camera.modsSlots && camera.modsSlots[camera.lastSlot];
      return camera.__anselConnected && camera.modsSupported && camera.modsIsEnabled &&
        camera.__dulukaModsRuntime && camera.__dulukaModsPrepared === camera.__dulukaModsRuntime &&
        camera.lastSlot >= 0 && camera.lastSlot < camera.NUM_MODS_SLOTS &&
        slot && Array.isArray(slot.filters);
    }
    if (!window.__dulukaModsReadinessInstalled) {
      window.__dulukaModsReadinessInstalled = true;
      var readyCamera = $injector.get('nvCameraService');
      var initializeCamera = readyCamera.init;
      if (typeof initializeCamera === 'function') {
        var initializedCamera = $injector.get('$q').defer();
        readyCamera.__dulukaNativeInitialization = initializedCamera.promise;
        readyCamera.init = function () {
          return initializeCamera.apply(this, arguments).then(function (value) {
            initializedCamera.resolve(true);
            return value;
          }, function (error) {
            initializedCamera.reject(error);
            throw error;
          });
        };
      }
      var getSupportedFilterTypes = readyCamera.getSupportedFilterTypes;
      var enableReadyMods = readyCamera.enableModsMode;
      var preparingMods;
      readyCamera.getSupportedFilterTypes = function () {
        readyCamera.__dulukaModsPrepared = null;
        // NVIDIA's one-shot FILTERS callback removes itself without resetting
        // setupFilterStackEvents' internal flag. Re-arm before each catalog read.
        readyCamera.setupFilterStackEvents(false);
        readyCamera.setupFilterStackEvents(true);
        return getSupportedFilterTypes.apply(this, arguments);
      };
      readyCamera.enableModsMode = function (enabled) {
        if (enabled && preparingMods) return preparingMods;
        if (!enabled) readyCamera.__dulukaModsPrepared = null;
        var identity = readyCamera.__dulukaModsRuntime;
        var result = enableReadyMods.apply(this, arguments);
        var pending = result.then(function (value) {
          if (enabled && readyCamera.modsIsEnabled && identity && identity === readyCamera.__dulukaModsRuntime)
            readyCamera.__dulukaModsPrepared = identity;
          return value;
        });
        if (!enabled) return pending;
        var flight = pending.finally(function () {
          if (preparingMods === flight) preparingMods = null;
        });
        preparingMods = flight;
        return preparingMods;
      };
    }
    if (!window.__dulukaPhotoUnavailableInstalled) {
      window.__dulukaPhotoUnavailableInstalled = true;
      function photoUnavailable() {
        if (typeof Notifier !== 'undefined') Notifier.show('Photo mode', {subtext:'This feature is currently unavailable.',group:'photo-unavailable',icon:'\uE940'});
        return false;
      }
      $injector.get('nvCameraService').launchUIForNvCamera = photoUnavailable;
      $injector.get('$rootScope').$on('$stateChangeStart', function (event, target) {
        if (target && target.name === 'nvcamera') { event.preventDefault(); photoUnavailable(); }
      });
    }
    if (!window.__dulukaSliderLayoutInstalled && $injector.has && $injector.has('nvSliderDirective')) {
      window.__dulukaSliderLayoutInstalled = true;
      $injector.get('nvSliderDirective').forEach(function (directive) {
        var compile = directive.compile;
        if (typeof compile !== 'function') return;
        directive.compile = function () {
          var link = compile.apply(this, arguments);
          if (typeof link !== 'function') return link;
          return function (scope, element) {
            var node = element[0];
            if (!node.closest('mods-menu')) return link.apply(this, arguments);
            var track = node.querySelector('.nv-track-container');
            var thumb = node.querySelector('.nv-thumb');
            if (!track || !thumb) return link.apply(this, arguments);
            var rect, width;
            var originalRect = track.getBoundingClientRect;
            var ownRect = Object.getOwnPropertyDescriptor(track, 'getBoundingClientRect');
            var ownWidth = Object.getOwnPropertyDescriptor(thumb, 'offsetWidth');
            var widthDescriptor = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'offsetWidth');
            if (!widthDescriptor || !widthDescriptor.get) return link.apply(this, arguments);
            track.getBoundingClientRect = function () {
              if (!rect || !rect.width) rect = originalRect.call(track);
              return rect;
            };
            Object.defineProperty(thumb, 'offsetWidth', {configurable:true,get:function () {
              if (!width) width = widthDescriptor.get.call(thumb);
              return width;
            }});
            function restore() {
              if (ownRect) Object.defineProperty(track, 'getBoundingClientRect', ownRect);
              else delete track.getBoundingClientRect;
              if (ownWidth) Object.defineProperty(thumb, 'offsetWidth', ownWidth);
              else delete thumb.offsetWidth;
            }
            // Attribute observers update ARIA and thumb styles repeatedly in
            // the first digest. Track geometry is stable within that frame;
            // drag, resize and subsequent updates must read fresh geometry.
            window.requestAnimationFrame(restore);
            try { return link.apply(this, arguments); }
            catch (error) { restore(); throw error; }
          };
        };
      });
    }
    function restoreLocalModsSlot(camera) {
      if (camera.modsIsEnabled && camera.modsSlots) {
        var choice = selectedSlot(camera);
        if (choice !== undefined) camera.modsActiveSlot = choice < 0 ? undefined : camera.modsSlots[choice];
        camera.setupFilterStackEvents(true);
        camera.__dulukaEntrySlot = camera.modsActiveSlot;
        camera.__dulukaEntryPrepared = true;
      }
    }
    if (!window.__dulukaEntrySlotReuseInstalled) {
      window.__dulukaEntrySlotReuseInstalled = true;
      var slotCamera = $injector.get('nvCameraService');
      var originalApplySlot = slotCamera.applySlot;
      slotCamera.applySlot = function (slot) {
        var prepared = slotCamera.__dulukaEntrySlot;
        // Older native controllers clear once before restoring the same slot.
        // An explicit UI click clears this token in the capture handler above.
        if (slotCamera.__dulukaEntryPrepared && prepared && !slot) return prepared;
        if (slotCamera.__dulukaEntryPrepared && slot === prepared) {
          slotCamera.__dulukaEntryPrepared = false;
          slotCamera.__dulukaEntrySlot = null;
          slotCamera.modsActiveSlot = prepared;
          return prepared;
        }
        slotCamera.__dulukaEntryPrepared = false;
        slotCamera.__dulukaEntrySlot = null;
        return originalApplySlot.apply(this, arguments);
      };
    }
    if (!window.__dulukaPreparedModsEntryInstalledV2) {
      window.__dulukaPreparedModsEntryInstalledV2 = true;
      var entryCamera = $injector.get('nvCameraService');
      var previousEntry = entryCamera.launchUIForMods;
      entryCamera.launchUIForMods = function () {
        var state = $injector.get('$state');
        if (canReuseMods(entryCamera)) {
          restoreLocalModsSlot(entryCamera);
          if (state.current.name === 'mods') return $injector.get('$q').when(true);
          entryCamera.UIRunning = true;
          return state.go('mods', {lastState:state.current.name,lastParams:state.params}).then(function () {
            if (state.current.name === 'mods')
              return $injector.get('cefService').query({command:'QUERY_WIN_OPEN_OSC'});
          });
        }
        if (state.current.name !== 'mods' && state.current.name !== 'nvcamera') entryCamera.UIRunning = false;
        return previousEntry.apply(this, arguments);
      };
    }
    if (!window.__dulukaFocusToastInstalledV2 && $injector.has && $injector.has('socketService')) {
      window.__dulukaFocusToastInstalledV2 = true;
      var focusEvents = $injector.get('eventAggregator');
      function registerFocusToast() { $injector.get('socketService').register('/Duluka/v1/OverlayGameEntry', 'duluka-overlay-game-entry'); }
      focusEvents.on('connect', registerFocusToast);
      focusEvents.on('duluka-overlay-game-entry', function (data) {
        if (data && data.executablePath && window.__dulukaReplayGameEntry)
          return window.__dulukaReplayGameEntry({executablePath:data.executablePath,displayName:data.displayName});
        if (data && typeof data.message === 'string' && typeof Notifier !== 'undefined')
          Notifier.show(data.message, {subtext:data.subtext || '',group:'overlay-focus',icon:'\uE932'});
      });
      registerFocusToast();
    }
    if (!window.__dulukaFastModsToggleInstalledV3) {
      window.__dulukaFastModsToggleInstalledV3 = true;
      var events = $injector.get('eventAggregator');
      var previousTrigger = events.trigger;
      events.trigger = function (name) {
        var camera = $injector.get('nvCameraService');
        var state = $injector.get('$state');
        var display = $injector.get('oscDisplayService');
        if (name === 'Hotkey_ModsShowUI' && canReuseMods(camera) && display.openOSC) {
          // Toggling a prepared editor is local UI work. Re-entering the
          // native initialization path reapplies every saved uniform.
          var root = $injector.get('$rootScope');
          var toggle = function () {
            if (state.current.name === 'mods') {
              camera.UIRunning = false;
              display.closeOSC();
              state.go('base');
              $injector.get('cefService').query({command:'QUERY_WIN_CLOSE_OSC'});
            } else {
              restoreLocalModsSlot(camera);
              camera.UIRunning = true;
              state.go('mods', {lastState:state.current.name,lastParams:state.params}).then(function () {
                if (state.current.name === 'mods')
                  $injector.get('cefService').query({command:'QUERY_WIN_OPEN_OSC'});
              });
            }
          };
          return root.$$phase ? toggle() : root.$apply(toggle);
        }
        return previousTrigger.apply(this, arguments);
      };
    }
    if (!window.__dulukaEditorBackInstalled) {
      window.__dulukaEditorBackInstalled = true;
      // Window capture also supersedes the older document close handler when
      // this mod is updated without restarting OSC.
      window.addEventListener('click', function (event) {
        var button = event.target.closest && event.target.closest('.mods-close-button');
        if (!button) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        $injector.get('$rootScope').$evalAsync(function () {
          $injector.get('nvCameraService').UIRunning = false;
          $injector.get('$state').go('main.main-menu');
        });
      }, true);
    }
    if (!window.__dulukaEditorLifecycleInstalled) {
      window.__dulukaEditorLifecycleInstalled = true;
      function reconcileEditorState(name) {
        if (name !== 'mods' && name !== 'nvcamera')
          $injector.get('nvCameraService').UIRunning = false;
      }
      $injector.get('$rootScope').$on('$stateChangeSuccess', function (event, state) {
        if (state) reconcileEditorState(state.name);
      });
      reconcileEditorState($injector.get('$state').current.name);
    }
    function supportedGameRequired() {
      $injector.get('oscNotificationService').show($injector.get('NOTIFIER_SELECTIONS').WARNING_SUPPORTED_GAME_REQUIRED);
      return false;
    }
    if (!window.__dulukaGameEditorRouteGuardInstalled) {
      window.__dulukaGameEditorRouteGuardInstalled = true;
      var pendingEditorEntry;
      $injector.get('$rootScope').$on('$stateChangeStart', function (event, toState, params) {
        if (!toState || (toState.name !== 'mods' && toState.name !== 'nvcamera')) return;
        var camera = $injector.get('nvCameraService');
        if (camera.__anselConnected && camera.modsSupported && camera.persistSlots &&
            camera.persistSlots[camera.__dulukaModsExecutable] === camera.modsSlots) return;
        event.preventDefault();
        if (pendingEditorEntry) return;
        var state = $injector.get('$state');
        // The base state's onEnter starts the socket and camera service. A
        // prevented initial deep link must bootstrap that state before waiting.
        var bootstrap = !state.current.name ? state.go('main.main-menu') : $injector.get('$q').when(true);
        pendingEditorEntry = bootstrap.then(function () {
          return camera.__dulukaNativeInitialization;
        }).then(function () {
          return $injector.get('$http').get('/Duluka/v.1.0/GameFilter/State', {cache:false,timeout:1800});
        }).then(function (response) {
          if (response.data && response.data.connected) {
            rememberRuntime(camera, response.data);
            // A connected pipe alone does not initialize NVIDIA's notification
            // listeners or per-game slots. Deep links must use the normal entry.
            if (toState.name === 'mods') return camera.launchUIForMods();
            $injector.get('$state').go(toState.name, params);
          } else supportedGameRequired();
        }, supportedGameRequired).finally(function () { pendingEditorEntry = null; });
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
          rememberRuntime($injector.get('nvCameraService'), response.data);
        }, function () {
          rememberRuntime($injector.get('nvCameraService'), null);
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
      if (selectedSlot(camera) !== undefined) return;
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
          rememberRuntime($injector.get('nvCameraService'), null);
          return supportedGameRequired();
        }
        var camera = $injector.get('nvCameraService');
        rememberRuntime(camera, response.data);
        adoptInitialRuntime(camera, response.data, $injector);
        var choice = selectedSlot(camera);
        if (choice !== undefined) {
          camera.lastActiveSlot = choice;
          if (choice >= 0) {
            camera.lastSlot = choice;
            camera.anselInitialSlot = choice + 1;
          } else {
            camera.modsActiveSlot = undefined;
            camera.anselInitialSlot = null;
          }
        }
        return (response.data.enabled ? $injector.get('$q').when(true) : camera.setModsStatusToNvcamera(true)).then(function () {
          return camera.checkReshadeSupport();
        }).then(function () {
          return invokeWithLocalAvailability(invoke, context, args, $injector);
        });
      }, function () {
        rememberRuntime($injector.get('nvCameraService'), null);
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
          var camera = $injector.get('nvCameraService');
          if (canReuseMods(camera) &&
              $injector.get('oscDisplayService').openOSC)
            return trigger.apply($delegate, arguments);
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
        if (canReuseMods($delegate))
          return launchUIForMods.apply($delegate, arguments);
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
