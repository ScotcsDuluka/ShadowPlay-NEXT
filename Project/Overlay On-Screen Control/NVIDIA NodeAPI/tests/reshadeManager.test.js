'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const installer = require('../shims/reshade-installer-route');
const ui = require('../osc/mods/reshade-manager');

const exe = 'C:\\Games\\Example\\game.exe';
function fakeIO(names, logText) {
  return {
    statSync: () => ({isFile: () => true}),
    readdirSync: () => names,
    openSync: () => 1,
    fstatSync: () => ({size: Buffer.byteLength(logText || '')}),
    readSync: (_handle, buffer) => Buffer.from(logText || '').copy(buffer),
    closeSync: () => {}
  };
}
test('no ReShade evidence shows Not Installed locally, not global proof', () => {
  const result = installer.inspect(exe, fakeIO(['game.exe']));
  assert.equal(result.status, 'not-detected');
  assert.equal(result.installationStatus, 'Not Installed');
  assert.equal(result.installationScope, 'game-directory-only');
  assert.equal(result.graphicsApiVerified, false);
  assert.equal(result.mutationSupported, false);
  assert.equal(result.gameRunning, 'not-verified');
});
test('unidentified DXGI proxy is Unknown, never claimed as installed', () => {
  const result = installer.inspect(exe, fakeIO(['game.exe','dxgi.dll']));
  assert.equal(result.status, 'possible');
  assert.equal(result.installationStatus, 'Unknown');
  assert.deepEqual(result.graphicsApiHints, ['d3d10','d3d11','d3d12']);
  assert.equal(result.evidence.reshadeLogIdentified, false);
});
test('ReShade log plus proxy yields evidence-based Installed', () => {
  const result = installer.inspect(exe,
    fakeIO(['GAME.EXE','ReShade.log','d3d9.dll','ReShade.ini'],
      'ReShade version 6.0\nInitializing crosire\'s ReShade'));
  assert.equal(result.installationStatus, 'Installed');
  assert.equal(result.detected, true);
  assert.deepEqual(result.graphicsApiHints, ['d3d9']);
  assert.equal(result.evidence.reshadeLogIdentified, true);
});
test('game path validation rejects UNC, relative, missing exe and malformed inputs', () => {
  for (const input of ['game.exe', '\\\\server\\share\\game.exe', 'C:\\game.dll', 1234, '']) {
    assert.throws(() => installer.inspect(input, fakeIO(['game.exe'])), /Choose an absolute local Windows game/);
  }
});
test('UI helpers remain read-only and report the status contract', () => {
  assert.equal(ui.label('detected'), 'Installed');
  assert.equal(ui.label('possible'), 'Unknown');
  assert.equal(ui.label('not-detected'), 'Not Installed');
  assert.equal(ui.isGamePath(exe), true);
  assert.equal(ui.isGamePath('notepad.exe'), false);
  assert.equal(ui.isGamePath('\\\\server\\share\\game.exe'), false);
  const script = fs.readFileSync(path.join(__dirname,'../osc/mods/reshade-manager.js'),'utf8');
  assert.match(script, /window\.confirm\(/);
  assert.match(script, /window\.open\(official/);
  assert.doesNotMatch(script, /(?:fs\.copyFile|unlinkSync|execFile|spawn\()/);
});

test('ReShade Manager compiles genuine Angular Material form components', () => {
  const source = fs.readFileSync(path.join(__dirname,'../osc/mods/reshade-manager.js'),'utf8');
  const styles = fs.readFileSync(path.join(__dirname,'../osc/mods/reshade-manager.css'),'utf8');
  assert.match(source, /node\('md-input-container'/);
  assert.match(source, /node\('md-select'/);
  assert.match(source, /node\('md-option'/);
  assert.match(source, /node\('md-checkbox'/);
  assert.match(source, /injector\.get\('\$compile'\)\(manager\)\(scope\)/);
  assert.match(source, /var parentScope = settings\.__nvScope;/);
  assert.match(source, /var scope = parentScope\.\$new\(\)/);
  assert.doesNotMatch(source, /node\('select', 'nv-reshade-input'\)/);
  assert.match(source, /node\('details', 'nv-reshade-disclosure'\)/);
  assert.match(styles, /#nv-reshade-manager md-input-container/);
  assert.match(styles, /#nv-reshade-manager md-checkbox/);
  assert.match(source, /fetchJson\(base \+ '\/Inspect'/);
  assert.match(source, /window\.confirm\(/);
  assert.match(source, /window\.open\(official/);
});

test('ReShade Manager uses compact NVIDIA Settings proportions without numbered cards', () => {
  const script = fs.readFileSync(path.join(__dirname, '../osc/mods/reshade-manager.js'), 'utf8');
  const styles = fs.readFileSync(path.join(__dirname, '../osc/mods/reshade-manager.css'), 'utf8');
  assert.doesNotMatch(script, /heading\.appendChild\(node\('span', 'nv-reshade-step'/);
  assert.match(script, /headingWrap\.insertBefore\(back, headingWrap\.firstChild\)/);
  assert.ok(styles.includes('#nv-reshade-manager .nv-reshade-section {'));
  assert.ok(styles.includes('border-bottom: 1px solid rgba(171, 179, 184, .22)'));
  assert.ok(styles.includes('height: 46px !important'));
  assert.ok(styles.includes('font-size: 16px !important'));
  assert.ok(styles.includes('max-width: none'));
  assert.ok(styles.includes('grid-template-columns: minmax(0, 1.7fr) minmax(250px, 1fr)'));
  assert.ok(styles.includes('#nv-filter-settings.nv-reshade-open > :not(#nv-reshade-manager)'));
  assert.ok(styles.includes('background: #262b30'));
  assert.ok(styles.includes('@media (max-width: 720px)'));
});

test('NodeAPI mounts safe installer route and OSC mod manifest', () => {
  const source = fs.readFileSync(path.join(__dirname,'../index.js'),'utf8');
  assert.match(source, /reshade-installer-route\.js'\)\.mount\(app\)/);
  assert.match(source, /'reshade-manager\.js': true/);
  assert.match(source, /'reshade-manager\.css': true/);
  const settings = fs.readFileSync(path.join(__dirname,'../osc/mods/gpu-status-panel.js'),'utf8');
  assert.match(settings, /Open ReShade Manager/);
  assert.doesNotMatch(settings, /osc-button-disabled" disabled aria-describedby="nv-reshade-install-help"/);
});
