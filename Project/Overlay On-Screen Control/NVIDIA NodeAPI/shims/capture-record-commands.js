'use strict';

// capture-record-commands.js — the ONLY NodeAPI surface that may ask for a
// recording to start or stop. Chain of command (SPN-ADR control plane):
//
//   OSC UI → NodeAPI (this shim) → NvContainer :59012 /v1/capture/record/*
//          → NvCapture :59077 (the recording owner)
//
// Rules enforced here:
//   - disabled unless the capture runtime flag is explicitly on;
//   - request body must be exactly {"confirmed":true} (per-display and
//     screenshot commands share this Container rule);
//   - the Container gateway itself remains the authorization authority
//     (captureRuntime.allowRecordCommands) — a 403 from upstream is passed
//     through untouched;
//   - the upstream answer is relayed verbatim. This shim never invents
//     started/stopped/saved: a timeout is reported as outcome-unknown.
const http = require('http');

const PATHS = {
  '/DulukaCapture/v.1.0/record/start': '/v1/capture/record/start',
  '/DulukaCapture/v.1.0/record/stop': '/v1/capture/record/stop'
};

// Body reader for both backend lineages: the source backend sets req.body
// through a global parser, the deployed one parses per-route. Never read a
// stream twice — when req.body exists the stream is already consumed.
function readJsonBody(req, done) {
  if (req.body !== undefined) return done(req.body);
  let size = 0;
  const chunks = [];
  let settled = false;
  req.on('data', function (c) {
    size += c.length;
    if (size > 1048576) {
      if (!settled) { settled = true; done(undefined); }
      req.destroy();
      return;
    }
    chunks.push(c);
  });
  req.on('end', function () {
    if (settled) return;
    settled = true;
    const raw = Buffer.concat(chunks).toString('utf8');
    let parsed;
    try { parsed = raw ? JSON.parse(raw) : {}; } catch (_e) { parsed = undefined; }
    done(parsed);
  });
  req.on('error', function () {
    if (!settled) { settled = true; done(undefined); }
  });
}

module.exports = function registerCaptureRecordCommands(app, options) {
  options = options || {};

  const enabled = options.enabled !== undefined
    ? options.enabled === true
    : process.env.NV_CAPTURE_RUNTIME_ENABLED === '1';
  const configured = Number(options.port || process.env.NV_CONTAINER_CONTROL_API_PORT || 59012);
  const portValid = Number.isInteger(configured) && configured >= 1 && configured <= 65535;

  function reject(res, status, code, message) {
    res.status(status).json({ relayed: false, error: code, message: message || code });
  }

  Object.keys(PATHS).forEach(function (localPath) {
    const upstreamPath = PATHS[localPath];
    app.post(localPath, function (req, res) {
      if (!enabled) {
        return reject(res, 403, 'capture_record_commands_disabled',
          'Set NV_CAPTURE_RUNTIME_ENABLED=1 and Container captureRuntime.allowRecordCommands=true to enable recording commands.');
      }
      if (!portValid) {
        return reject(res, 503, 'capture_gateway_invalid_port');
      }
      readJsonBody(req, function (body) {
        if (!body || typeof body !== 'object' || Array.isArray(body) ||
            body.confirmed !== true || Object.keys(body).length !== 1) {
          return reject(res, 400, 'capture_record_confirmation_required',
            'Only {"confirmed":true} is permitted.');
        }
        dispatch(req, res, upstreamPath);
      });
    });
  });

  function dispatch(req, res, upstreamPath) {
    const payload = JSON.stringify({ confirmed: true });
    let connected = false;
    let settled = false;
    function fail(code, status) {
      if (settled) return;
      settled = true;
      reject(res, status, code);
    }
    function unknown() {
      fail(upstreamPath.endsWith('/stop') ? 'capture_record_stop_outcome_unknown' :
        'capture_record_start_outcome_unknown', 504);
    }
    const outbound = http.request({
      hostname: '127.0.0.1',
      port: configured,
      method: 'POST',
      path: upstreamPath,
      // Container preflight can take three bounded reads before its 19s
      // worker-start budget; stop has one read plus a 35s worker budget.
      timeout: options.timeoutMs || (upstreamPath.endsWith('/stop') ? 45000 : 35000),
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) }
    }, function (upstream) {
      let length = 0;
      const chunks = [];
      upstream.on('data', function (chunk) {
        length += chunk.length;
        if (length > 65536) {
          upstream.destroy(new Error('capture_record_response_too_large'));
          return;
        }
        chunks.push(chunk);
      });
      upstream.on('end', function () {
        if (settled) return;
        settled = true;
        const raw = Buffer.concat(chunks).toString('utf8');
        let parsed;
        try { parsed = JSON.parse(raw); } catch (_e) { parsed = undefined; }
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          // Relay the gateway verdict verbatim — truth lives upstream.
          return res.status(upstream.statusCode).json(parsed);
        }
        // Non-JSON upstream: preserve the status without inventing a result.
        return res.status(upstream.statusCode === 200 ? 502 : upstream.statusCode)
          .json({ relayed: false, error: 'capture_gateway_invalid_response',
                  upstreamStatus: upstream.statusCode });
      });
      upstream.on('error', function () {
        unknown();
      });
      upstream.on('aborted', unknown);
    });
    outbound.on('socket', function (socket) {
      if (!socket.connecting) connected = true;
      else socket.once('connect', function () { connected = true; });
    });
    outbound.on('timeout', function () {
      outbound.destroy(new Error('capture_record_command_timeout'));
    });
    outbound.on('error', function (err) {
      // Once connected, the gateway may have received and acted on the
      // command even if the connection closes before its response arrives.
      if (connected || (err && /timeout/.test(String(err.message)))) return unknown();
      fail('capture_gateway_unavailable', 503);
    });
    outbound.end(payload);
  }
};
