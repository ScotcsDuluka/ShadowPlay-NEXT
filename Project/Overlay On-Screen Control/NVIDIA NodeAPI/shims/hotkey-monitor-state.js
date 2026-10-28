'use strict';

function createMonitorState(state) {
  let session = null, sequence = -1, sessionStartedAt = -1;
  function setEnabled(enable) {
    if (state.hotkeyMonitor !== enable)
      state.hotkeyMonitorGeneration = (state.hotkeyMonitorGeneration || 0) + 1;
    state.hotkeyMonitor = enable;
  }
  return {
    update(body) {
      if (!body || typeof body.enable !== 'boolean') return 400;
      if (body.pageSession !== undefined) {
        if (typeof body.pageSession !== 'string' || !/^[A-Za-z0-9_-]{1,80}$/.test(body.pageSession) ||
            !Number.isSafeInteger(body.sequence) || body.sequence < 0) return 400;
        if (session !== body.pageSession) {
          if (session !== null && body.pageStart !== true) return 409;
          if (!Number.isSafeInteger(body.pageStartedAtUnixMs) ||
              body.pageStartedAtUnixMs < sessionStartedAt ||
              (body.pageStartedAtUnixMs === sessionStartedAt && body.pagePreviousSession !== session) ||
              body.pageStartedAtUnixMs > Date.now() + 1000)
            return 409;
          session = body.pageSession;
          sessionStartedAt = body.pageStartedAtUnixMs;
          sequence = -1;
        }
        if (body.sequence <= sequence) return 409;
        sequence = body.sequence;
      } else if (session !== null) {
        // Once the page supports generations, old page requests cannot undo it.
        return 409;
      }
      setEnabled(body.enable);
      return 200;
    }
  };
}

const state = require('./osc-runtime-state');
const monitorState = createMonitorState(state);
module.exports = { createMonitorState, monitorState };
