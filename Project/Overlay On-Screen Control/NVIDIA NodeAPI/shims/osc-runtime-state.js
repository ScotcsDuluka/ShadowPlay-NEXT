'use strict';

module.exports = {
  // Global hotkey monitoring defaults ON; the keyboard-shortcut editor
  // explicitly suspends it via POST /ShadowPlay/v.1.0/Hotkey/Monitor.
  // A NodeAPI cold start must not strand Alt+Z in the disabled state.
  hotkeyMonitor: true,
  recordEnabled: false,
  recordRunning: false,
  irEnabled: false,
  irRunning: false,
  irSaving: false,
  broadcastEnabled: false,
  broadcastRunning: false,
  micPresent: true,
  ptt: false,
  webcamPresent: false,
  webcamEnable: false,
  desktopCapture: true,
  fpsIndicator: false,
  recordIndicator: false,
  coplay: false,
};
