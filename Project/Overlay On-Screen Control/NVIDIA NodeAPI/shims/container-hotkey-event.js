'use strict';

// NvContainer reports OpenShare on the generic Hotkey channel. OSC's actual
// visibility owner is the native host at 127.0.0.1:59013, not the CEF page.
// The native /toggle route opens even when CEF is still at #/base.
const http = require('http');
const crypto = require('crypto');
const state = require('./osc-runtime-state');
const HOTKEY_CHANNEL = '/ShadowPlay/v.1.0/Hotkey';
const WINDOW_STATE_CHANNEL = '/ShadowPlay/v.1.0/WindowState';

function normalizeContainerHotkeyEvent(event) {
  if (!event || event.name !== HOTKEY_CHANNEL || !event.data ||
      event.data.hotkeyId !== 'OSC') return event;

  const name = typeof event.data.hotKeyName === 'string'
    ? event.data.hotKeyName.toLowerCase() : '';
  if (name !== 'openshare' && name !== 'overlaytoggle') return event;

  return {
    name: WINDOW_STATE_CHANNEL,
    data: { windowMsg: 'overlayToggle' }
  };
}

// Return true only once the native host acknowledges /toggle.
// No Socket.IO WindowState emission here: host WM_OSC_VIS owns visibility,
// and QueueOscPageAction updates the CEF route without a second toggle.
function toggleNativeOsc(port = 59013, intent = {}) {
  return new Promise(resolve => {
    const req = http.request({
      hostname: '127.0.0.1', port, path: '/toggle', method: 'POST',
      agent: false, timeout: 1000,
      headers: {
        'X-OSC-Request-Id': intent.requestId || crypto.randomBytes(16).toString('hex'),
        'X-OSC-Created-At': String(intent.createdAtUnixMs || Date.now())
      }
    }, response => {
      response.resume();
      response.on('end', () => resolve(response.statusCode === 200));
      response.on('error', () => resolve(false));
    });
    req.on('timeout', () => req.destroy(new Error('OSC native toggle timed out')));
    req.on('error', () => resolve(false));
    req.end();
  });
}

// One queue owns native intents, including events from concurrent HTTP callers.
// Do not replay timed-out toggles: a timeout can mean the host already applied it.
function createNativeOscDispatcher(options = {}) {
  const clock = options.clock || Date.now;
  const log = options.log || (() => {});
  const send = options.send || (intent => toggleNativeOsc(options.port || 59013, intent));
  const monitor = options.monitor || (() => ({
    enabled: state.hotkeyMonitor !== false, generation: state.hotkeyMonitorGeneration || 0
  }));
  let tail = Promise.resolve(), pendingCount = 0;
  const requests = new Map();
  return function dispatch(data = {}) {
    const suppliedId = data.requestId;
    const requestId = typeof suppliedId === 'string' && /^[A-Za-z0-9_.:-]{1,80}$/.test(suppliedId)
      ? suppliedId : crypto.randomBytes(16).toString('hex');
    if (requests.has(requestId)) return requests.get(requestId);
    if (pendingCount >= 64) {
      log('[hotkey-native] id=' + requestId + ' outcome=queue-full');
      return Promise.resolve(false);
    }
    const createdAtUnixMs = data.createdAtUnixMs === undefined ? clock() : data.createdAtUnixMs;
    const initial = monitor();
    const current = () => {
      const now = clock();
      const latest = monitor();
      return initial.enabled && latest.enabled && initial.generation === latest.generation &&
        Number.isSafeInteger(createdAtUnixMs) && now - createdAtUnixMs <= 2000 &&
        createdAtUnixMs - now <= 1000;
    };
    pendingCount++;
    let attempted = false;
    const task = tail.then(() => {
      if (!current()) return false;
      attempted = true;
      return send({ requestId, createdAtUnixMs });
    }).then(ok => ok === true, () => false).then(ok => {
      pendingCount--;
      log('[hotkey-native] id=' + requestId + ' outcome=' +
        (ok ? 'acknowledged' : attempted ? 'unavailable-or-timeout' : 'stale-or-suppressed'));
      return ok;
    });
    tail = task.then(() => undefined);
    requests.set(requestId, task);
    if (requests.size > 128) requests.delete(requests.keys().next().value);
    return task;
  };
}

const dispatchNativeOscToggle = createNativeOscDispatcher({log: message => console.log(message)});
module.exports = { normalizeContainerHotkeyEvent, toggleNativeOsc,
  createNativeOscDispatcher, dispatchNativeOscToggle };
