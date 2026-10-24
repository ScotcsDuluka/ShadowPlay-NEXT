'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const truth = require('../shims/record-runtime-truth');

function live(overrides) {
  return { availability: 'Available', runtimeReady: true, recording: false,
    actualSessionBackend: '', startOutcomeUnresolved: false,
    stopOutcomeUnresolved: false, ...overrides };
}
test('authoritative worker truth accepts healthy idle and active backend', () => {
  assert.deepEqual(truth.interpretWorkerStatus(live()),
    { verified: true, recording: false });
  assert.deepEqual(truth.interpretWorkerStatus(live({
    recording: true, actualSessionBackend: 'Duluka'
  })), { verified: true, recording: true });
});
test('missing/ambiguous/failed worker state never fabricates idle', () => {
  const samples = [null, {}, [], 'idle', live({ availability: 'Unavailable' }),
    live({ runtimeReady: false }), live({ recording: null }),
    live({ startOutcomeUnresolved: true }), live({ stopOutcomeUnresolved: true }),
    live({ recording: true, actualSessionBackend: '' }),
    live({ recording: false, actualSessionBackend: 'Duluka' })];
  for (const sample of samples) {
    assert.equal(truth.interpretWorkerStatus(sample).verified, false,
      JSON.stringify(sample));
  }
});
test('legacy view keeps Capture native and replay/broadcast distinct', () => {
  assert.deepEqual(truth.legacyCaptureView(true, true, true),
    { captureMode: 0, recordingState: 1 });
  assert.deepEqual(truth.legacyCaptureView(false, true, true),
    { captureMode: 1, recordingState: 1 });
  assert.deepEqual(truth.legacyCaptureView(false, false, true),
    { captureMode: 2, recordingState: 1 });
  assert.deepEqual(truth.legacyCaptureView(false, false, false),
    { captureMode: 0, recordingState: 0 });
});
