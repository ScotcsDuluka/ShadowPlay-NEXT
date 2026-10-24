(function () {
  'use strict';
  var base = '/Duluka/v.1.0/ReShade/Installer';
  var official = 'https://reshade.me/';
  var active = null;

  function label(status) {
    return status === 'detected' ? 'Installed' :
      status === 'not-detected' ? 'Not Installed' : 'Unknown';
  }
  function isGamePath(text) {
    return typeof text === 'string' && /^[A-Z]:[\\/].+\.exe$/i.test(text.trim()) &&
      text.length <= 1024;
  }
  function node(tag, className, text) {
    var element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }
  function makeButton(title, method, primary) {
    var button = node('button', 'osc-button ' +
      (primary ? 'osc-button-green' : 'osc-button-gray'), title);
    button.type = 'button';
    button.setAttribute('hover-focus', '');
    button.addEventListener('click', method);
    return button;
  }
  function fetchJson(url, options) {
    return fetch(url, options).then(function (reply) {
      return reply.json().catch(function () { return {}; }).then(function (body) {
        if (!reply.ok) throw new Error(body.message || body.codeText || 'Request failed');
        return body;
      });
    });
  }
  function materialSelect(items, model, change, aria) {
    var select = node('md-select', 'osc-select nv-reshade-md-select');
    select.setAttribute('ng-model', model);
    select.setAttribute('ng-change', change);
    select.setAttribute('aria-label', aria);
    select.setAttribute('hover-focus', '');
    items.forEach(function (item) {
      var option = node('md-option', null, item[1]);
      option.setAttribute('value', item[0]);
      option.setAttribute('md-ink-ripple', 'false');
      select.appendChild(option);
    });
    return select;
  }
  function field(caption, control) {
    var container = node('md-input-container', 'nv-reshade-md-field');
    container.appendChild(node('label', null, caption));
    container.appendChild(control);
    return container;
  }
  function section(parent, number, name) {
    var element = node('section', 'nv-reshade-section');
    var heading = node('div', 'nv-reshade-section-heading');
    // Native NVIDIA settings use plain section headers, not numbered cards.
    heading.appendChild(node('h3', null, name));
    element.appendChild(heading);
    parent.appendChild(element);
    return element;
  }

  function mount() {
    var settings = document.querySelector('#nv-filter-settings');
    if (!settings) return null;
    var previous = settings.querySelector('#nv-reshade-manager');
    if (previous) return previous.__nvManager || null;

    // This page is mounted inside the existing Angular NVIDIA Settings scope.
    // Material controls MUST be compiled; raw <select>/<input> styles do not work.
    var parentScope = settings.__nvScope;
    var injector = window.angular &&
      window.angular.element(document.documentElement).injector();
    if (!parentScope || parentScope.$$destroyed || !injector) return null;
    var scope = parentScope.$new();
    var view = scope.reshadeUi = {
      path:'', api:'unknown', operation:'install', confirmed:false
    };
    var manager = node('section', 'nv-reshade-manager');
    manager.id = 'nv-reshade-manager';
    manager.setAttribute('aria-label', 'ReShade Manager');
    manager.hidden = true;

    var top = node('div', 'nv-reshade-heading');
    var headingWrap = node('div', 'nv-reshade-heading-text');
    headingWrap.appendChild(node('h2', null, 'ReShade Manager'));
    headingWrap.appendChild(node('p', null,
      'ตรวจสอบเกมก่อนดำเนินการผ่าน ReShade Setup อย่างเป็นทางการ'));
    var back = makeButton('‹ Game filter', function () {
      settings.classList.remove('nv-reshade-open');
      manager.hidden = true;
      state.open = false;
      state.token++;
      state.infoVersion++;
      state.contractReady = false;
      state.busy = false;
      refresh();
    }, false);
    back.className = 'nv-reshade-back';
    headingWrap.insertBefore(back, headingWrap.firstChild);
    top.appendChild(headingWrap);
    manager.appendChild(top);

    var inspectSection = section(manager, '01', 'Game inspection');
    var fields = node('div', 'nv-reshade-fields');
    var gameInput = node('input');
    gameInput.type = 'text';
    gameInput.placeholder = 'C:\\Games\\Your Game\\game.exe';
    gameInput.autocomplete = 'off';
    gameInput.setAttribute('ng-model', 'reshadeUi.path');
    gameInput.setAttribute('ng-change', 'reshadeUi.pathChanged()');
    gameInput.setAttribute('aria-label', 'Full path to game executable');
    fields.appendChild(field('Game executable (.exe)', gameInput));
    var apiSelect = materialSelect([
      ['unknown','Unknown / inspect first'],
      ['d3d9','Direct3D 9 / 9Ex'], ['d3d10','Direct3D 10'],
      ['d3d11','Direct3D 11'], ['d3d12','Direct3D 12'],
      ['opengl','OpenGL'], ['vulkan','Vulkan']
    ], 'reshadeUi.api', 'reshadeUi.apiChanged()', 'Graphics API (unverified)');
    fields.appendChild(field('Graphics API (manual hint)', apiSelect));
    inspectSection.appendChild(fields);
    inspectSection.appendChild(node('p', 'nv-reshade-hint',
      'การเลือก API เป็นข้อมูลประกอบเท่านั้น ไม่ใช่การยืนยันจากตัวเกม'));
    var actions = node('div', 'nv-reshade-actions');
    var inspectButton = makeButton('Inspect game', inspect, true);
    var verifyButton = makeButton('Verify after Setup', inspect);
    verifyButton.disabled = true;
    actions.appendChild(inspectButton);
    actions.appendChild(verifyButton);
    inspectSection.appendChild(actions);

    var resultSection = section(manager, '02', 'Inspection result');
    var summary = node('div', 'nv-reshade-summary');
    var badge = node('span', 'nv-reshade-state', 'Unknown');
    var details = node('p', 'nv-reshade-result-copy', 'Select a game executable and inspect.');
    summary.appendChild(badge);
    summary.appendChild(details);
    resultSection.appendChild(summary);
    var disclosure = node('details', 'nv-reshade-disclosure');
    disclosure.appendChild(node('summary', null, 'View inspection evidence'));
    var evidence = node('div', 'nv-reshade-evidence');
    evidence.setAttribute('aria-label', 'Inspection evidence');
    disclosure.appendChild(evidence);
    resultSection.appendChild(disclosure);

    var setupSection = section(manager, '03', 'Official ReShade Setup');
    var notice = node('p', 'nv-reshade-notice',
      'Close the game first. Check anti-cheat rules. Existing graphics DLLs may belong to other tools.');
    notice.setAttribute('role', 'note');
    setupSection.appendChild(notice);
    var operation = materialSelect([
      ['install','Install'], ['update','Update'], ['uninstall','Uninstall']
    ], 'reshadeUi.operation', 'reshadeUi.operationChanged()', 'Official Setup operation');
    setupSection.appendChild(field('Operation through official Setup', operation));

    var permission = node('md-checkbox', 'nv-reshade-confirm');
    permission.setAttribute('ng-model', 'reshadeUi.confirmed');
    permission.setAttribute('ng-change', 'reshadeUi.confirmChanged()');
    permission.setAttribute('aria-label', 'Confirm game closed and official setup rules reviewed');
    permission.appendChild(document.createTextNode(
      'I closed the game, reviewed its anti-cheat rules, and understand only official ReShade Setup will request changes.'));
    setupSection.appendChild(permission);
    var launchRow = node('div', 'nv-reshade-actions nv-reshade-launch-row');
    var launch = makeButton('Open official ReShade Setup', launchSetup, true);
    launch.disabled = true;
    var link = node('a', 'nv-reshade-official-link', 'Official website ↗');
    link.href = official;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
    launchRow.appendChild(launch);
    launchRow.appendChild(link);
    setupSection.appendChild(launchRow);

    var footer = node('div', 'nv-reshade-feedback');
    var progress = node('p', 'nv-reshade-progress', 'Ready.');
    progress.setAttribute('role', 'status');
    progress.setAttribute('aria-live', 'polite');
    var error = node('p', 'settings-text-warning');
    error.setAttribute('role', 'alert');
    footer.appendChild(progress);
    footer.appendChild(error);
    manager.appendChild(footer);

    var state = {open:false, busy:false, inspected:null, inspectedPath:'', token:0,
      infoVersion:0, contractReady:false};
    function digest() {
      if (!scope.$$destroyed) scope.$evalAsync();
    }
    function refresh() {
      inspectButton.disabled = state.busy;
      verifyButton.disabled = state.busy || !state.inspected;
      // Fail closed until this particular opening verifies the safe guided-setup contract.
      launch.disabled = state.busy || !state.contractReady || !state.inspected || !view.confirmed ||
        state.inspectedPath !== gameInput.value.trim() ||
        state.inspected.gameRunning === 'possible';
      digest();
    }
    function clearInspection() {
      state.token++;
      state.busy = false;
      state.inspected = null;
      state.inspectedPath = '';
      badge.textContent = 'Unknown';
      badge.removeAttribute('data-state');
      details.textContent = 'Select a game executable and inspect again.';
      evidence.textContent = '';
      disclosure.open = false;
      view.confirmed = false;
      refresh();
    }
    view.pathChanged = clearInspection;
    view.apiChanged = function () {
      if (state.inspected) showEvidence(state.inspected);
    };
    view.confirmChanged = refresh;
    view.operationChanged = refresh;

    function showEvidence(result) {
      var facts = result.evidence || {};
      evidence.textContent = '';
      [
        'Game: ' + (result.executable || 'Unavailable'),
        'Scope: local game folder only; global or alternate installations not verified',
        'Graphics API hints: ' + ((result.graphicsApiHints || []).join(', ') || 'None') +
          ' (not verified)',
        'Selected API: ' + (view.api === 'unknown' ? 'Unknown' : view.api) +
          ' (manual selection, not verified)',
        'Candidate graphics DLLs: ' + ((facts.proxyDllNames || []).join(', ') || 'None'),
        'ReShade.ini: ' + (facts.reshadeConfigPresent ? 'Present' : 'Not found'),
        'ReShade.log: ' + (facts.reshadeLogPresent ? 'Present' : 'Not found'),
        'Identified ReShade log: ' + (facts.reshadeLogIdentified ? 'Yes' : 'No'),
        'ReShade shaders folder: ' + (facts.shadersFolderPresent ? 'Present' : 'Not found'),
        'Game running: ' + (result.gameRunning || 'Not verified'),
        'Result: ' + (result.message || 'Inspection completed')
      ].forEach(function (line) { evidence.appendChild(node('p', null, line)); });
    }
    function inspect() {
      if (state.busy) return;
      var exe = gameInput.value.trim();
      if (!isGamePath(exe)) {
        error.textContent = 'Enter a local Windows game executable path ending in .exe.';
        progress.textContent = 'Inspection not started.';
        return;
      }
      var token = ++state.token;
      state.inspected = null;
      state.inspectedPath = '';
      state.busy = true;
      view.confirmed = false;
      error.textContent = '';
      progress.textContent = 'Inspecting game files (read-only)...';
      refresh();
      fetchJson(base + '/Inspect', {
        method:'POST',
        headers: {'Content-Type':'application/json'},
        body:JSON.stringify({executable:exe})
      }).then(function (result) {
        if (token !== state.token || !state.open) return;
        if (result.schemaVersion !== 1 || !result.evidence) {
          throw new Error('Installer API returned an invalid inspection report.');
        }
        state.inspected = result;
        state.inspectedPath = exe;
        badge.textContent = label(result.status);
        badge.setAttribute('data-state', result.status);
        details.textContent = result.message || 'Inspection completed.';
        showEvidence(result);
        progress.textContent = 'Inspection complete. Review evidence before using official Setup.';
      }).catch(function (cause) {
        if (token !== state.token || !state.open) return;
        badge.textContent = 'Unknown';
        badge.removeAttribute('data-state');
        details.textContent = 'Installation status cannot be confirmed.';
        evidence.textContent = '';
        error.textContent = cause.message || 'Inspection failed.';
        progress.textContent = 'Inspection failed; no files were changed.';
      }).then(function () {
        if (token !== state.token) return;
        state.busy = false;
        refresh();
      });
    }
    function launchSetup() {
      if (launch.disabled || !state.inspected || !state.open) return;
      var choice = view.operation;
      var message = 'Continue to the official ReShade website for ' + choice + '?\n\n' +
        'Game: ' + state.inspectedPath + '\n' +
        'No game DLLs will be changed by ShadowPlay NEXT. Follow official Setup prompts and return to Verify after completion.';
      if (!window.confirm(message)) return;
      var opened = window.open(official, '_blank', 'noopener,noreferrer');
      progress.textContent = opened === null ?
        'If a popup was blocked, use the official website link. Return and Verify after Setup.' :
        'Official ReShade website opened for ' + choice + '. Complete Setup manually, then Verify.';
      view.confirmed = false;
      refresh();
    }

    // Compile Angular Material against the SAME Settings scope hierarchy.
    // The parent destroys this scope when leaving Preferences/Mods.
    try {
      injector.get('$compile')(manager)(scope);
      settings.appendChild(manager);
      digest();
    } catch (cause) {
      if (!scope.$$destroyed) scope.$destroy();
      console.error('[reshade-manager] Angular Material render failed', cause);
      return null;
    }
    manager.__nvManager = {
      open: function () {
        if (!document.documentElement.contains(settings) || scope.$$destroyed) return false;
        state.open = true;
        state.contractReady = false;
        var thisOpening = ++state.infoVersion;
        manager.hidden = false;
        settings.classList.add('nv-reshade-open');
        error.textContent = '';
        progress.textContent = 'Checking official Setup safety contract...';
        refresh();
        fetchJson(base + '/Info').then(function (body) {
          if (!state.open || thisOpening !== state.infoVersion) return;
          if (body.schemaVersion !== 1 || body.mode !== 'guided' ||
              body.nativeInstall !== false || body.nativeUninstall !== false ||
              body.officialUrl !== official) {
            error.textContent = 'The official Setup safety contract could not be verified.';
            state.contractReady = false;
          } else {
            state.contractReady = true;
            progress.textContent = state.inspected ?
              'Inspection complete. Review evidence before using official Setup.' :
              'Enter the game path to begin read-only inspection.';
          }
          refresh();
        }).catch(function () {
          if (!state.open || thisOpening !== state.infoVersion) return;
          error.textContent = 'Could not connect to the ReShade installer API.';
          state.contractReady = false;
          refresh();
        });
        return true;
      }
    };
    return manager.__nvManager;
  }
  var api = {open: function () {
    if (active && active.open()) return true;
    active = mount();
    return active ? active.open() : false;
  }};
  if (typeof module !== 'undefined' && module.exports)
    module.exports = {label:label, isGamePath:isGamePath};
  if (typeof window !== 'undefined') window.NvReShadeManager = api;
})();
