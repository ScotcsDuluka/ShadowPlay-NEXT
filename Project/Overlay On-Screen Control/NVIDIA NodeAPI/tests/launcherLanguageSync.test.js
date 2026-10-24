'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '..', 'osc', 'mods', 'launcher-language-sync.js'), 'utf8');

function setup() {
  let run;
  const timers = [], intervals = [], warnings = [], listeners = {};
  const state = {language: 'en-US', remote: 'th-TH', status: 200, offline: false};
  const document = {documentElement: {lang: 'en'}};
  const rootScope = {$on(name, cb) {listeners[name] = cb;}};
  const translate = {
    use(lang) {
      if (typeof lang !== 'string') return state.language;
      state.language = lang;
      if (listeners.$translateChangeSuccess)
        listeners.$translateChangeSuccess({}, {language: lang});
      return Promise.resolve(lang);
    }
  };
  const angular = {module(name) {
    assert.equal(name, 'main');
    return {run(def) {run = def[def.length - 1];}};
  }};
  const win = {
    angular,
    fetch() {
      if (state.offline) return Promise.reject(new Error('offline'));
      return Promise.resolve({
        ok: state.status === 200,
        json: () => Promise.resolve({language: state.remote})
      });
    },
    setTimeout(fn, delay) {timers.push({fn, delay});},
    setInterval(fn, delay) {intervals.push({fn, delay});}
  };
  vm.runInNewContext(source, {window: win, document, angular, Promise}, {filename: 'launcher-language-sync.js'});
  run(translate, {info() {}, warn(...args) {warnings.push(args);}}, rootScope);
  async function tick(fn) {
    fn();
    for (let i = 0; i < 15; i++) await Promise.resolve();
  }
  return {timers, intervals, warnings, state, document, translate,
    first: () => tick(timers[0].fn), poll: () => tick(intervals[0].fn)};
}

test('OSC adopts Launcher choice and follows subsequent publications', async () => {
  const h = setup();
  assert.equal(h.timers[0].delay, 1500);
  assert.equal(h.intervals[0].delay, 3000);
  await h.first();
  assert.equal(h.state.language, 'th-TH');
  assert.equal(h.document.documentElement.lang, 'th');
  h.state.remote = 'fr-FR';
  await h.poll();
  assert.equal(h.state.language, 'fr-FR');
  assert.equal(h.document.documentElement.lang, 'fr');
});

test('manual OSC language stays selected while published Launcher language is unchanged', async () => {
  const h = setup();
  await h.first();
  await h.translate.use('ja-JP');
  assert.equal(h.document.documentElement.lang, 'ja');
  await h.poll();
  await h.poll();
  assert.equal(h.state.language, 'ja-JP');
  h.state.remote = 'de-DE';
  await h.poll();
  assert.equal(h.state.language, 'de-DE');
});

test('invalid locale and missing service do not override OSC', async () => {
  const h = setup();
  h.state.remote = '../bad';
  await h.first();
  assert.equal(h.state.language, 'en-US');
  h.state.status = 404;
  await h.poll();
  assert.equal(h.state.language, 'en-US');
  h.state.status = 200;
  h.state.offline = true;
  await h.poll();
  assert.equal(h.state.language, 'en-US');
  assert.ok(h.warnings.length > 0);
});
