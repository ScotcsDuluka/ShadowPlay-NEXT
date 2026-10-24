/* NVIDIA ShadowPlay Launcher — page logic.
   RPC: window.cefQuery (the osc page contract) carrying JSON requests,
   LAUNCHER_* command namespace. State is pushed by the native supervisor
   on change; one initial pull seeds the page after load.

   NULL-SAFE BY CONTRACT: the HTML is owner-editable (elements may be
   removed/renamed freely). Every lookup and every handler attach is
   guarded — a missing element must degrade that one feature, never kill
   the script (a single throw at load detaches EVERY button). */
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }
  var i18n = window.LauncherI18n;
  function tr(key) { return i18n.t(key); }
  function setText(node, key) {
    if (!node) return;
    node.setAttribute('data-i18n', key);
    node.textContent = tr(key);
  }
  i18n.apply();

  // Chromium 73 lacks :focus-visible. Only show the outline when the
  // navigation method is keyboard, not after every mouse click.
  if (document.addEventListener && document.body && document.body.classList) {
    document.addEventListener('keydown', function (event) {
      var k = event.key;
      if (k === 'Tab' || k === 'Enter' || k === ' ' || k === 'Spacebar' ||
          k === 'ArrowUp' || k === 'ArrowDown' ||
          k === 'ArrowLeft' || k === 'ArrowRight') {
        document.body.classList.add('keyboard-nav');
      }
    }, true);
    document.addEventListener('mousedown', function () {
      document.body.classList.remove('keyboard-nav');
    }, true);
    document.addEventListener('touchstart', function () {
      document.body.classList.remove('keyboard-nav');
    }, true);
  }

  // ── RPC bridge ──────────────────────────────────────────────────────
  var bridgeUp = typeof window.cefQuery === 'function';
  var bridgeTag = $('bridgeState');

  function rpc(command, extra) {
    return new Promise(function (resolve, reject) {
      if (!bridgeUp) { reject(new Error('no bridge')); return; }
      var req = { command: command };
      var settled = false;
      var queryId = null;
      // OPEN OVERLAY may legitimately wait for NvContainer-owned services to
      // start (two bounded 15s readiness windows); all other RPCs should be
      // quick and must not leave controls waiting forever.
      var timeoutMs = command === 'LAUNCHER_OPEN_OVERLAY' ? 35000 : 8000;
      var timer = setTimeout(function () {
        if (settled) return;
        settled = true;
        if (queryId !== null && typeof window.cefQueryCancel === 'function') {
          try { window.cefQueryCancel(queryId); } catch (e) {}
        }
        reject(new Error(command + ' timed out after ' + timeoutMs + 'ms'));
      }, timeoutMs);

      if (extra) {
        for (var k in extra) { if (extra.hasOwnProperty(k)) req[k] = extra[k]; }
      }
      try {
        queryId = window.cefQuery({
          request: JSON.stringify(req),
          persistent: false,
          onSuccess: function (response) {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            try { resolve(JSON.parse(response)); } catch (e) { resolve(response); }
          },
          onFailure: function (code, msg) {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            reject(new Error(code + ': ' + msg));
          }
        });
      } catch (error) {
        settled = true;
        clearTimeout(timer);
        reject(error);
      }
    });
  }
  if (bridgeTag) {
    if (!bridgeUp) {
      setText(bridgeTag, 'bridgeOffline');
      bridgeTag.className = 'bridge off';
    } else {
      setText(bridgeTag, 'bridgeLive');
    }
  }

  // ── Elements (any may be absent) ────────────────────────────────────
  var el = {
    dotOverlay: $('dotOverlay'),
    dotNotifier: $('dotNotifier'),
    dotNvApi: $('dotNvApi'),
    dotContainer: $('dotContainer'),
    stateOverlay: $('stateOverlay'),
    stateNotifier: $('stateNotifier'),
    stateNvApi: $('stateNvApi'),
    stateContainer: $('stateContainer'),
    chipEngine: $('chipEngine'),
    chipCef: $('chipCef'),
    chipHub: $('chipHub'),
    actionStatus: $('actionStatus'),
    tglOverlay: $('tglOverlay'),
    tglEngine: $('tglEngine'),
    lblWinform: $('lblWinform'),
    lblCef: $('lblCef'),
    modeDesc: $('modeDesc'),
    btnOpenOverlay: $('btnOpenOverlay'),
    btnObt3: $('btnObt3'),
    btnExit: $('btnExit'),
    btnMin: $('btnMin'),
    btnClose: $('btnClose')
  };

  // Guarded event attach: a missing element skips silently.
  function on(node, ev, fn) {
    if (node) node.addEventListener(ev, fn);
  }

  on($('launcherLanguage'), 'change', function () {
    publishLanguage(i18n.setLanguage(this.value));
  });

  function activateByKeyboard(node, fn) {
    on(node, 'keydown', function (event) {
      if (event.key !== 'Enter' && event.key !== ' ' && event.key !== 'Spacebar') return;
      event.preventDefault();
      fn.call(node, event);
    });
  }

  var statusTimer = null;
  function announce(message, failed) {
    if (!el.actionStatus) return;
    if (message) setText(el.actionStatus, message);
    else { el.actionStatus.removeAttribute('data-i18n'); el.actionStatus.textContent = ''; }
    el.actionStatus.className = 'action-status' + (failed ? ' error' : '');
    if (statusTimer) clearTimeout(statusTimer);
    if (message && !failed) {
      statusTimer = setTimeout(function () {
        el.actionStatus.removeAttribute('data-i18n');
        el.actionStatus.textContent = '';
        el.actionStatus.className = 'action-status';
      }, 2500);
    }
  }

  function reportFailure(label, error) {
    if (window.console && console.error) console.error(label, error);
    announce(label, true);
  }

  function setDot(dot, stateEl, cls, label) {
    if (!dot) return;
    dot.className = 'dot' + (cls ? ' ' + cls : '');
    if (stateEl) setText(stateEl, label);
  }

  // Product modules are reported by the host. DLLs/configuration can be
  // installed without being a standalone process or an active game session.
  var moduleIds = ['Capture', 'Graphics', 'Overlay', 'NodeApi', 'Container',
    'Adapter', 'Hook', 'Ansel', 'Helper', 'Ffmpeg', 'Config'];
  function renderModules(modules) {
    for (var mi = 0; mi < moduleIds.length; mi++) {
      var id = moduleIds[mi];
      var info = modules && modules[id];
      var key = 'moduleWaiting';
      var stateClass = 'unknown';
      if (info && typeof info.installed === 'boolean') {
        if (!info.installed) {
          key = 'moduleMissing';
          stateClass = 'missing';
        } else if (info.processTracking === false) {
          key = 'moduleInstalled';
          stateClass = 'installed';
        } else if (info.processTracking === true && typeof info.running === 'boolean') {
          key = info.running ? 'running' : 'stopped';
          stateClass = info.running ? 'running' : 'stopped';
        }
      }
      var stateNode = $('moduleState' + id);
      if (stateNode) {
        setText(stateNode, key);
        stateNode.className = 'module-state ' + stateClass;
      }
      var availability = $('moduleAvailability' + id);
      if (availability) setText(availability,
        info && typeof info.installed === 'boolean'
          ? (info.installed ? 'moduleInstalled' : 'moduleMissing') : 'moduleWaiting');
      var navState = $('navState' + id);
      if (navState) {
        navState.className = 'nav-module-state ' + stateClass;
        navState.setAttribute('title', tr(key));
      }
    }
  }

  // ── Render ──────────────────────────────────────────────────────────
  var latestState = null;
  function render(s) {
    if (!s) return;
    latestState = s;
    window.__quickState = s;
    if (typeof window.onQuickLauncherState === 'function') window.onQuickLauncherState(s);
    renderModules(s.modules);

    // OVERLAY API: running+ready -> RUNNING, running only -> LOADING,
    // else STOPPED (Main.vb IF_APP_Tick contract).
    if (s.overlayApi && s.overlayApi.running && s.overlayApi.ready) {
      setDot(el.dotOverlay, el.stateOverlay, 'running', 'running');
    } else if (s.overlayApi && s.overlayApi.running) {
      setDot(el.dotOverlay, el.stateOverlay, 'loading', 'loading');
    } else {
      setDot(el.dotOverlay, el.stateOverlay, '', 'stopped');
    }

    setDot(el.dotNotifier, el.stateNotifier,
      (s.notifierApi && s.notifierApi.running) ? 'running' : '',
      (s.notifierApi && s.notifierApi.running) ? 'running' : 'stopped');
    setDot(el.dotNvApi, el.stateNvApi,
      (s.nvApi && s.nvApi.running) ? 'running' : '',
      (s.nvApi && s.nvApi.running) ? 'running' : 'stopped');
    setDot(el.dotContainer, el.stateContainer,
      (s.lanes && s.lanes.container) ? 'running' : '',
      (s.lanes && s.lanes.container) ? 'running' : 'stopped');

    var lanes = s.lanes || {};
    if (el.chipEngine) el.chipEngine.className = 'lane-chip' + (lanes.container ? ' on' : '');
    if (el.chipCef) el.chipCef.className = 'lane-chip' + (lanes.cefOverlay ? ' on' : '');
    if (el.chipHub) el.chipHub.className = 'lane-chip' + ((s.nvApi && s.nvApi.running) ? ' on' : '');
    // Dashboard health: connected launcher lanes only. Do not claim that
    // recording, game injection or GPU routing are ready based on these dots.
    var controllerOn = !!lanes.container;
    var presenterOn = !!lanes.cefOverlay;
    var apiOn = !!(s.nvApi && s.nvApi.running);
    var healthOk = !!(s.overlayApi && s.overlayApi.running && s.overlayApi.ready);
    var sysKey = healthOk ? 'systemOnline' : 'systemPartial';
    var labels = ['headerHealth', 'sidebarHealth', 'systemHealth'];
    for (var ix = 0; ix < labels.length; ix++) {
      var stateNode = $(labels[ix]);
      if (stateNode) {
        setText(stateNode, sysKey);
        stateNode.classList.toggle('on', healthOk);
        stateNode.classList.toggle('off', !healthOk);
      }
    }
    var led = $('sidebarLed');
    if (led) {
      led.classList.toggle('on', healthOk);
      led.classList.toggle('off', !healthOk);
    }
    var indicators = [
      ['signalController', controllerOn, el.chipEngine],
      ['signalPresenter', presenterOn, el.chipCef],
      ['signalApi', apiOn, el.chipHub]
    ];
    for (var si = 0; si < indicators.length; si++) {
      var icon = $(indicators[si][0]);
      if (icon) icon.className = 'signal-icon' + (indicators[si][1] ? ' on' : '');
      if (indicators[si][2]) setText(indicators[si][2], indicators[si][1] ? 'running' : 'stopped');
    }
    var engineText = $('overviewEngine');
    if (engineText) engineText.textContent = tr(s.engineOverlay ? 'selectedCef' : 'selectedWinform');
    var checkedAt = $('signalTimestamp');
    if (checkedAt) {
      checkedAt.removeAttribute('data-i18n');
      checkedAt.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    syncToggle(el.tglOverlay, s.overlayEnabled);
    syncMode(!!s.engineOverlay);
  }

  function syncToggle(t, on) {
    if (!t || typeof on !== 'boolean') return;
    if (t.dataset.userHold === '1') {
      // User just interacted; the authoritative 1s push re-syncs later.
      if (t.dataset.pending === String(on)) t.dataset.userHold = '';
      return;
    }
    t.classList.toggle('on', on);
    t.setAttribute('aria-checked', on ? 'true' : 'false');
    t.dataset.pending = String(on);
  }

  // Overlay Mode switch: false = WINFORM lane, true = CEF lane.
  function syncMode(cef) {
    if (!el.tglEngine) return;
    if (el.tglEngine.dataset.userHold === '1') {
      if (el.tglEngine.dataset.pending === String(cef)) el.tglEngine.dataset.userHold = '';
      return;
    }
    applyMode(cef);
    el.tglEngine.dataset.pending = String(cef);
  }

  function applyMode(cef) {
    el.tglEngine.classList.toggle('cef', cef);
    el.tglEngine.setAttribute('aria-checked', cef ? 'true' : 'false');
    if (el.lblWinform) el.lblWinform.classList.toggle('active', !cef);
    if (el.lblCef) el.lblCef.classList.toggle('active', cef);
    // modeDesc is owner-authored static copy ("Two Engine Regimes") —
    // the active label highlight above is the state indicator.
  }

  window.onLauncherLanguageChanged = function () {
    if (latestState) render(latestState);
  };

  function publishLanguage(lang) {
    rpc('LAUNCHER_SET_LANGUAGE', { language: lang }).then(function (result) {
      if (!result || result.ok !== true) throw new Error('language was not synchronized');
    }).catch(function (error) {
      if (window.console && console.warn)
        console.warn('[launcher-language] Could not synchronize OSC language', error);
    });
  }

  // ── Host push (launcher_script.h augmentation) ──────────────────────
  window.__onLauncherState(function (s) { render(s); });

  // Pull fallback: never let the page sit on stale state.
  function pull() {
    rpc('LAUNCHER_GET_STATE').then(render).catch(function (error) {
      if (bridgeUp) reportFailure('stateSyncFailed', error);
    });
  }
  pull();
  publishLanguage(i18n.getLanguage());

  // ── Toggles (user action only — config.json is the source of truth) ─
  on(el.tglOverlay, 'click', function () {
    var on = !this.classList.contains('on');
    this.dataset.userHold = '1';
    this.dataset.pending = String(on);
    this.classList.toggle('on', on);
    this.setAttribute('aria-checked', on ? 'true' : 'false');
    rpc('LAUNCHER_SET_OVERLAY', { value: on }).then(function (result) {
      if (!result || result.ok === false) throw new Error('setting was not saved');
      announce('settingSaved', false);
    }).catch(function (error) {
      el.tglOverlay.dataset.userHold = '';
      reportFailure('saveFailed', error);
      pull();
    });
  });
  activateByKeyboard(el.tglOverlay, function () {
    this.click();
  });

  // Overlay Mode: click the switch or either label.
  function setMode(cef) {
    if (!el.tglEngine) return;
    el.tglEngine.dataset.userHold = '1';
    el.tglEngine.dataset.pending = String(cef);
    applyMode(cef);
    rpc('LAUNCHER_SET_ENGINE_OVERLAY', { value: cef }).then(function (result) {
      if (!result || result.ok === false) throw new Error('mode was not saved');
      announce(cef ? 'cefStarting' : 'winformSelected', false);
    }).catch(function (error) {
      el.tglEngine.dataset.userHold = '';
      reportFailure('changeEngineFailed', error);
      pull();
    });
  }
  on(el.tglEngine, 'click', function () {
    setMode(!this.classList.contains('cef'));
  });
  on(el.lblWinform, 'click', function () {
    if (!el.tglEngine.classList.contains('cef')) return;  // already WINFORM
    setMode(false);
  });
  activateByKeyboard(el.lblWinform, function () { this.click(); });
  on(el.lblCef, 'click', function () {
    if (el.tglEngine.classList.contains('cef')) return;  // already CEF
    setMode(true);
  });
  activateByKeyboard(el.lblCef, function () { this.click(); });
  activateByKeyboard(el.tglEngine, function () { this.click(); });

  // Command Center sidebar: local view changes only, never service RPC.
  // Capture/Graphics are informational until native route contracts exist.
  var viewIds = ['Overview', 'Capture', 'Graphics', 'Overlay', 'NodeApi',
    'Container', 'Adapter', 'Hook', 'Ansel', 'Helper', 'Ffmpeg', 'Services', 'Settings'];
  function showView(name) {
    for (var i = 0; i < viewIds.length; i++) {
      var active = viewIds[i] === name;
      var panel = $('view' + viewIds[i]);
      var button = $('nav' + viewIds[i]);
      if (panel) panel.classList.toggle('active', active);
      if (button) {
        button.classList.toggle('active', active);
        if (active) button.setAttribute('aria-current', 'page');
        else button.removeAttribute('aria-current');
      }
    }
    var content = $('contentScroll');
    if (content) content.scrollTop = 0;
  }
  for (var vi = 0; vi < viewIds.length; vi++) {
    (function (name) {
      on($('nav' + name), 'click', function () { showView(name); });
    })(viewIds[vi]);
  }
  on($('btnJumpServices'), 'click', function () { showView('Services'); });
  on($('btnGotoCapture'), 'click', function () { showView('Capture'); });
  on($('btnGotoGraphics'), 'click', function () { showView('Graphics'); });
  on($('btnGotoSettings'), 'click', function () { showView('Settings'); });
  on($('btnCaptureOverlay'), 'click', function () {
    // Exactly the same CEF RPC path as the Overview OPEN OVERLAY button.
    if (el.btnOpenOverlay && typeof el.btnOpenOverlay.click === 'function')
      el.btnOpenOverlay.click();
  });
  on($('btnModuleOverlay'), 'click', function () {
    if (el.btnOpenOverlay && typeof el.btnOpenOverlay.click === 'function')
      el.btnOpenOverlay.click();
  });

  // ── Buttons ─────────────────────────────────────────────────────────
  // OPEN OVERLAY feedback: never read back the mutated label (a fast
  // double-click used to capture "SENT" as the "original" text and get
  // stuck there) — restore the fixed label, and ignore clicks while the
  // feedback cycle is running.
  var overlayBusy = false;
  on(el.btnOpenOverlay, 'click', function () {
    var b = this;
    if (overlayBusy) return;
    overlayBusy = true;
    setText(b, 'sending');
    rpc('LAUNCHER_OPEN_OVERLAY').then(function (r) {
      setText(b, (r && r.ok) ? 'sent' : 'hubOffline');
      announce((r && r.ok) ? 'overlayRequestSent' : 'overlayServiceOffline',
        !(r && r.ok));
      setTimeout(function () {
        setText(b, 'openOverlay');
        overlayBusy = false;
      }, 1200);
    }).catch(function (error) {
      setText(b, 'openOverlay');
      overlayBusy = false;
      reportFailure('overlayRequestFailed', error);
    });
  });

  on(el.btnObt3, 'click', function () {
    rpc('LAUNCHER_OPEN_OBT3').then(function (result) {
      if (!result || result.ok === false) throw new Error('browser could not be opened');
    }).catch(function (error) {
      reportFailure('openObt3Failed', error);
    });
  });

  // Account credentials are entered ONLY in the external HTTPS browser.
  // This button does not create/retain a desktop session or handle passwords.
  on($('btnAccount'), 'click', function () {
    rpc('LAUNCHER_OPEN_ACCOUNT_PORTAL').then(function (result) {
      if (!result || result.ok !== true)
        throw new Error('account portal could not be opened');
    }).catch(function (error) {
      reportFailure('accountPortalFailed', error);
    });
  });

  // EXIT ALL is armed by a first click (old RadioButton2 kills the whole
  // family — a single accidental click must not do that).
  var exitArm = null;
  on(el.btnExit, 'click', function () {
    var b = this;
    if (b.classList.contains('armed')) {
      clearTimeout(exitArm);
      b.classList.remove('armed');
      setText(b, 'exiting');
      rpc('LAUNCHER_EXIT_ALL').then(function (result) {
        if (!result || result.ok === false) throw new Error('shutdown request failed');
      }).catch(function (error) {
        b.classList.remove('armed');
        setText(b, 'exitAll');
        reportFailure('exitFailed', error);
      });
      return;
    }
    b.classList.add('armed');
    setText(b, 'clickAgain');
    exitArm = setTimeout(function () {
      b.classList.remove('armed');
      setText(b, 'exitAll');
    }, 3000);
  });

  on(el.btnMin, 'click', function () {
    rpc('LAUNCHER_MINIMIZE').catch(function () {});
  });
  on(el.btnClose, 'click', function () {
    rpc('LAUNCHER_CLOSE').catch(function () {});
  });

  // ── Window drag (titlebar mousedown, buttons excluded) ──────────────
  var titlebar = $('titlebar');
  if (titlebar) {
    titlebar.addEventListener('mousedown', function (e) {
      if (e.button !== 0) return;
      if (e.target.closest && e.target.closest('button, select, option, label')) return;
      rpc('LAUNCHER_DRAG').catch(function () {});
    });
  }
})();
