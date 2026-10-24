'use strict';
// Native-free Command Center navigation + original CEF RPC smoke regression.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..', 'ui');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const code = fs.readFileSync(path.join(root, 'app.js'), 'utf8');

function harness() {
  const elements = Object.create(null);
  for (const hit of html.matchAll(/<[^>]+\bid="([^"]+)"[^>]*>/g)) {
    const markup = hit[0], id = hit[1];
    const active = /class="[^"]*\bactive\b/.test(markup);
    const classes = new Set(active ? ['active'] : []);
    const handlers = Object.create(null);
    const element = {
      id, dataset: {}, className: '', textContent: '',
      attrs: Object.create(null),
      classList: {
        add(c) { classes.add(c); }, remove(c) { classes.delete(c); },
        toggle(c, state) { if (state) classes.add(c); else classes.delete(c); },
        contains(c) { return classes.has(c); }
      },
      getAttribute(k) { return this.attrs[k] || null; },
      setAttribute(k, v) { this.attrs[k] = String(v); },
      removeAttribute(k) { delete this.attrs[k]; },
      addEventListener(type, cb) { handlers[type] = cb; },
      click() { if (handlers.click) handlers.click.call(this, { button: 0 }); },
      dispatch(type) { if (handlers[type]) handlers[type].call(this, { button: 0 }); }
    };
    elements[id] = element;
  }
  const calls = [];
  let push = null;
  const state = {
    overlayApi: { running: true, ready: true },
    notifierApi: { running: false }, nvApi: { running: true },
    lanes: { container: true, cefOverlay: true },
    overlayEnabled: true, engineOverlay: true
  };
  const document = { getElementById: id => elements[id] || null };
  const win = {
    LauncherI18n: {
      apply() {},
      t: key => key,
      getLanguage: () => 'en-US',
      setLanguage: v => v
    },
    cefQuery({ request, onSuccess }) {
      const cmd = JSON.parse(request); calls.push(cmd);
      onSuccess(JSON.stringify(cmd.command === 'LAUNCHER_GET_STATE' ? state : { ok: true }));
      return 1;
    },
    __onLauncherState(fn) { push = fn; fn(state); },
    console
  };
  vm.runInNewContext(code, { window: win, document, console,
    Date, Promise, JSON, setTimeout() { return 1; }, clearTimeout() {} }, { filename: 'app.js' });
  return { elements, calls, push, state };
}

test('every product module and system page navigates without a native service command', () => {
  const h = harness();
  const start = h.calls.length;
  for (const name of ['Capture','Graphics','Overlay','NodeApi','Container',
    'Adapter','Hook','Ansel','Helper','Ffmpeg','Services','Settings','Overview']) {
    h.elements['nav' + name].click();
    assert.equal(h.elements['view' + name].classList.contains('active'), true, name);
    assert.equal(h.elements['nav' + name].classList.contains('active'), true, name);
    assert.equal(h.elements['nav' + name].getAttribute('aria-current'), 'page');
    const activePanels = Object.entries(h.elements).filter(([id, el]) =>
      /^view[A-Z]/.test(id) && el.classList.contains('active'));
    assert.equal(activePanels.length, 1, 'one visible page for ' + name);
  }
  assert.equal(h.calls.length, start);
});

test('view navigation starts at top of content without changing modules or invoking RPC', () => {
  const h = harness();
  const before = h.calls.length;
  for (const page of ['Capture', 'Graphics', 'Overlay', 'Services', 'Settings']) {
    h.elements.contentScroll.scrollTop = 240;
    h.elements['nav' + page].click();
    assert.equal(h.elements.contentScroll.scrollTop, 0, 'reset scroll on ' + page);
  }
  assert.equal(h.calls.length, before, 'navigation performs no service commands');
});

test('sidebar source retains all 11 module links and an independent Services action', () => {
  const modules = ['Capture', 'Graphics', 'Overlay', 'NodeApi', 'Container',
    'Adapter', 'Hook', 'Ansel', 'Helper', 'Ffmpeg', 'Settings'];
  for (const item of modules) assert.match(html, new RegExp('id="nav' + item + '"'));
  assert.match(html, /id="navServices"/);
  const css = fs.readFileSync(path.join(root, 'app.css'), 'utf8');
  assert.match(css, /@media\s*\(max-height:\s*700px\)/);
  assert.match(css, /\.module-links\s*\{[^}]*overflow-y:\s*auto/);
});

test('module status distinguishes running processes, installed components and missing files', () => {
  const h = harness();
  h.state.modules = {
    Capture: {installed:true, processTracking:true, running:true},
    Graphics: {installed:true, processTracking:true, running:false},
    Hook: {installed:true, processTracking:false, running:true},
    Ansel: {installed:false, processTracking:false, running:false}
  };
  h.push(h.state);
  assert.equal(h.elements.moduleStateCapture.textContent, 'running');
  assert.equal(h.elements.moduleStateGraphics.textContent, 'stopped');
  // A component package must not be called active just because it exists,
  // even if a future producer accidentally supplies running:true.
  assert.equal(h.elements.moduleStateHook.textContent, 'moduleInstalled');
  assert.equal(h.elements.moduleStateAnsel.textContent, 'moduleMissing');
  assert.equal(h.elements.moduleAvailabilityCapture.textContent, 'moduleInstalled');
  assert.equal(h.elements.moduleAvailabilityAnsel.textContent, 'moduleMissing');
  assert.equal(h.elements.moduleStateNodeApi.textContent, 'moduleWaiting');
  assert.equal(h.elements.navStateCapture.className, 'nav-module-state running');
});

test('all eleven modules stay unknown when an older host supplies no module inventory', () => {
  const h = harness();
  for (const name of ['Capture','Graphics','Overlay','NodeApi','Container',
    'Adapter','Hook','Ansel','Helper','Ffmpeg','Config']) {
    assert.equal(h.elements['moduleState' + name].textContent, 'moduleWaiting', name);
    assert.equal(h.elements['moduleAvailability' + name].textContent, 'moduleWaiting', name);
  }
});

test('dashboard shortcut buttons navigate; capture Open Overlay reuses original RPC', async () => {
  const h = harness();
  h.elements.btnJumpServices.click();
  assert.ok(h.elements.viewServices.classList.contains('active'));
  h.elements.btnGotoCapture.click();
  assert.ok(h.elements.viewCapture.classList.contains('active'));
  h.elements.btnCaptureOverlay.click();
  assert.ok(h.calls.some(x => x.command === 'LAUNCHER_OPEN_OVERLAY'));
  await Promise.resolve();
});

test('status is descriptive, and Settings power and engine still use original commands', async () => {
  const h = harness();
  assert.equal(h.elements.systemHealth.textContent, 'systemOnline');
  h.state.overlayApi.ready = false;
  h.push(h.state);
  assert.equal(h.elements.systemHealth.textContent, 'systemPartial');
  h.elements.navSettings.click();
  h.elements.tglOverlay.click();
  h.elements.lblWinform.click();
  await Promise.resolve();
  assert.ok(h.calls.some(x => x.command === 'LAUNCHER_SET_OVERLAY'));
  assert.ok(h.calls.some(x => x.command === 'LAUNCHER_SET_ENGINE_OVERLAY'));
});

test('CEF 73 sidebar focus is keyboard-only and narrow rows protect labels', () => {
  const css = fs.readFileSync(path.join(root, 'app.css'), 'utf8');
  assert.match(css, /body\.keyboard-nav button:focus/);
  assert.match(css, /button:focus, select:focus,[\s\S]*?outline:\s*none/);
  assert.match(code, /document\.addEventListener\('mousedown'/);
  assert.match(code, /document\.body\.classList\.remove\('keyboard-nav'\)/);
  assert.match(css, /\.module-links \.side-link > span\[data-i18n\][\s\S]*?text-overflow:\s*ellipsis/);
  assert.match(css, /\.sidebar \{ width: 224px; flex: 0 0 224px;/);
});

test('all ID attributes unique: one bridge and one instance per native control', () => {
  const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(x => x[1]);
  assert.equal(new Set(ids).size, ids.length);
  for (const id of ['btnOpenOverlay','tglOverlay','tglEngine','btnExit','bridgeState','launcherLanguage']) {
    assert.equal(ids.filter(x => x === id).length, 1);
  }
});
