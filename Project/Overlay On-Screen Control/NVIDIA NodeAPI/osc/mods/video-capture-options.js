(function () {
  'use strict';

  var endpoint = '/DulukaCapture/v.1.0/settings';
  var catalogEndpoint = '/DulukaCapture/v.1.0/engines';
  var panelId = 'nv-video-capture-options';
  var panelStackId = 'nv-video-capture-panels';
  var panelVersion = '69';
  var state = null;
  var statusNode = null;
  var captureFields = [];
  var captureFieldsRow = null;
  var previewNode = null;
  var runtimeStatusNode = null;
  var runtimeStatusTimer = null;
  var encoderNames = {};
  var encoderCatalog = null;
  var displayRecords = [];
  var displayStatusNode = null;
  var panelScope = null;
  var injector = null;
  var presetChoices = {
    NVENC_H264: ['P1', 'P2', 'P3', 'P4', 'P5', 'P6', 'P7'],
    NVENC_HEVC: ['P1', 'P2', 'P3', 'P4', 'P5', 'P6', 'P7'],
    NVENC_AV1: ['P1', 'P2', 'P3', 'P4', 'P5', 'P6', 'P7'],
    QuickSync_H264: ['veryslow', 'slower', 'slow', 'medium', 'fast', 'faster', 'veryfast'],
    QuickSync_HEVC: ['veryslow', 'slower', 'slow', 'medium', 'fast', 'faster', 'veryfast'],
    AMF_H264: [{ index: 2, label: 'quality' }, { index: 4, label: 'balanced' }, { index: 6, label: 'speed' }],
    AMF_HEVC: [{ index: 2, label: 'quality' }, { index: 4, label: 'balanced' }, { index: 6, label: 'speed' }],
    LibX264: ['slow', 'medium', 'fast', 'faster', 'veryfast', 'superfast', 'ultrafast'],
    LibX265: ['slow', 'medium', 'fast', 'faster', 'veryfast', 'superfast', 'ultrafast']
  };
  var encoderChoices = [
    ['NVENC_H264', 'NVIDIA NVENC H.264'],
    ['NVENC_HEVC', 'NVIDIA NVENC HEVC'],
    ['NVENC_AV1', 'NVIDIA NVENC AV1'],
    ['QuickSync_H264', 'Intel Quick Sync H.264'],
    ['QuickSync_HEVC', 'Intel Quick Sync HEVC'],
    ['AMF_H264', 'AMD AMF H.264'],
    ['AMF_HEVC', 'AMD AMF HEVC'],
    ['LibX264', 'Software x264'],
    ['LibX265', 'Software x265']
  ];
  // Container publishes route/catalog identifiers, while OSC settings use
  // legacy encoder keys. These aliases DO NOT certify hardware runtime.
  var catalogEncoderAliases = {
    h264_nvenc: 'NVENC_H264',
    hevc_nvenc: 'NVENC_HEVC',
    av1_nvenc: 'NVENC_AV1',
    h264_qsv: 'QuickSync_H264',
    hevc_qsv: 'QuickSync_HEVC',
    h264_amf: 'AMF_H264',
    hevc_amf: 'AMF_HEVC',
    libx264: 'LibX264',
    libx265: 'LibX265'
  };
  function parseEncoderCatalog(body) {
    if (!body || body.schemaVersion !== 1 || body.readOnly !== true ||
        !Array.isArray(body.providers)) {
      throw new Error('Invalid NVIDIA Container capture catalog');
    }
    var mapping = {};
    ['Duluka', 'FFmpeg'].forEach(function (engine) {
      var provider = body.providers.filter(function (item) {
        return item && item.id === engine && item.routeWired === true &&
          Array.isArray(item.encoders);
      })[0];
      if (!provider) throw new Error('Capture route unavailable: ' + engine);
      mapping[engine] = provider.encoders.map(function (id) {
        return catalogEncoderAliases[id] || id;
      }).filter(function (id, index, list) {
        return encoderChoices.some(function (choice) { return choice[0] === id; }) &&
          list.indexOf(id) === index;
      });
      if (!mapping[engine].length) throw new Error('No known codec route for ' + engine);
    });
    return {
      routes: mapping,
      policyValid: body.policyValid === true,
      binaryVerified: body.providers.some(function (item) {
        return item && item.id === 'FFmpeg' && item.capabilityVerified === true;
      }),
      hardwareVerified: body.providers.some(function (item) {
        return item && item.id === 'FFmpeg' && item.hardwareVerified === true;
      })
    };
  }
  function allowedEncoders(engine) {
    return encoderCatalog && encoderCatalog.routes[engine] ?
      encoderCatalog.routes[engine].slice() : [];
  }
  function syncEncoderControls() {
    if (!panelScope || !state) return;
    var available = allowedEncoders(state.engineMode);
    var choices = encoderChoices.filter(function (choice) {
      return available.indexOf(choice[0]) >= 0;
    }).map(function (choice) {
      return { value: choice[0], label: choice[1], disabled: false };
    });
    if (encoderCatalog && available.indexOf(state.encoder) < 0) {
      // Preserve the actual persisted encoder in the visible UI; never
      // silently rewrite it to NVENC_H264 during GET/read-only rendering.
      choices.push({ value: state.encoder,
        label: (encoderNames[state.encoder] || state.encoder) +
          ' (saved; unsupported for ' + state.engineMode + ')',
        disabled: true });
    }
    panelScope.captureEncoderChoices = choices;
    panelScope.captureUi.encoder = state.encoder;
    panelScope.captureCodecStatus = !encoderCatalog ?
      'Codec routes unavailable — reload to retry.' :
      (available.indexOf(state.encoder) < 0 ?
        'Saved codec unavailable in the selected engine; select another codec.' :
        state.engineMode === 'Duluka' ?
          'Duluka Native: H.264 NVENC only.' :
          (encoderCatalog.hardwareVerified ?
            'FFmpeg hardware readiness reported by Capture.' :
            'FFmpeg codec list ≠ GPU support. Intel QSV requires an available Intel GPU.')) +
      (encoderCatalog && !encoderCatalog.policyValid ?
        ' Capture routing policy unverified.' : '');
    schedulePanelDigest();
  }
  function readCaptureSettingsAndCatalog() {
    // Both responses must be valid before the UI can mutate persisted
    // recording settings. A missing catalog must not restore 9 guessed codecs.
    return Promise.all([
      fetchWithTimeout(endpoint, {}, 12000).then(function (response) {
        if (!response.ok) throw new Error('Unable to load capture settings (' + response.status + ')');
        return response.json();
      }),
      fetchWithTimeout(catalogEndpoint, {}, 3500).then(function (response) {
        if (!response.ok) throw new Error('Unable to load codec routes (' + response.status + ')');
        return response.json();
      })
    ]).then(function (responses) {
      if (!usableCaptureSettings(responses[0])) {
        throw new Error('Invalid capture settings response');
      }
      var verifiedCatalog = parseEncoderCatalog(responses[1]);
      // Apply together only after both requests passed validation.
      encoderCatalog = verifiedCatalog;
      state = responses[0];
      panelScope.captureSettingsReady = true;
      render();
    });
  }
  function videoPage() {
    return /#\/base\/preferences\/video(?:[/?]|$)/.test(window.location.hash);
  }

  function fetchWithTimeout(url, options, timeoutMs) {
    var limit = Number(timeoutMs) > 0 ? Number(timeoutMs) : 12000;
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
  function clearLegacyPageLayout() {
    var style = document.getElementById('nv-video-capture-options-style');
    if (style) style.parentNode.removeChild(style);
    document.body.classList.remove('nv-video-capture-options-page');
  }

  function clearPreviousPanelSizing(panel) {
    if (!panel || !panel.style) return;
    var previousValues = {
      height: 'auto',
      'min-height': '0',
      'max-height': 'none',
      flex: '0 0 auto',
      'align-self': 'stretch',
      width: '100%',
      'min-width': '0',
      overflow: 'visible',
      'box-sizing': 'border-box',
      'margin-bottom': '16px'
    };
    Object.keys(previousValues).forEach(function (property) {
      if (panel.style.getPropertyValue(property) === previousValues[property]) {
        panel.style.removeProperty(property);
      }
    });
    if (panel.style.getPropertyValue('margin-top') === '0px' ||
        panel.style.getPropertyValue('margin-top') === '28px') {
      panel.style.removeProperty('margin-top');
    }
  }

  function captureDisplayKey(display) {
    return String(display.AdapterLuidHigh) + ':' +
      String(display.AdapterLuidLow) + ':' + String(display.OutputIndex);
  }

  function refreshCaptureDisplays() {
    if (!panelScope) return Promise.resolve(false);
    panelScope.captureDisplayReady = false;
    panelScope.captureDisplayStatus = 'Reading displays from NVIDIA Capture…';
    schedulePanelDigest();
    return fetchWithTimeout('/DulukaCapture/v.1.0/displays', {}, 3500)
      .then(function (response) {
        if (!response.ok) throw new Error('Display gateway unavailable (' + response.status + ')');
        return response.json();
      }).then(function (body) {
        if (!body || body.schemaVersion !== 1 || !Array.isArray(body.displays) ||
            body.selectionValid !== true || !body.selected) {
          throw new Error('Selected display unavailable');
        }
        displayRecords = body.displays.filter(function (display) {
          return display && Number.isInteger(display.OutputIndex) &&
            Number.isInteger(display.AdapterLuidHigh) &&
            Number.isInteger(display.AdapterLuidLow) &&
            display.Width > 0 && display.Height > 0;
        });
        var selected = displayRecords.filter(function (d) {
          return captureDisplayKey(d) === captureDisplayKey(body.selected);
        })[0];
        if (!selected || !displayRecords.length) {
          throw new Error('Saved display is disconnected');
        }
        panelScope.captureDisplayChoices = displayRecords.map(function (display) {
          return { value: captureDisplayKey(display),
            label: display.Label || ('Display ' + (display.OutputIndex + 1) +
              ' — ' + display.Width + '×' + display.Height) };
        });
        panelScope.captureUi.displayKey = captureDisplayKey(selected);
        panelScope.captureDisplayReady = !body.recording;
        panelScope.captureDisplayStatus = body.recording ?
          'Display selection locked while recording.' :
          'Primary Capture display for Record / Replay / Screenshot. Changes apply after engine rebuild.';
        schedulePanelDigest();
        return true;
      }).catch(function (err) {
        panelScope.captureDisplayReady = false;
        panelScope.captureDisplayChoices = [];
        displayRecords = [];
        panelScope.captureDisplayStatus = 'Capture displays unavailable — ' + err.message;
        schedulePanelDigest();
        return false;
      });
  }

  function createPanelScope() {
    injector = window.angular.element(document.documentElement).injector();
    if (!injector) return null;
    if (panelScope) panelScope.$destroy();
    panelScope = injector.get('$rootScope').$new();
    panelScope.captureUi = {};
    panelScope.captureSettingsReady = false;
    panelScope.captureDisplayReady = false;
    panelScope.captureDisplayChoices = [];
    panelScope.captureDisplayStatus = 'Checking available displays…';
    panelScope.captureEngineChoices = [
      { value: 'FFmpeg', label: 'FFmpeg Capture' },
      { value: 'Duluka', label: 'Duluka Capture' }
    ];
    panelScope.captureApiChoices = apiChoices('FFmpeg');
    // Unknown catalog = no selectable codecs. Never infer installed
    // hardware support from the hardcoded display-name dictionary.
    panelScope.captureEncoderChoices = [];
    panelScope.captureCodecStatus = 'Loading encoder routes from NVIDIA Container…';
    panelScope.capturePresetChoices = [];
    panelScope.captureSelectedLabel = function (value, choices) {
      var selected = (choices || []).filter(function (choice) {
        return String(choice.value) === String(value);
      })[0];
      return selected ? selected.label : '';
    };
    panelScope.onCaptureEngineChange = function () {
      if (!panelScope.captureSettingsReady) return;
      if (panelScope.captureUi.engineMode !== 'FFmpeg' &&
          panelScope.captureUi.engineMode !== 'Duluka') return;
      if (!state) state = defaultSettings();
      state.engineMode = panelScope.captureUi.engineMode;
      state.apiCapture = apiChoices(state.engineMode)[0].value;
      var allowed = allowedEncoders(state.engineMode);
      if (!allowed.length) return;
      if (allowed.indexOf(state.encoder) < 0) {
        // This is an explicit Engine change; choose its supported route.
        // Never silently alter an unsupported SAVED value on page load.
        state.encoder = allowed[0];
        state.encoderPreset = 4;
      }
      syncCaptureControls();
      syncEncoderControls();
      presetOptions();
      save();
    };
    panelScope.onCaptureApiChange = function () {
      if (!panelScope.captureSettingsReady) return;
      if (!state) state = defaultSettings();
      if (!apiChoices(state.engineMode).some(function (choice) {
        return choice.value === panelScope.captureUi.apiCapture;
      })) return;
      state.apiCapture = panelScope.captureUi.apiCapture;
      save();
    };
    panelScope.onEncoderChange = function () {
      if (!panelScope.captureSettingsReady || !state ||
          allowedEncoders(state.engineMode).indexOf(panelScope.captureUi.encoder) < 0) return;
      state.quality = 'Custom';
      state.encoder = panelScope.captureUi.encoder;
      presetOptions();
      state.encoderPreset = Number(panelScope.captureUi.encoderPreset);
      save();
    };
    panelScope.onPresetChange = function () {
      if (!panelScope.captureSettingsReady) return;
      if (!state) state = defaultSettings();
      var preset = Number(panelScope.captureUi.encoderPreset);
      if (!panelScope.capturePresetChoices.some(function (choice) {
        return choice.value === preset;
      })) return;
      state.quality = 'Custom';
      state.encoderPreset = preset;
      save();
    };
    panelScope.onCaptureDisplayChange = function () {
      if (!panelScope.captureDisplayReady) return;
      var desired = displayRecords.filter(function (d) {
        return captureDisplayKey(d) === panelScope.captureUi.displayKey;
      })[0];
      if (!desired) return;
      panelScope.captureDisplayReady = false;
      panelScope.captureDisplayStatus = 'Saving Capture display…';
      schedulePanelDigest();
      return fetchWithTimeout('/DulukaCapture/v.1.0/display/select', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ confirmed: true, outputIndex: desired.OutputIndex,
          adapterLuidLow: desired.AdapterLuidLow,
          adapterLuidHigh: desired.AdapterLuidHigh })
      }, 5500).then(function (response) {
        if (!response.ok) throw new Error('Display selection gateway unavailable');
        return response.json();
      }).then(function (result) {
        if (!result || result.saved !== true || result.effectiveNow !== false) {
          throw new Error('Capture display change was not confirmed');
        }
        return refreshCaptureDisplays().then(function (loaded) {
          if (loaded) {
            panelScope.captureDisplayStatus =
              'Display saved. Restart/rebuild Capture Engine to use it for Record / Replay / Screenshot.';
            schedulePanelDigest();
          }
        });
      }).catch(function (error) {
        return refreshCaptureDisplays().then(function () {
          panelScope.captureDisplayStatus = 'Display not saved: ' + error.message;
          schedulePanelDigest();
        });
      });
    };
    panelScope.onCursorChange = function () {
      if (!panelScope.captureSettingsReady) return;
      if (!state) state = defaultSettings();
      state.captureCursor = Boolean(panelScope.captureUi.captureCursor);
      save();
    };
    return panelScope;
  }

  function schedulePanelDigest() {
    if (panelScope) panelScope.$applyAsync();
  }

  function makeSelect(id, label, model, choices, changeHandler) {
    var wrap = document.createElement('md-input-container');
    wrap.className = 'fps flex resolution-fps nv-video-engine-field';
    var heading = document.createElement('div');
    heading.className = 'customize-header-text';
    heading.setAttribute('layout', 'row');
    var caption = document.createElement('span');
    caption.textContent = label + ':';
    heading.appendChild(caption);
    var select = document.createElement('md-select');
    select.setAttribute('ng-model', model);
    select.setAttribute('ng-change', changeHandler);
    select.setAttribute('ng-disabled', '!captureSettingsReady');
    select.setAttribute('aria-label', label);
    select.setAttribute('md-selected-text', 'captureSelectedLabel(' + model + ', ' + choices + ')');
    select.className = 'osc-select resolution-fps-select nv-video-engine-select';
    select.id = id;
    select.setAttribute('hover-focus', '');
    var repeat = document.createElement('md-option');
    repeat.setAttribute('ng-repeat', 'choice in ' + choices + ' track by choice.value');
    repeat.setAttribute('ng-value', 'choice.value');
    repeat.setAttribute('ng-disabled', 'choice.disabled');
    repeat.setAttribute('hover-focus', '');
    repeat.textContent = '{{choice.label}}';
    select.appendChild(repeat);
    wrap.appendChild(heading);
    wrap.appendChild(select);
    return { wrap: wrap, select: select };
  }

  function makeSection(title) {
    var section = document.createElement('section');
    section.className = 'nv-video-engine-section';
    var heading = document.createElement('h2');
    heading.className = 'nv-video-engine-section-heading';
    heading.textContent = title;
    section.appendChild(heading);
    return section;
  }

  function save() {
    // Never POST guessed settings or a codec not listed by the active
    // Container route (e.g. Intel/AMD entries on a Duluka-only machine).
    if (!panelScope || !panelScope.captureSettingsReady || !state) {
      return Promise.resolve(false);
    }
    if (allowedEncoders(state.engineMode).indexOf(state.encoder) < 0) {
      statusNode.classList.add('highlights-item-disabled');
      statusNode.textContent = 'Choose a codec available for this Capture engine before saving.';
      return Promise.resolve(false);
    }
    statusNode.textContent = 'Saving…';
    return fetchWithTimeout(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(state)
    }).then(function (response) {
      if (!response.ok) {
        return response.text().then(function (body) {
          var error = null;
          try {
            error = body ? JSON.parse(body) : null;
          } catch (parseError) {
            error = null;
          }
          throw new Error((error && error.message) ||
            'Unable to save capture settings (' + response.status + ')');
        });
      }
      return response.json();
    }).then(function (saved) {
      if (!usableCaptureSettings(saved)) throw new Error('Invalid capture settings response');
      state = saved;
      statusNode.classList.remove('highlights-item-disabled');
      statusNode.textContent = 'Saved';
      render();
    }).catch(function (error) {
      statusNode.textContent = error.message;
      statusNode.classList.add('highlights-item-disabled');
    });
  }

  function defaultSettings() {
    return {
      engineMode: 'FFmpeg',
      apiCapture: 'ddagrab',
      quality: 'Custom',
      encoder: 'NVENC_H264',
      encoderPreset: 4,
      captureCursor: false,
      fps: 60,
      bitrateKbps: 50000,
      useNativeResolution: true,
      width: 1920,
      height: 1080
    };
  }

  function usableCaptureSettings(value) {
    return !!value && typeof value === 'object' &&
      (value.engineMode === 'FFmpeg' || value.engineMode === 'Duluka') &&
      apiChoices(value.engineMode).some(function (choice) {
        return !choice.disabled && choice.value === value.apiCapture;
      }) &&
      encoderChoices.some(function (choice) { return choice[0] === value.encoder; }) &&
      Number.isInteger(value.encoderPreset) && value.encoderPreset >= 1 &&
      value.encoderPreset <= 7 && typeof value.captureCursor === 'boolean' &&
      Number.isFinite(value.fps) && value.fps > 0 &&
      Number.isFinite(value.bitrateKbps) && value.bitrateKbps > 0 &&
      typeof value.useNativeResolution === 'boolean' &&
      Number.isFinite(value.width) && value.width > 0 &&
      Number.isFinite(value.height) && value.height > 0;
  }

  function setUnavailableSettings(error) {
    // Keep controls locked on loading failure; defaults are only a visual
    // placeholder, never a confirmed state that may be persisted.
    if (!state) state = defaultSettings();
    if (panelScope) panelScope.captureSettingsReady = false;
    render();
    statusNode.classList.add('highlights-item-disabled');
    statusNode.textContent = 'Capture settings unavailable. Select Reload to retry (' +
      (error && error.message ? error.message : 'unknown error') + ')';
  }

  function apiChoices(engine) {
    return engine === 'Duluka'
      ? [
        { value: 'dxgi_desktop_duplication', label: 'DXGI Desktop Duplication', disabled: false },
        { value: 'windows_graphics_capture', label: 'Windows Graphics Capture', disabled: true },
        { value: 'd3d11_native', label: 'Direct3D 11 Native Capture', disabled: true },
        { value: 'window_capture', label: 'Window Capture', disabled: true },
        { value: 'region_capture', label: 'Region Capture', disabled: true },
        { value: 'native_game_capture', label: 'Native Game Capture', disabled: true }
      ]
      : [
        { value: 'ddagrab', label: 'ddagrab — Direct3D 11 Desktop Capture', disabled: false },
        { value: 'gdigrab', label: 'gdigrab — GDI Desktop Capture', disabled: false },
        { value: 'gfxcapture', label: 'gfxcapture — Graphics Capture', disabled: false }
      ];
  }

  function syncCaptureControls() {
    if (!state || !panelScope) return;
    var choices = apiChoices(state.engineMode);
    if (!choices.some(function (item) { return item.value === state.apiCapture; })) {
      state.apiCapture = choices[0].value;
    }
    panelScope.captureUi.engineMode = state.engineMode;
    panelScope.captureApiChoices = choices;
    panelScope.captureUi.apiCapture = state.apiCapture;
    schedulePanelDigest();
  }

  function destroyCaptureControls() {
    captureFields.forEach(function (field) {
      if (field.parentNode) field.parentNode.removeChild(field);
    });
    captureFields = [];
    if (captureFieldsRow) {
      captureFieldsRow.classList.remove('nv-video-engine-fields-row');
    }
    captureFieldsRow = null;
    Array.prototype.forEach.call(document.querySelectorAll(
      '#nv-video-engine-mode-field, #nv-video-engine-api-field'
    ), function (field) {
      if (field.parentNode) field.parentNode.removeChild(field);
    });
  }

  function hasSingleCaptureControlPair(row) {
    return row.classList.contains('nv-video-engine-fields-row') &&
      row.querySelectorAll('#nv-video-engine-mode, #nv-video-engine-api').length === 2;
  }

  function unwrapVideoPanels() {
    var stack = document.getElementById(panelStackId);
    if (!stack || !stack.parentNode) return;
    var mainPanel = stack.querySelector('#topLevel')
      ? stack.querySelector('#topLevel').parentNode : null;
    if (mainPanel && mainPanel !== stack) {
      mainPanel.classList.remove('nv-video-capture-panel');
      clearPreviousPanelSizing(mainPanel);
      stack.parentNode.insertBefore(mainPanel, stack);
    }
    stack.parentNode.removeChild(stack);
  }

  function buildCaptureControls(row) {
    destroyCaptureControls();
    row.classList.add('nv-video-engine-fields-row');
    captureFieldsRow = row;
    Array.prototype.forEach.call(row.children, function (field) {
      if (!field.classList.contains('resolution') && !field.classList.contains('fps')) return;
      field.style.flex = '1 1 calc(24% - 12px)';
      field.style.minWidth = '190px';
      field.style.maxWidth = 'none';
      field.style.margin = '10px 6px';
      field.style.boxSizing = 'border-box';
    });
    [
      {
        id: 'nv-video-engine-mode',
        label: 'Capture engine',
        model: 'captureUi.engineMode',
        choices: 'captureEngineChoices',
        changeHandler: 'onCaptureEngineChange()'
      },
      {
        id: 'nv-video-engine-api',
        label: 'Capture API',
        model: 'captureUi.apiCapture',
        choices: 'captureApiChoices',
        changeHandler: 'onCaptureApiChange()'
      }
    ].forEach(function (field) {
      var fieldControl = makeSelect(
        field.id, field.label, field.model, field.choices, field.changeHandler
      );
      fieldControl.wrap.id = field.id + '-field';
      fieldControl.wrap.style.flex = field.id === 'nv-video-engine-api'
        ? '1.15 1 calc(28% - 12px)'
        : '1 1 calc(24% - 12px)';
      fieldControl.wrap.style.minWidth = '190px';
      fieldControl.wrap.style.maxWidth = 'none';
      fieldControl.wrap.style.margin = '10px 6px';
      fieldControl.wrap.style.boxSizing = 'border-box';
      row.appendChild(fieldControl.wrap);
      captureFields.push(fieldControl.wrap);
    });
    return true;
  }

  function presetOptions() {
    if (!state || !panelScope) return;
    var choices = presetChoices[state.encoder] || ['medium'];
    panelScope.capturePresetChoices = choices.map(function (choice, index) {
      var value = typeof choice === 'string' ? index + 1 : choice.index;
      var label = typeof choice === 'string' ? choice : choice.label;
      return { value: value, label: label };
    });
    var selected = choices.some(function (choice, index) {
      return (typeof choice === 'string' ? index + 1 : choice.index) === state.encoderPreset;
    });
    if (!selected) state.encoderPreset = typeof choices[0] === 'string' ? 1 : choices[0].index;
    panelScope.captureUi.encoderPreset = state.encoderPreset;
    schedulePanelDigest();
  }

  function render() {
    if (!state || !panelScope) return;
    syncCaptureControls();
    syncEncoderControls();
    panelScope.captureUi.captureCursor = Boolean(state.captureCursor);
    presetOptions();
    var selectedPreset = panelScope.capturePresetChoices.filter(function (choice) {
      return choice.value === Number(state.encoderPreset);
    })[0];
    var presetLabel = selectedPreset ? selectedPreset.label : '';
    var captureApi = state.engineMode === 'Duluka'
      ? 'DXGI Desktop Duplication (runtime: ddagrab)'
      : state.apiCapture === 'ddagrab' ? 'ddagrab (built-in)' : state.apiCapture;
    if (Number.isFinite(state.fps) && Number.isFinite(state.bitrateKbps) &&
        typeof state.useNativeResolution === 'boolean' &&
        Number.isFinite(state.width) && Number.isFinite(state.height)) {
      var resolution = state.useNativeResolution ? 'native' : state.width + 'x' + state.height;
      previewNode.textContent = 'Requested (config.json): ' + state.encoder + ' · ' +
        state.fps + ' fps (live per record) · ' + state.bitrateKbps +
        ' kbps (engine restart) · ' + resolution + ' (engine restart) · preset ' +
        presetLabel + ' (engine restart) · Capture API: ' + captureApi;
    } else {
      previewNode.textContent = 'Engine: ' + state.engineMode + ' · ' + captureApi +
        ' · ' + encoderNames[state.encoder] + ' · ' + presetLabel;
    }
    schedulePanelDigest();
  }

  function refreshCaptureRuntimeStatus() {
    if (!runtimeStatusNode || !videoPage()) return;
    return fetchWithTimeout('/DulukaCapture/v.1.0/status', {}, 2500).then(function (response) {
      return response.json().then(function (body) {
        return { statusCode: response.status, body: body };
      });
    }).then(function (result) {
      var body = result.body || {};
      if (result.statusCode !== 200 || typeof body.enabled !== 'boolean' ||
          typeof body.ready !== 'boolean' || typeof body.recording !== 'boolean') {
        throw new Error('Capture runtime status invalid or unavailable');
      }
      runtimeStatusNode.classList.remove('nv-video-engine-error');
      if (!body.enabled) runtimeStatusNode.textContent = 'Capture runtime: disabled';
      else if (body.ready) runtimeStatusNode.textContent = 'Capture runtime: ready · ' +
        (body.recording ? 'Recording' : 'Idle');
      else {
        runtimeStatusNode.textContent = 'Capture runtime: starting or unavailable';
        runtimeStatusNode.classList.add('nv-video-engine-error');
      }
    }).catch(function () {
      runtimeStatusNode.textContent = 'Capture runtime: unavailable';
      runtimeStatusNode.classList.add('nv-video-engine-error');
    });
  }

  function buildPanel(host) {
    var panel = document.createElement('section');
    panel.id = panelId;
    panel.className = 'nv-video-capture-panel';

    var encoder = makeSelect(
      'nv-video-engine-encoder',
      'Codec Encoder',
      'captureUi.encoder',
      'captureEncoderChoices',
      'onEncoderChange()'
    );
    encoderChoices.forEach(function (choice) {
      encoderNames[choice[0]] = choice[1];
    });
    var preset = makeSelect(
      'nv-video-engine-preset',
      'Encoder preset',
      'captureUi.encoderPreset',
      'capturePresetChoices',
      'onPresetChange()'
    );

    var displaySection = makeSection('Display Capture:');
    var displaySelect = makeSelect(
      'nv-video-capture-display', 'Primary Capture display',
      'captureUi.displayKey', 'captureDisplayChoices', 'onCaptureDisplayChange()'
    );
    displaySelect.select.setAttribute('ng-disabled', '!captureDisplayReady');
    displaySection.appendChild(displaySelect.wrap);
    displayStatusNode = document.createElement('p');
    displayStatusNode.className = 'nv-video-engine-codec-note';
    displayStatusNode.setAttribute('role', 'status');
    displayStatusNode.textContent = '{{captureDisplayStatus}}';
    displaySection.appendChild(displayStatusNode);

    var advancedSection = makeSection('Advanced:');
    var advanced = document.createElement('div');
    advanced.className = 'nv-video-engine-advanced';
    var preview = document.createElement('div');
    preview.className = 'nv-video-engine-preview';
    var previewTitle = document.createElement('span');
    previewTitle.className = 'nv-video-engine-preview-title';
    previewTitle.textContent = 'Preview Build Arguments';
    previewNode = document.createElement('code');
    previewNode.className = 'nv-video-engine-preview-value';
    preview.appendChild(previewTitle);
    preview.appendChild(previewNode);
    var actions = document.createElement('div');
    actions.className = 'nv-video-engine-actions';
    var reloadButton = document.createElement('md-button');
    reloadButton.setAttribute('type', 'button');
    reloadButton.className = 'md-raised md-primary nv-video-engine-action';
    reloadButton.textContent = 'Reload';
    reloadButton.addEventListener('click', function () {
      // Reload both persisted settings and the read-only Container catalog.
      panelScope.captureSettingsReady = false;
      schedulePanelDigest();
      statusNode.textContent = 'Loading…';
      refreshCaptureDisplays();
      readCaptureSettingsAndCatalog().then(function () {
        statusNode.classList.remove('highlights-item-disabled');
        statusNode.textContent = 'Ready';
      }).catch(setUnavailableSettings);
    });
    var copyButton = document.createElement('md-button');
    copyButton.setAttribute('type', 'button');
    copyButton.className = 'md-raised md-primary nv-video-engine-action';
    copyButton.textContent = 'Copy';
    copyButton.addEventListener('click', function () {
      if (!navigator.clipboard || !navigator.clipboard.writeText) {
        statusNode.textContent = 'Clipboard access is unavailable';
        statusNode.classList.add('highlights-item-disabled');
        return;
      }
      navigator.clipboard.writeText(previewNode.textContent).then(function () {
        statusNode.classList.remove('highlights-item-disabled');
        statusNode.textContent = 'Copied';
      }).catch(function () {
        statusNode.textContent = 'Clipboard access is unavailable';
        statusNode.classList.add('highlights-item-disabled');
      });
    });
    actions.appendChild(reloadButton);
    actions.appendChild(copyButton);
    var previewPanel = document.createElement('div');
    previewPanel.className = 'nv-video-engine-preview-panel';
    previewPanel.appendChild(preview);
    previewPanel.appendChild(actions);
    var encoderFields = document.createElement('div');
    encoderFields.className = 'nv-video-engine-advanced-fields';
    encoderFields.appendChild(encoder.wrap);
    var codecStatus = document.createElement('p');
    codecStatus.className = 'nv-video-engine-codec-note';
    codecStatus.setAttribute('role', 'status');
    codecStatus.textContent = '{{captureCodecStatus}}';
    // Helper text MUST be outside the md-select/encoder field stack.
    // The earlier placement with negative margin overlaid the green
    // Material select border and Encoder preset label in CEF.
    encoderFields.appendChild(preset.wrap);
    advanced.appendChild(previewPanel);
    advanced.appendChild(encoderFields);
    advancedSection.appendChild(advanced);
    advancedSection.appendChild(codecStatus);
    panel.appendChild(displaySection);
    panel.appendChild(advancedSection);

    var footer = document.createElement('div');
    footer.className = 'nv-video-engine-row nv-video-engine-footer';
    var cursorSwitch = document.createElement('md-switch');
    cursorSwitch.className = 'nv-video-engine-cursor-label';
    cursorSwitch.setAttribute('ng-model', 'captureUi.captureCursor');
    cursorSwitch.setAttribute('ng-change', 'onCursorChange()');
    cursorSwitch.setAttribute('ng-disabled', '!captureSettingsReady');
    cursorSwitch.textContent = 'Capture cursor';
    statusNode = document.createElement('span');
    statusNode.className = 'nv-video-engine-status';
    runtimeStatusNode = document.createElement('span');
    runtimeStatusNode.className = 'nv-video-engine-runtime-status';
    runtimeStatusNode.textContent = 'Capture runtime: checking…';
    footer.appendChild(cursorSwitch);
    footer.appendChild(runtimeStatusNode);
    footer.appendChild(statusNode);
    var content = document.createElement('div');
    content.className = 'nv-video-capture-options-content';
    while (panel.firstChild) content.appendChild(panel.firstChild);
    content.appendChild(footer);
    panel.appendChild(content);
    host.appendChild(panel);

    var compiler = injector.get('$compile');
    captureFields.forEach(function (field) { compiler(field)(panelScope); });
    compiler(panel)(panelScope);
  }

  function initialize() {
    clearLegacyPageLayout();
    if (!videoPage()) return;
    var host = document.querySelector('nv-preferences-video #topLevel');
    if (!host) return;
    if (!window.angular) return;
    var resolutionRow = host.querySelector('.resolution-fps.layout-row');
    if (!resolutionRow) return;
    var existing = document.getElementById(panelId);
    if (existing && existing.getAttribute('data-nv-ui-version') === panelVersion &&
        hasSingleCaptureControlPair(resolutionRow)) return;
    if (!createPanelScope()) return;
    destroyCaptureControls();
    unwrapVideoPanels();
    existing = document.getElementById(panelId);
    if (existing) existing.parentNode.removeChild(existing);
    if (!buildCaptureControls(resolutionRow)) return;
    buildPanel(host);
    document.getElementById(panelId).setAttribute('data-nv-ui-version', panelVersion);
    if (runtimeStatusTimer) window.clearInterval(runtimeStatusTimer);
    refreshCaptureRuntimeStatus();
    refreshCaptureDisplays();
    runtimeStatusTimer = window.setInterval(refreshCaptureRuntimeStatus, 5000);
    statusNode.textContent = 'Loading…';
    readCaptureSettingsAndCatalog().then(function () {
      statusNode.classList.remove('highlights-item-disabled');
      statusNode.textContent = 'Ready';
    }).catch(setUnavailableSettings);
  }

  var attempts = 0;
  var timer = window.setInterval(function () {
    initialize();
    attempts += 1;
    if (document.getElementById(panelId) || attempts >= 120) window.clearInterval(timer);
  }, 500);
  window.addEventListener('hashchange', function () {
    clearLegacyPageLayout();
    if (videoPage()) initialize();
    else {
      destroyCaptureControls();
      if (panelScope) {
        panelScope.$destroy();
        panelScope = null;
      }
      var panel = document.getElementById(panelId);
      if (panel) panel.parentNode.removeChild(panel);
      unwrapVideoPanels();
    }
  });
})();
