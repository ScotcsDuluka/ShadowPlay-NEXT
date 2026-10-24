'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const child = require('node:child_process');
const project = path.join(__dirname, '..');
const root = path.join(project, 'osc', 'l10n');
const codes = fs.readdirSync(root).filter(n => /^[a-z]{2}-[A-Z]{2,3}\.json$/.test(n)).sort();
const english = JSON.parse(fs.readFileSync(path.join(root, 'en-US.json'), 'utf8').replace(/^\uFEFF/, ''));
const keys = Object.keys(english);
const manifest = JSON.parse(fs.readFileSync(path.join(project, 'tools', 'locale-fallbacks.json'), 'utf8'));
test('all 28 JSON locales have identical nonempty key sets and no duplicate declarations', () => {
  assert.equal(codes.length, 28);
  for (const code of codes) {
    const raw = fs.readFileSync(path.join(root, code), 'utf8').replace(/^\uFEFF/, '');
    const data = JSON.parse(raw);
    assert.deepEqual(Object.keys(data).sort(), keys.slice().sort(), code + ' key parity');
    assert.ok(Object.values(data).every(v => typeof v === 'string' && !!v.trim()), code + ' values');
    const declarations = [...raw.matchAll(/^\s*"((?:[^"\\]|\\.)*)"\s*:/gm)].map(m => JSON.parse('"' + m[1] + '"'));
    assert.equal(new Set(declarations).size, declarations.length, code + ' duplicate declaration');
  }
});
test('English fallbacks are explicitly documented and track actual present values', () => {
  assert.match(manifest.description, /NOT translations/);
  assert.equal(manifest.reference, 'en-US');
  const tracked = Object.entries(manifest.englishFallbacks).reduce((n, entry) => n + entry[1].length, 0);
  assert.equal(tracked, 5583);
  for (const [code, entries] of Object.entries(manifest.englishFallbacks)) {
    const values = JSON.parse(fs.readFileSync(path.join(root, code + '.json'), 'utf8').replace(/^\uFEFF/, ''));
    for (const key of entries) assert.equal(values[key], english[key], code + ': ' + key);
  }
});
test('OSC mod phrase keys all exist in all dictionaries', () => {
  const mapJs = fs.readFileSync(path.join(project, 'osc', 'mods', 'osc-ui-phrase-map.js'), 'utf8');
  const context = {window: {}};
  vm.runInNewContext(mapJs, context);
  const map = context.window.__oscUiPhraseKeys;
  assert.ok(Object.keys(map).length > 900);
  for (const key of Object.values(map))
    assert.ok(Object.prototype.hasOwnProperty.call(english, key), 'unknown mapped key ' + key);
});
test('coverage command is repeatable in read-only mode', () => {
  const run = child.spawnSync(process.execPath, [path.join(project, 'tools', 'locale-coverage.cjs')],
    {encoding: 'utf8'});
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /remainingMissing=0 errors=0/);
});
