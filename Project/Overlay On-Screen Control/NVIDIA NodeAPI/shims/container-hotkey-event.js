'use strict';

// NvContainer reports OpenShare on the generic Hotkey channel. OSC's actual
// visibility owner is the native host at 127.0.0.1:59013, not the CEF page.
// The native /toggle route opens even when CEF is still at #/base.
const http = require('http');
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
function toggleNativeOsc(port = 59013) {
  return new Promise(resolve => {
    const req = http.request({
      hostname: '127.0.0.1', port, path: '/toggle', method: 'POST',
      agent: false, timeout: 1900
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

module.exports = { normalizeContainerHotkeyEvent, toggleNativeOsc };
