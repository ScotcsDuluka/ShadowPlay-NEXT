'use strict';

// capture-state-sync.js — the OSC UI's live capture truth.
//
// Responsibilities (all behind the explicit runtime-enabled flag):
//   1. Poll the NvContainer gateway status on a bounded interval and push
//      /DulukaCapture/v.1.0/state over the existing socket.io channel when
//      anything a UI renders actually changes (sync push).
//   2. Emit the current payload once for every new socket client, so a
//      freshly opened overlay shows real state without waiting a poll.
//   3. OPTIONAL explicit command bridge (NV_CAPTURE_DESIRED_BRIDGE=1).
//      Each request performs a fresh verified Container status check and
//      permits one command in flight. Polling never issues commands.
//      Default OFF: legacy workers may still poll the /Duluka plane.
//
// The gateway (NvContainer :59012) stays the single data broker: this module
// never talks to NvCapture directly and never fabricates state. Unknown or
// unavailable truth is pushed as unknown, never as idle/recording.
const http = require('http');

function interpretWorkerStatus(body, requireContainerMode) {
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
  if (requireContainerMode && body.recordCommandMode !== 'container') {
    return { verified: false, reason: 'capture_record_command_mode_unverified' };
  }
  return { verified: true, recording: body.recording };
}

module.exports = function installCaptureStateSync(app, options) {
  options = options || {};
  const enabled = options.enabled !== undefined
    ? options.enabled === true
    : process.env.NV_CAPTURE_RUNTIME_ENABLED === '1';
  const configured = Number(options.port || process.env.NV_CONTAINER_CONTROL_API_PORT || 59012);
  const portValid = Number.isInteger(configured) && configured >= 1 && configured <= 65535;
  const pollIntervalMs = Math.max(1000, Number(options.pollIntervalMs) || 2000);
  const desiredBridge = options.desiredBridge !== undefined
    ? options.desiredBridge === true
    : process.env.NV_CAPTURE_DESIRED_BRIDGE === '1';
  const socket = options.socket;
  const CHANNEL = options.channel || '/DulukaCapture/v.1.0/state';

  const handle = {
    timer: null,
    stopped: false,
    last: unknownPayload('capture_state_sync_disabled'),
    lastKey: '',
    // Raw last validated /v1/capture/status body, used for worker truth.
    lastRaw: null,
    commandInFlight: false,
    pollCount: 0
  };

  function unknownPayload(reason) {
    return {
      schemaVersion: 1, source: 'NvCapture',
      enabled: enabled, ready: false, recording: null, stateKnown: false,
      workerReady: false, actualSessionBackend: '', actualRunningEngine: '',
      lifecycleState: 'Unknown', busy: null, isCapturing: null,
      runtimeTruth: null, reason: reason,
      recordedAt: new Date().toISOString()
    };
  }

  function payloadFrom(body) {
    return {
      schemaVersion: 1, source: 'NvCapture',
      enabled: body.runtimeEnabled,
      ready: body.runtimeReady === true,
      recording: body.recording,
      stateKnown: typeof body.recording === 'boolean',
      workerReady: body.workerReady === true,
      actualSessionBackend: typeof body.actualSessionBackend === 'string' ? body.actualSessionBackend : '',
      actualRunningEngine: typeof body.actualRunningEngine === 'string' ? body.actualRunningEngine : '',
      lifecycleState: typeof body.lifecycleState === 'string' ? body.lifecycleState : 'Unknown',
      busy: typeof body.busy === 'boolean' ? body.busy : body.recording,
      isCapturing: typeof body.isCapturing === 'boolean' ? body.isCapturing : null,
      runtimeTruth: body.workerRuntime || null,
      recordedAt: new Date().toISOString()
    };
  }

  function stateKey(p) {
    // recordedAt changes on every poll; all other published fields may be
    // meaningful to a client, including progress, verification and reasons.
    // Do not suppress them merely because the lifecycle label stayed equal.
    const snapshot = Object.assign({}, p);
    delete snapshot.recordedAt;
    return JSON.stringify(snapshot);
  }

  function publish(payload) {
    handle.last = payload;
    const key = stateKey(payload);
    const changed = key !== handle.lastKey;
    handle.lastKey = key;
    if (changed && socket && typeof socket.emitChannel === 'function') {
      try { socket.emitChannel(CHANNEL, payload); } catch (_e) { /* keep polling */ }
    }
    return changed;
  }

  function fetchStatus(done) {
    let settled = false;
    const finish = function (body, reason) {
      if (settled) return;
      settled = true;
      done(body, reason);
    };
    const request = http.get({
      hostname: '127.0.0.1', port: configured,
      // Container checks health, state and engine evidence sequentially,
      // each with a bounded worker timeout.
      path: '/v1/capture/status', timeout: 10000
    }, function (reply) {
      let length = 0;
      const chunks = [];
      reply.on('data', function (c) {
        length += c.length;
        if (length > 131072) reply.destroy(new Error('capture_status_too_large'));
        else chunks.push(c);
      });
      reply.on('end', function () {
        if (reply.statusCode !== 200) return finish(null, 'CAPTURE_RUNTIME_UNAVAILABLE');
        try { finish(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
        catch (_e) { finish(null, 'CAPTURE_RUNTIME_INVALID_RESPONSE'); }
      });
      reply.on('error', function () { finish(null, 'CAPTURE_RUNTIME_STREAM_FAILED'); });
    });
    request.on('timeout', function () { request.destroy(new Error('capture_status_timeout')); });
    request.on('error', function () { finish(null, 'CAPTURE_RUNTIME_UNAVAILABLE'); });
  }

  function sendRecordCommand(command, done) {
    const payload = JSON.stringify({ confirmed: true });
    let settled = false;
    function finish(result, status) {
      if (settled) return;
      settled = true;
      done(result, status);
    }
    const outbound = http.request({
      hostname: '127.0.0.1', port: configured, method: 'POST',
      path: '/v1/capture/record/' + command, timeout: command === 'stop' ? 45000 : 35000,
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) }
    }, function (reply) {
      let length = 0;
      const chunks = [];
      reply.on('data', function (c) {
        length += c.length;
        if (length > 65536) reply.destroy(new Error('capture_record_response_too_large'));
        else chunks.push(c);
      });
      reply.on('end', function () {
        let parsed = null;
        try { parsed = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch (_e) { }
        finish(parsed && typeof parsed === 'object' ? parsed : null, reply.statusCode);
      });
      reply.on('error', function () { finish(null, 0); });
      reply.on('aborted', function () { finish(null, 0); });
    });
    outbound.on('timeout', function () { outbound.destroy(new Error('capture_record_command_timeout')); });
    outbound.on('error', function () { finish(null, 0); });
    outbound.end(payload);
  }

  function ingest(body) {
    if (handle.stopped) return;
    if (!body || typeof body !== 'object' || Array.isArray(body) ||
        body.schemaVersion !== 1 || body.source !== 'NvCapture' ||
        typeof body.runtimeEnabled !== 'boolean' ||
        typeof body.workerReady !== 'boolean' ||
        typeof body.runtimeReady !== 'boolean' ||
        !(typeof body.recording === 'boolean' || body.recording === null)) {
      handle.lastRaw = null;
      publish(unknownPayload('capture_worker_truth_unavailable'));
      return;
    }
    handle.lastRaw = body;
    publish(payloadFrom(body));
  }

  function poll(done) {
    if (handle.stopped) return;
    handle.pollCount += 1;
    fetchStatus(function (body) {
      if (handle.stopped) return;
      ingest(body);
      if (done) { try { done(); } catch (_e) { } }
      // Polling observes truth only. Commands require an explicit request;
      // a persisted desired value must never become an implicit start.
    });
  }

  // Desired-state feed: the page drives /ShadowPlay/v.1.0/Record/Enable.
  app.get('/DulukaCapture/v.1.0/state', function (_req, res) {
    res.status(200).json(handle.last);
  });

  const api = {
    channel: CHANNEL,
    currentState: function () { return handle.last; },
    pollCount: function () { return handle.pollCount; },
    pollNow: function (done) { if (enabled && portValid && !handle.stopped) poll(done); },
    requestRecord: function (desired, done) {
      if (!enabled || !desiredBridge || !portValid || handle.stopped) {
        return done(503, { error: 'capture_record_truth_unavailable' });
      }
      if (handle.commandInFlight) return done(409, { error: 'capture_command_in_flight' });
      handle.commandInFlight = true;
      fetchStatus(function (body) {
        if (handle.stopped) {
          handle.commandInFlight = false;
          return done(503, { error: 'capture_record_truth_unavailable' });
        }
        ingest(body);
        // A mismatched worker must never start, but an active legacy session
        // must remain stoppable through Container for safety.
        if (!interpretWorkerStatus(handle.lastRaw, desired === true).verified) {
          handle.commandInFlight = false;
          return done(503, { error: 'capture_record_truth_unavailable' });
        }
        const command = desired ? 'start' : 'stop';
        if (handle.lastRaw.recording === desired) {
          handle.commandInFlight = false;
          return done(200, { accepted: true, unchanged: true });
        }
        sendRecordCommand(command, function (result, status) {
          handle.commandInFlight = false;
          if (!status || !result) {
            return done(504, { error: 'capture_record_' + command + '_outcome_unknown', outcomeUnknown: true });
          }
          done(status, result);
        });
      });
    },
    bridgeEnabled: function () { return enabled && desiredBridge && portValid && !handle.stopped; },
    commandInFlight: function () { return handle.commandInFlight; },
    verifiedRecordState: function () { return interpretWorkerStatus(handle.lastRaw, false); },
    stop: function () {
      handle.stopped = true;
      if (handle.timer) { clearInterval(handle.timer); handle.timer = null; }
    }
  };

  if (enabled && portValid) {
    if (socket && socket.io && typeof socket.io.on === 'function') {
      socket.io.on('connection', function () {
        if (socket && typeof socket.emitChannel === 'function') {
          try { socket.emitChannel(CHANNEL, handle.last); } catch (_e) { }
        }
      });
    }
    handle.timer = setInterval(poll, pollIntervalMs);
    if (handle.timer.unref) handle.timer.unref();
    poll();
  }

  // Route-floor (the page's desired-state authority) reports record edges.
  // Body reader works on both backend lineages (global req.body parser or
  // per-route raw reading — never consume a stream twice).
  app.post('/DulukaCapture/v.1.0/desired-record', function (req, res) {
    function handle(body) {
      if (!body || typeof body !== 'object' || typeof body.status !== 'boolean') {
        return res.status(400).json({ accepted: false, error: 'desired_record_requires_boolean_status' });
      }
      api.requestRecord(body.status, function (status, result) {
        res.status(status).json(result);
      });
    }
    if (req.body !== undefined) return handle(req.body);
    let raw = '';
    req.on('data', function (c) { raw += c; if (raw.length > 262144) req.destroy(); });
    req.on('end', function () {
      let parsed;
      try { parsed = raw ? JSON.parse(raw) : {}; } catch (_e) { parsed = null; }
      handle(parsed);
    });
    req.on('error', function () { handle(null); });
  });

  return api;
};

module.exports.interpretWorkerStatus = interpretWorkerStatus;
