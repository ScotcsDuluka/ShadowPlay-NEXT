'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const source = fs.readFileSync(
  path.join(__dirname, '../osc/mods/video-capture-options.js'), 'utf8');
const ending = /\}\)\(\);\s*$/;
assert.match(source, ending);

function fakeElement(tag) {
  return {
    tagName: tag.toUpperCase(), children: [], attributes: {},
    textContent: '', className: '',
    classList: { add() {}, remove() {} },
    setAttribute(name, value) { this.attributes[name] = value; },
    appendChild(child) { this.children.push(child); return child; }
  };
}

function bootstrap() {
  const calls = [];
  const win = {
    location: { hash: '#/base/preferences/video' },
    setInterval() { return 1; }, clearInterval() {},
    setTimeout() { return 1; }, clearTimeout() {},
    addEventListener() {},
    angular: {
      element() {
        return { injector() {
          return { get(name) {
            if (name === '$rootScope') return { $new() {
              return { $applyAsync() {}, $destroy() {} };
            }};
            throw new Error('Unexpected Angular service: ' + name);
          }};
        }};
      }
    }
  };
  const exposed = source.replace(ending, `
    window.__draftTest = {
      create: createPanelScope,
      plan: buildCapturePlanPanel,
      setSavedState: function (value) { state = value; },
      savedState: function () { return state; }
    };
  })();
  `);
  vm.runInNewContext(exposed, {
    window: win,
    document: { documentElement: {}, createElement: fakeElement },
    navigator: {},
    fetch(url, options) { calls.push({ url, options }); throw new Error('Unexpected network request'); },
    Promise, Set, Number, Object, String, Array, JSON, Error
  });
  return { hooks: win.__draftTest, calls };
}

function flatten(node, result = []) {
  result.push(node);
  for (const child of node.children || []) flatten(child, result);
  return result;
}

test('Pipeline Design is a non-persistent draft with explicit unsupported providers', () => {
  const h = bootstrap();
  const scope = h.hooks.create();
  assert.equal(scope.capturePlanOpen, false);
  assert.equal(scope.capturePlanReady, true);
  assert.equal(scope.capturePlan.desktopApi, 'dxgi');
  assert.equal(scope.capturePlan.exclusiveApi, 'dxgi');
  assert.equal(scope.capturePlan.encoder, 'NVENC_H264');
  assert.equal(scope.capturePlan.recovery, 'planned');
  assert.equal(scope.capturePlanRecoveryChoices[0].disabled, true);
  assert.equal(scope.capturePlanAudioChoices.find(x => x.value === 'ffmpeg_audio').disabled, true);
  assert.equal(scope.capturePlanEncoderChoices.find(x => x.value === 'QuickSync_H264').disabled, true);
  assert.equal(scope.capturePlanExclusiveChoices.find(x => x.value === 'exclusive_source').disabled, true);

  const saved = { engineMode: 'Duluka', apiCapture: 'ddagrab', encoder: 'NVENC_H264' };
  h.hooks.setSavedState(saved);
  scope.capturePlan.systemAudio = 'duluka_wasapi';
  scope.capturePlan.microphone = 'off';
  scope.onCapturePlanChange();
  assert.match(scope.capturePlanSummary, /DRAFT ONLY/);
  assert.match(scope.capturePlanSummary, /Duluka WASAPI/);
  assert.equal(h.hooks.savedState(), saved);
  assert.deepEqual(h.calls, [], 'editing a design draft must NEVER POST to a runtime');
});

test('NVIDIA Material plan uses five independent choices and one disabled recovery slot', () => {
  const h = bootstrap();
  h.hooks.create();
  const panel = h.hooks.plan();
  const items = flatten(panel);
  assert.equal(panel.id, 'nv-video-session-plan');
  assert.equal(panel.attributes['ng-show'], 'capturePlanOpen');
  const selects = items.filter(x => x.tagName === 'MD-SELECT');
  assert.equal(selects.length, 6);
  assert.deepEqual(
    selects.map(x => x.attributes['ng-model']),
    ['capturePlan.desktopApi','capturePlan.exclusiveApi','capturePlan.encoder',
     'capturePlan.systemAudio','capturePlan.microphone','capturePlan.recovery']);
  for (const select of selects) {
    assert.equal(select.attributes['ng-disabled'],
      select.attributes['ng-model'] === 'capturePlan.recovery' ? 'true' : '!capturePlanReady');
    assert.equal(select.attributes['ng-change'], 'onCapturePlanChange()');
    assert.equal(select.children[0].attributes['ng-disabled'], 'choice.disabled');
  }
  const note = items.find(x => /not saved or applied/.test(x.textContent));
  assert.ok(note, 'draft-only warning must be plainly visible');
});

test('Live Settings and design view switch content without changing existing capture save path', () => {
  assert.match(source, /addViewTab\('Live Settings', false\)/);
  assert.match(source, /addViewTab\('Pipeline Design', true\)/);
  assert.match(source, /displaySection\.setAttribute\('ng-show', '!capturePlanOpen'\)/);
  assert.match(source, /advancedSection\.setAttribute\('ng-show', '!capturePlanOpen'\)/);
  assert.match(source, /panel\.appendChild\(buildCapturePlanPanel\(\)\)/);
  assert.match(source, /var endpoint = '\/DulukaCapture\/v\.1\.0\/settings'/);
});

test('Design viewport locks tab/field/panel/footer heights and equal column tracks', () => {
  const css = fs.readFileSync(path.join(__dirname,
    '../osc/mods/video-capture-options.css'), 'utf8');
  const locked = css.split('/* UI v72: LOCKED NVIDIA-style Pipeline Design sizing.')[1];
  assert.ok(locked, 'locked sizing CSS must be present');
  assert.match(locked, /grid-template-columns:\s*repeat\(3,\s*minmax\(0,\s*1fr\)\)/);
  assert.match(locked, /grid-template-rows:\s*repeat\(2,\s*66px\)/);
  assert.match(locked, /\.nv-video-plan-tabs[\s\S]*?height:\s*36px/);
  assert.match(locked, /\.nv-video-plan-grid\s+\.nv-video-plan-field[\s\S]*?height:\s*66px/);
  assert.match(locked, /md-select\s*\{[\s\S]*?height:\s*34px/);
  assert.match(locked, /\.nv-video-engine-row\.nv-video-engine-footer[\s\S]*?display:\s*grid/);
  assert.match(locked, /\.nv-video-plan-summary[\s\S]*?-webkit-line-clamp:\s*2/);
  assert.match(locked, /@media \(max-width:\s*850px\)[\s\S]*?grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)/);
  assert.doesNotMatch(locked, /overflow-y:\s*(auto|scroll)/,
    'locked design should not introduce nested scrollbars');
});
