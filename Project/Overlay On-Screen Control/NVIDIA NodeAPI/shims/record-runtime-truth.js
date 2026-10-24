'use strict';

// Capture worker authority beats NodeAPI's process-local manual-record cache.
// A timeout/unavailable response is UNKNOWN, never fabricated as idle/saved.
function interpretWorkerStatus(body) {
  if (!body || typeof body !== 'object' || body.availability !== 'Available' ||
      body.runtimeReady !== true || typeof body.recording !== 'boolean' ||
      body.startOutcomeUnresolved === true || body.stopOutcomeUnresolved === true ||
      typeof body.actualSessionBackend !== 'string') {
    return { verified: false, reason: 'capture_worker_truth_unavailable' };
  }
  if (body.recording && !body.actualSessionBackend) {
    return { verified: false, reason: 'active_backend_missing' };
  }
  if (!body.recording && body.actualSessionBackend) {
    return { verified: false, reason: 'idle_backend_conflict' };
  }
  return { verified: true, recording: body.recording };
}
function legacyCaptureView(recording, instantReplayRunning, broadcastRunning) {
  if (recording) return { captureMode: 0, recordingState: 1 };
  if (instantReplayRunning) return { captureMode: 1, recordingState: 1 };
  if (broadcastRunning) return { captureMode: 2, recordingState: 1 };
  return { captureMode: 0, recordingState: 0 };
}
module.exports = { interpretWorkerStatus, legacyCaptureView };
