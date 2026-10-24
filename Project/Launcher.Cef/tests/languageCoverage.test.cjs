'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ui = path.join(__dirname, '..', 'ui');
const read = file => fs.readFileSync(path.join(ui, file), 'utf8');
const html = read('index.html');
const app = read('app.js');
const localesScript = read('osc-locales.js');
const i18nScript = read('i18n.js');

function makeHarness() {
  const options = [];
  const select = {appendChild(node) { options.push(node); }, value: ''};
  const document = {
    cookie: '', documentElement: {lang: ''},
    getElementById(id) {return id === 'launcherLanguage' ? select : null;},
    createElement() {return {textContent:'', value:''};},
    querySelectorAll() {return [];}
  };
  const window = {
    localStorage: {getItem(){return null;}, setItem(){}},
    document
  };
  const ctx = {window, document, Object, JSON};
  vm.runInNewContext(localesScript, ctx, {filename:'osc-locales.js'});
  vm.runInNewContext(i18nScript, ctx, {filename:'i18n.js'});
  return {window, document, options, select};
}

test('launcher has 28 OSC locale choices and all DOM keys have an English fallback', () => {
  const h = makeHarness();
  const localeCodes = Object.keys(h.window.LauncherOscLocales);
  assert.equal(localeCodes.length, 28);
  assert.equal(h.options.length + 2, 28); // static English + Thai
  const keys = [...new Set([...html.matchAll(/data-i18n(?:-title|-aria-label)?="([^"]+)"/g)].map(m=>m[1]))];
  assert.ok(keys.length > 100, 'main Launcher workspace should be localized');
  for(const key of keys)
    assert.notEqual(h.window.LauncherI18n.t(key), key, 'No English translation: '+key);
  for(const lang of localeCodes) {
    h.window.LauncherI18n.setLanguage(lang);
    assert.equal(h.window.LauncherI18n.getLanguage(), lang);
    for(const key of keys)
      assert.notEqual(h.window.LauncherI18n.t(key), key, lang+' missing fallback: '+key);
  }
});
test('dynamic Launcher translated labels never expose raw key names', () => {
  const h = makeHarness();
  const keys = [...new Set([
    ...[...app.matchAll(/\btr\(['"]([A-Za-z][\w]*)['"]\)/g)].map(m=>m[1]),
    ...[...app.matchAll(/\bsetText\([^,]+,\s*['"]([A-Za-z][\w]*)['"]\)/g)].map(m=>m[1])
  ])];
  assert.ok(keys.length > 0);
  for (const key of keys)
    assert.notEqual(h.window.LauncherI18n.t(key), key,'Unknown dynamic label '+key);
  // Document actual limitation: only a small subset of the separate OSC
  // locale dictionary maps to Launcher-specific page messages.
  const translated = Object.values(h.window.LauncherOscLocales);
  assert.ok(translated.every(v=>Object.keys(v.messages).length <= 15));
});
