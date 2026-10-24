'use strict';

// OSC never talks to NvCapture directly. NodeAPI -> NvContainer -> NvCapture.
// These routes fail closed until Container implements the display/screenshot
// gateway. Container owns command authentication/authorization.
const http = require('http');

// Screenshot may take up to 15 seconds in the Container gateway (plus
// forwarding overhead). Do not disconnect at the generic 5.5-second
// write timeout and misreport an already-saved image as failed.
function upstreamTimeoutMs(method, path) {
  if (path === '/v1/capture/screenshot') return 19000;
  return method === 'POST' ? 5500 : 2500;
}

module.exports = function registerDisplayRouting(app, options) {
  const configured = Number((options || {}).port ||
    process.env.NV_CONTAINER_CONTROL_API_PORT || 59012);
  const port = Number.isInteger(configured) && configured > 0 && configured < 65536
    ? configured : 59012;

  function forward(method, upstreamPath, requestBody, res) {
    let settled = false;
    function error(status, code) {
      if (settled) return;
      settled = true;
      res.status(status).json({ availability: 'Unavailable', error: code });
    }
    const data = requestBody === null ? null : JSON.stringify(requestBody);
    const opts = {
      hostname: '127.0.0.1', port, method, path: upstreamPath,
      timeout: upstreamTimeoutMs(method, upstreamPath),
      headers: data ? { 'content-type': 'application/json',
        'content-length': Buffer.byteLength(data) } : {}
    };
    const outbound = http.request(opts, function (upstream) {
      let size = 0;
      const chunks = [];
      upstream.on('data', function (chunk) {
        size += chunk.length;
        if (size > 65536) {
          upstream.destroy(new Error('capture_gateway_response_too_large'));
          return;
        }
        chunks.push(chunk);
      });
      upstream.on('end', function () {
        if (settled) return;
        const status = upstream.statusCode;
        // Container may return plain-text 501/503; preserve an accurate
        // unavailable status instead of reporting a JSON parse failure.
        if (status !== 200) {
          const maybeScreenshot = upstreamPath === '/v1/capture/screenshot';
          error(status === 501 ? 501 : status === 504 ? 504 : 503,
            maybeScreenshot && status === 504 ?
              'capture_screenshot_outcome_unknown' : 'capture_gateway_unavailable');
          return;
        }
        try {
          const payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          if (!payload || typeof payload !== 'object') {
            error(503, 'capture_gateway_unavailable');
            return;
          }
          if (method === 'GET' && (payload.schemaVersion !== 1 ||
              !Array.isArray(payload.displays))) {
            error(503, 'invalid_capture_display_catalog');
            return;
          }
          if (method === 'POST' && typeof payload.saved !== 'boolean') {
            error(503, 'invalid_capture_command_result');
            return;
          }
          settled = true;
          res.status(200).json(payload);
        } catch (_) {
          error(503, 'capture_gateway_invalid_json');
        }
      });
      upstream.on('error', () => error(503, 'capture_gateway_read_failed'));
    });
    outbound.on('timeout', function () {
      // The GPU worker may still have completed after a timeout; do NOT
      // auto-retry a screenshot or infer saved=false from transport state.
      error(504, upstreamPath === '/v1/capture/screenshot' ?
        'capture_screenshot_outcome_unknown' : 'capture_gateway_timeout');
      outbound.destroy();
    });
    outbound.on('error', function () {
      error(503, 'capture_gateway_unavailable');
    });
    outbound.end(data || undefined);
  }

  app.get('/DulukaCapture/v.1.0/displays', function (_req, res) {
    forward('GET', '/v1/capture/displays', null, res);
  });
  app.post('/DulukaCapture/v.1.0/display/select', function (req, res) {
    const b = req.body;
    if (!b || b.confirmed !== true ||
        !Number.isInteger(b.outputIndex) || b.outputIndex < 0 ||
        !Number.isInteger(b.adapterLuidLow) || b.adapterLuidLow < 0 ||
        !Number.isInteger(b.adapterLuidHigh)) {
      res.status(400).json({ saved: false, error: 'invalid_capture_display_selection' });
      return;
    }
    forward('POST', '/v1/capture/display/select', {
      confirmed: true, outputIndex: b.outputIndex,
      adapterLuidLow: b.adapterLuidLow, adapterLuidHigh: b.adapterLuidHigh
    }, res);
  });
  app.post('/DulukaCapture/v.1.0/screenshot', function (req, res) {
    if (!req.body || req.body.confirmed !== true) {
      res.status(400).json({ saved: false, error: 'screenshot_confirmation_required' });
      return;
    }
    forward('POST', '/v1/capture/screenshot', { confirmed: true }, res);
  });
};

// Test seam only: no network, no GPU access.
module.exports.timeoutFor = upstreamTimeoutMs;
