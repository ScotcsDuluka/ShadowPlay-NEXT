'use strict';

// A UI request and a change of recording state do not prove that a file exists.
// The bundled OSC maps Record/Enable(false) to "Recording has been saved", so
// this event is reserved for a confirmed successful session completion.
const ENABLE = '/ShadowPlay/v.1.0/Record/Enable';
const RUNNING = '/ShadowPlay/v.1.0/Record/Running';

function desiredEvents() {
  // Never tell the UI "started" / "saved" from a desired-state POST.
  return [];
}
function actualEvents(previousRunning, body) {
  if (!body || typeof body.running !== 'boolean' ||
      previousRunning === body.running) return [];
  if (body.running) {
    return [
      { name: ENABLE, data: { status: true } },
      { name: RUNNING, data: { running: true } }
    ];
  }
  const events = [{ name: RUNNING, data: { running: false } }];
  // A save event is valid only when NvCapture reports an actual finalized
  // output and SessionResult.Pass. Do not infer success from a Stop request.
  if (body.recordingSaved === true && typeof body.outputPath === 'string' &&
      body.outputPath.length > 0 && body.outputPath.length < 4096) {
    events.unshift({ name: ENABLE, data: { status: false } });
  }
  return events;
}
module.exports = { desiredEvents, actualEvents };
