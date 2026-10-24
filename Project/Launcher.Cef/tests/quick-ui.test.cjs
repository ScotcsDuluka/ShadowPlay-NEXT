'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const base = path.join(__dirname, '..', 'ui');
const html = fs.readFileSync(path.join(base, 'index.html'), 'utf8');
const css = fs.readFileSync(path.join(base, 'quick-ui.css'), 'utf8');
const script = fs.readFileSync(path.join(base, 'quick-ui.js'), 'utf8');
function createHarness(state) {
  const nodes = Object.create(null);
  const ids = Array.from(html.matchAll(/\bid="([^"]+)"/g), match => match[1]);
  let oldPowerClicks = 0, oldOverlayClicks = 0;
  function node(id) {
    const selected = new Set();
    const handlers = Object.create(null);
    return {
      id, textContent: '', disabled: false, attrs: {}, children: [], scrollTop: 59,
      className: '',
      classList: {
        toggle(x, active) { if (active) selected.add(x); else selected.delete(x); },
        contains(x) { return selected.has(x); },
        add(x) { selected.add(x); }, remove(x) { selected.delete(x); }
      },
      addEventListener(name, callback) { handlers[name] = callback; },
      setAttribute(key, value) { this.attrs[key] = String(value); },
      removeAttribute(key) { delete this.attrs[key]; },
      getAttribute(key) { return this.attrs[key]; },
      appendChild(child) { this.children.push(child); },
      click() {
        if (id === 'tglOverlay') oldPowerClicks++;
        if (id === 'btnOpenOverlay') oldOverlayClicks++;
        if (handlers.click) handlers.click();
      }
    };
  }
  for (const id of ids) nodes[id] = node(id);
  const powerCard = node('power-card');
  const document = {
    getElementById(id) { return nodes[id] || null; },
    querySelector(selector) { return selector === '.quick-power-card' ? powerCard : null; }
  };
  const win = {
    __quickState: state || null,
    cefQuery() {},
    LauncherI18n: { t(key) { return key; } }
  };
  vm.runInNewContext(script,{document, window:win, setTimeout(){return 1;}, clearTimeout(){}},{filename:'quick-ui.js'});
  return {nodes,powerCard,win,get oldPowerClicks(){return oldPowerClicks;},
    get oldOverlayClicks(){return oldOverlayClicks;}};
}
test('three primary tabs; no editable Core Module page',()=>{
  for (const tab of ['Power','Core','Addons']) {
    assert.match(html,new RegExp('id="quickNav'+tab+'"'));
    assert.match(html,new RegExp('id="quickPage'+tab+'"'));
  }
  assert.doesNotMatch(html,/id="quickNavConfig"/);
  assert.doesNotMatch(html,/id="quickPageConfig"/);
  assert.doesNotMatch(html,/id="quickModuleLayout"/);
  assert.doesNotMatch(html,/src="module-profiles\.js/);
  assert.match(html,/<body class="quick-mode">/);
  assert.match(css,/\.quick-mode \.workspace \{ display: none; \}/);
  assert.match(html,/id="tglOverlay"/);
  assert.match(html,/id="appearanceSettings"/);
  assert.match(html,/src="quick-ui\.js/);
});
test('navigation only changes views and leaves native controls intact',()=>{
  const h=createHarness();
  for(const tab of ['Core','Addons','Power']) {
    h.nodes['quickNav'+tab].click();
    assert.equal(h.nodes['quickPage'+tab].classList.contains('active'),true);
    assert.equal(h.nodes['quickNav'+tab].getAttribute('aria-current'),'page');
    assert.equal(h.nodes.quickMain.scrollTop,0);
  }
  assert.equal(h.oldPowerClicks,0);
});
test('power status uses real config flag, clicks existing native toggle',()=>{
  const h=createHarness({overlayEnabled:false,overlayApi:{running:false},modules:{}});
  assert.equal(h.nodes.quickPowerState.textContent,'quickIsOff');
  assert.equal(h.nodes.quickPowerToggle.textContent,'quickPowerOn');
  assert.equal(h.nodes.quickPowerToggle.disabled,false);
  h.nodes.quickPowerToggle.click();
  assert.equal(h.oldPowerClicks,1);
  h.win.onQuickLauncherState({overlayEnabled:true,overlayApi:{running:true,ready:true},modules:{}});
  assert.equal(h.nodes.quickPowerState.textContent,'quickIsOn');
  assert.equal(h.nodes.quickEngineStatus.textContent,'quickServiceReady');
  h.nodes.quickOpenOverlay.click();
  assert.equal(h.oldOverlayClicks,1);
});
test('native module inventory differentiates installed/running/missing/unknown',()=>{
  const h=createHarness({overlayEnabled:true,overlayApi:{running:true},modules:{
    Container:{installed:true,processTracking:true,running:true},
    Overlay:{installed:true,processTracking:true,running:false},
    Hook:{installed:true,processTracking:false},
    Ansel:{installed:false,processTracking:false}
  }});
  assert.equal(h.nodes.quickStatusContainer.textContent,'running');
  assert.equal(h.nodes.quickStatusOverlay.textContent,'stopped');
  assert.equal(h.nodes.quickStatusHook.textContent,'moduleInstalled');
  assert.equal(h.nodes.quickStatusAnsel.textContent,'moduleMissing');
  assert.equal(h.nodes.quickStatusFfmpeg.textContent,'moduleWaiting');
});

test('Magisk-style directory has all 11 core modules and no placeholder add-ons', () => {
  const core=html.match(/<section class="quick-page" id="quickPageCore"[\s\S]*?<\/section>/)[0];
  const addons=html.match(/<section class="quick-page" id="quickPageAddons"[\s\S]*?<\/section>/)[0];
  const ids=[...core.matchAll(/data-module="([A-Za-z]+)"/g)].map(m=>m[1]);
  assert.deepEqual(ids,['Container','Overlay','NodeApi','Capture','Graphics','Helper',
    'Adapter','Hook','Ansel','Ffmpeg','Config']);
  assert.equal((core.match(/data-config-module="/g)||[]).length,0);
  assert.doesNotMatch(core,/quick-module-readonly|quick-core-lock|READ ONLY/);
  assert.doesNotMatch(core,/class="quick-module-bottom"/);
  assert.doesNotMatch(core,/data-i18n="quickCoreReadOnly"/);
  assert.equal((core.match(/class="quick-module-meta"/g)||[]).length,11);
  assert.equal((core.match(/class="quick-module-head"/g)||[]).length,11);
  assert.equal((core.match(/data-lifecycle="suite-managed"/g)||[]).length,11);
  assert.doesNotMatch(core,/data-i18n="quickCoreLifecycle"/);
  assert.doesNotMatch(html,/data-i18n="quickCoreSettingsLifecycle"/);
  assert.match(core,/data-i18n="quickServerCatalogPending"/);
  assert.match(addons,/data-i18n="quickAddonLifecycle"/);
  assert.doesNotMatch(core,/role="switch"/);
  assert.doesNotMatch(core,/data-module-power|data-toggle-module|data-remove-module/);
  assert.doesNotMatch(script,/LAUNCHER_(?:ENABLE|DISABLE|UNINSTALL)_CORE_MODULE/);
  assert.doesNotMatch(core,/id="quickModuleLayout"/);
  assert.match(core,/data-catalog-source="bundled-fallback"/);
  assert.match(core,/data-runtime-source="local-launcher"/);
  assert.doesNotMatch(addons,/data-module="/);
  assert.match(addons,/id="quickPageAddons"/);
  assert.match(addons,/data-i18n="quickAddonEmpty"/);
  assert.match(css,/\.quick-module-grid \{ display: grid; grid-template-columns: minmax\(0,1fr\)/);
  // The old generic `.quick-module p` style must not flatten the description's inset.
  assert.match(css,/\.quick-module\s*>\s*p\.quick-module-description\s*\{[^}]*margin:\s*8px 20px 18px 73px/);
  assert.doesNotMatch(css,/\.quick-module-readonly|\.quick-core-lock/);
});
test('core module inventory cannot be edited through Launcher module UI',()=>{
  const h=createHarness({modules:{Config:{installed:true,processTracking:false}}});
  assert.equal(h.win.ShadowPlayModuleUI,undefined);
  assert.equal(h.nodes.quickPageConfig,undefined);
  assert.equal(h.nodes.quickStatusConfig.textContent,'moduleInstalled');
  assert.equal(h.nodes.quickModuleLayout,undefined);
  assert.equal(h.nodes.quickGoCore,undefined);
  assert.equal(h.oldPowerClicks,0);
  assert.equal(h.oldOverlayClicks,0);
  assert.doesNotMatch(script,/moduleRpc|LAUNCHER_UNINSTALL_APPEARANCE|LAUNCHER_SET_MODULE/);
  assert.doesNotMatch(html,/id="btnApplyAppearance"[^>]*data-config-module/);
});

test('Web Github art direction renders complete Core cards without editing actions or remote assets',()=>{
  const core=html.match(/<section class="quick-page" id="quickPageCore"[\s\S]*?<\/section>/)[0];
  assert.match(core,/class="quick-core-stage"/);
  assert.match(core,/class="quick-core-stage-grid"/);
  assert.match(core,/class="quick-core-monogram"/);
  assert.match(core,/class="quick-core-information"/);
  assert.equal((core.match(/class="quick-module-accent"/g)||[]).length,11);
  assert.equal((core.match(/class="quick-module-edge"/g)||[]).length,11);
  assert.equal((core.match(/class="quick-module-meta"/g)||[]).length,11);
  assert.equal((core.match(/class="quick-module-state"/g)||[]).length,11);
  assert.equal((core.match(/data-lifecycle="suite-managed"/g)||[]).length,11);
  assert.doesNotMatch(core,/<button\b|<input\b|<select\b|data-config-module|quick-module-readonly|READ ONLY/);
  assert.match(core,/data-catalog-source="bundled-fallback"/);
  assert.match(core,/data-runtime-source="local-launcher"/);
  assert.match(css,/#quickPageCore \.quick-module-grid\s*\{[^}]*grid-template-columns:repeat\(2,minmax\(0,1fr\)\)/);
  assert.match(css,/#quickPageCore \.quick-core-stage-grid\s*\{/);
  assert.match(css,/@media\(max-width:960px\)/);
  assert.doesNotMatch(core,/https?:\/\/|<script\b|<iframe\b/);
});
