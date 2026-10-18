'use strict'
// store.js — sectioned JSON state persistence → Backend/data/state.json.
//
// Pattern lifted from DulukaFloor.js (proven 2026-09-19/22): POST bodies
// are merged per section so every settings screen survives backend
// restarts. Atomic write (tmp + rename) — a kill mid-write can never
// corrupt the store.

const fs = require('fs');
const path = require('path');

const STORE_VERSION = 'nvsp-backend-1.0';

function createStore(dataDir) {
    const filePath = path.join(dataDir, 'state.json');

    function load() {
        try {
            const parsed = JSON.parse(fs.readFileSync(filePath, 'utf8'));
            if (parsed && typeof parsed === 'object' && parsed.sections) return parsed;
        } catch (err) { /* corrupt/absent store reads as empty */ }
        return { version: STORE_VERSION, sections: {} };
    }

    let store = load();

    function save() {
        try {
            store.version = STORE_VERSION;
            store.savedAt = new Date().toISOString();
            fs.mkdirSync(path.dirname(filePath), { recursive: true });
            const tmp = filePath + '.' + process.pid + '.tmp';
            fs.writeFileSync(tmp, JSON.stringify(store, null, 2));
            fs.renameSync(tmp, filePath);
            return true;
        } catch (err) {
            return false;
        }
    }

    function getSection(name) {
        if (!store.sections[name]) store.sections[name] = {};
        return store.sections[name];
    }

    // merge a plain object into a section (verbatim keys — POST body trust)
    function storeSection(name, obj) {
        if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return false;
        const sec = getSection(name);
        for (const k of Object.keys(obj)) sec[k] = obj[k];
        save();
        return true;
    }

    function replaceSection(name, obj) {
        if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return false;
        store.sections[name] = obj;
        save();
        return true;
    }

    function clearSection(name) {
        delete store.sections[name];
        save();
    }

    return {
        getSection: getSection,
        storeSection: storeSection,
        replaceSection: replaceSection,
        clearSection: clearSection,
        filePath: function () { return filePath; },
        flush: save
    };
}

module.exports = { createStore };
