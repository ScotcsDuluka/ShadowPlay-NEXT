// generic-addon.js — factory for native-addon shims. Every function the
// genuine JS layer calls arrives as `api.Fn(doReply, ...args)` (callback
// style); the default shim answers `callback(null, {})` so unknown surface
// degrades to empty-200 (same as our previous stub routes) instead of
// crashing. Specific functions get real implementations by passing a map.
'use strict';

// data-floor: shape จริงให้ route ที่หน้าอ่าน (แทน {} เปล่า) — อ้างอิง docs/osc/real-floor
const { FLOOR } = require('./data-floor.js');

// DULUKA_RECORD=1 → RECORDER MODE: load the GENUINE native addon and log every
// call + genuine reply to Logs/floor-real.jsonl (the "pull real data" pass —
// run with the genuine nvcontainer service up, drive every OSC page, then
// bake the captures into FLOOR permanently). Falls back to stub mode if the
// genuine module is missing or fails to load.
const RECORD = process.env.DULUKA_RECORD === '1' && process.env.NV_CONTAINER_TEST_MODE !== '1';
const fs = RECORD ? require('fs') : null;
const path = RECORD ? require('path') : null;

function makeAddon(specific, log, genuinePath) {
  let genuine = null;
  if (RECORD && genuinePath) {
    try {
      genuine = require(genuinePath);
      try {
        console.log('[shim:record] genuine addon loaded: ' + genuinePath);
      } catch (e) {}
    } catch (e) {
      try { console.log('[shim:record] genuine load failed (' + e.message + ') — stub mode'); } catch (e2) {}
    }
  }
  let recStream = null;
  function record(fn, args, err, data) {
    try {
      if (!recStream) {
        const dir = path.join(__dirname, '..', 'Logs');
        try { fs.mkdirSync(dir, { recursive: true }); } catch (e) {}
        recStream = fs.createWriteStream(path.join(dir, 'floor-real.jsonl'), { flags: 'a' });
      }
      recStream.write(JSON.stringify({
        ts: Date.now(), fn: fn,
        args: Array.prototype.slice.call(args, 1).map(function (a) {
          return typeof a === 'function' ? '[callback]' : a;
        }),
        err: err ? String(err.message || err) : null,
        data: data === undefined ? null : data,
      }) + '\n');
    } catch (e) { /* never break the genuine call path */ }
  }
  const target = {};
  return new Proxy(target, {
    get(t, name) {
      if (name in t) return t[name];
      if (specific && typeof specific[name] === 'function') {
        return specific[name];
      }
      return function (cb) {
        // *Callback registrations take a REAL callback to invoke later —
        // standalone: keep nothing, never call it now (calling with null
        // payloads crashed ApplicationChangedCallback et al).
        if (/Callback$/.test(String(name))) {
          // recorder: pass callback registrations through to the genuine
          // module so real events flow (we do not log the cb itself)
          if (genuine && typeof genuine[name] === 'function') {
            try { return genuine[name](cb); } catch (e) {}
          }
          return;
        }
        const key = String(name);
        // recorder: delegate to genuine, intercept reply
        if (genuine && typeof genuine[key] === 'function') {
          var callArgs = Array.prototype.slice.call(arguments, 1);
          try {
            return genuine[key].apply(genuine, [function (err, data) {
              record(key, [null].concat(callArgs), err, data);
              if (typeof cb === 'function') cb(err, data);
            }].concat(callArgs));
          } catch (e) {
            if (typeof cb === 'function') cb(e);
            return;
          }
        }
        // data-floor: ถ้ามี shape จริง → ตอบด้วยของจริง (clone กัน mutate)
        if (Object.prototype.hasOwnProperty.call(FLOOR, key)) {
          if (log) {
            try { log('[shim] ' + key + ' -> floor'); } catch (e) {}
          }
          const val = JSON.parse(JSON.stringify(FLOOR[key]));
          if (typeof cb === 'function') cb(null, val);
          return val;
        }
        if (log) {
          try { log('[shim] ' + key + ' -> {}'); } catch (e) {}
        }
        if (RECORD) record(key, arguments, { message: 'no-genuine' }, undefined);
        if (typeof cb === 'function') cb(null, {});
        return {};
      };
    },
  });
}

module.exports = makeAddon;
