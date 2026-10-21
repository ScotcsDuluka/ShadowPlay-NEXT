'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const oscRoot = path.join(__dirname, '..', 'osc');
const adapter = fs.readFileSync(path.join(oscRoot, 'mods', 'notifier', 'Ontology-Update-Adapter.js'), 'utf8');
const STORAGE_KEY = 'nv-ontology-update-state-v1';

function harness(savedStorage, translations) {
    const storage = savedStorage || new Map();
    const notifications = [];
    const timers = [];
    const intervals = [];
    const handlers = {};
    const active = new Set();
    const warnings = [];
    let payload = { catalogVersion: '1.2.6', gameCount: 6 };
    let blocked = false;
    let shouldFail = false;
    let fn;

    const win = {
        angular: { module: function () { return { run: function (a) { fn = a[a.length - 1]; } }; } },
        Notifier: {
            show: function (message, opts) {
                notifications.push({ message, opts });
                if (!blocked) active.add(opts.group);
            },
            hasActiveGroupPrefix: function (prefix) {
                return Array.from(active).some(function (group) { return group.indexOf(prefix) === 0; });
            }
        },
        localStorage: {
            getItem: function (key) { return storage.has(key) ? storage.get(key) : null; },
            setItem: function (key, value) { storage.set(key, String(value)); }
        },
        setTimeout: function (cb, delay) { timers.push({ cb, delay }); return timers.length; },
        clearTimeout: function () {},
        setInterval: function (cb, delay) { intervals.push({ cb, delay }); return intervals.length; }
    };
    const fakeFetch = function () {
        if (shouldFail) return Promise.reject(new Error('offline'));
        return Promise.resolve({
            ok: true,
            json: function () { return Promise.resolve(payload); }
        });
    };
    vm.runInNewContext(adapter, {
        window: win,
        angular: win.angular,
        fetch: fakeFetch,
        AbortController: AbortController,
        Number: Number,
        JSON: JSON,
        Math: Math,
        isFinite: isFinite
    }, { filename: 'Ontology-Update-Adapter.js' });
    fn(
        { on: function (name, cb) { handlers[name] = cb; } },
        { info: function () {}, warn: function () { warnings.push(Array.from(arguments)); } },
        function (name) {
            assert.equal(name, 'translate');
            return function (key, args) {
                const phrase = translations && translations[key] || key;
                return phrase.replace(/\{\{(\w+)\}\}/g, function (_, token) {
                    return args && args[token] !== undefined ? String(args[token]) : '';
                });
            };
        }
    );

    async function tick(callback) {
        callback();
        for (let i = 0; i < 10; i++) await Promise.resolve();
    }
    return {
        storage, notifications, handlers, warnings,
        setPayload: function (value) { payload = value; },
        setBlocked: function (value) { blocked = value; },
        setOffline: function (value) { shouldFail = value; },
        firstPoll: function () { return tick(timers.find(function (x) { return x.delay === 3000; }).cb); },
        poll: function () { return tick(intervals[0].cb); }
    };
}

test('initial public observation establishes baseline; next version shows exactly once', async () => {
    const h = harness();
    await h.firstPoll();
    assert.equal(h.notifications.length, 0);
    assert.equal(JSON.parse(h.storage.get(STORAGE_KEY)).lastSeenVersion, '1.2.6');
    h.setPayload({ catalogVersion: '1.2.7', gameCount: 7 });
    await h.poll();
    assert.equal(h.notifications.length, 1);
    assert.equal(h.notifications[0].message, 'nv-ontology catalog update available');
    assert.match(h.notifications[0].opts.subtext, /Catalog v1\.2\.6.*v1\.2\.7 · 7 games/);
    assert.equal(h.notifications[0].opts.priority, true);
    assert.equal(JSON.parse(h.storage.get(STORAGE_KEY)).lastNotifiedVersion, '1.2.7');
    await h.poll();
    assert.equal(h.notifications.length, 1);
    const reopened = harness(h.storage);
    await reopened.firstPoll();
    assert.equal(reopened.notifications.length, 0);
});

test('older local GameWatcher catalog triggers a notification on first public check', async () => {
    const h = harness();
    h.handlers['nv-ontology-local-catalog-version']('1.2.5');
    await h.firstPoll();
    assert.equal(h.notifications.length, 1);
    assert.match(h.notifications[0].opts.subtext, /1\.2\.5.*1\.2\.6/);
});

test('blocked notifier slot is retried without losing update', async () => {
    const h = harness();
    await h.firstPoll();
    h.setPayload({ catalogVersion: '1.2.7', gameCount: 7 });
    h.setBlocked(true);
    await h.poll();
    assert.equal(JSON.parse(h.storage.get(STORAGE_KEY)).lastNotifiedVersion, '');
    h.setBlocked(false);
    await h.poll();
    assert.equal(JSON.parse(h.storage.get(STORAGE_KEY)).lastNotifiedVersion, '1.2.7');
});

test('invalid version, rollback and offline responses do not create updates', async () => {
    const h = harness();
    await h.firstPoll();
    h.setPayload({ catalogVersion: '1.2.5', gameCount: 6 });
    await h.poll();
    h.setPayload({ catalogVersion: 'evil version', gameCount: 6 });
    await h.poll();
    h.setOffline(true);
    await h.poll();
    assert.equal(h.notifications.length, 0);
    assert.ok(h.warnings.length > 0);
});

test('OSC adapter forwards validated local catalog version and index loads update adapter', () => {
    const oscAdapter = fs.readFileSync(path.join(oscRoot, 'mods', 'notifier', 'OSC-Adapter.js'), 'utf8');
    const index = fs.readFileSync(path.join(oscRoot, 'index.html'), 'utf8');
    assert.match(oscAdapter, /eventAggregator\.trigger\('nv-ontology-local-catalog-version', event\.status\.catalogVersion\)/);
    assert.match(index, /\/mods\/notifier\/Ontology-Update-Adapter\.js/);
});

function readLocale(filename) {
    const contents = fs.readFileSync(path.join(oscRoot, 'l10n', filename), 'utf8');
    return JSON.parse(contents.replace(/^\uFEFF/, ''));
}

test('English is the primary catalog update language', async () => {
    const english = readLocale('en-US.json');
    const h = harness(new Map(), english);
    await h.firstPoll();
    h.setPayload({ catalogVersion: '1.2.7', gameCount: 7 });
    await h.poll();
    assert.equal(h.notifications[0].message, 'nv-ontology catalog update available');
    assert.equal(h.notifications[0].opts.subtext, 'Catalog v1.2.6 → v1.2.7 · 7 games');
});

test('Thai locale translates title, catalog label and game count', async () => {
    const thai = readLocale('th-TH.json');
    const h = harness(new Map(), thai);
    await h.firstPoll();
    h.setPayload({ catalogVersion: '1.2.7', gameCount: 7 });
    await h.poll();
    assert.equal(h.notifications[0].message, 'มีอัปเดตแค็ตตาล็อก nv-ontology');
    assert.equal(h.notifications[0].opts.subtext, 'แค็ตตาล็อก v1.2.6 → v1.2.7 · 7 เกม');
});
