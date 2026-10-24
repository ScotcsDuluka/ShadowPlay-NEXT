'use strict';

// Read-only Capture capabilities facade. NvContainer is the single
// authority; no local codec guessing or policy fallback is permitted.
const http = require('http');

module.exports = function registerCaptureCapabilities(app, options) {
  options = options || {};

  app.get('/DulukaCapture/v.1.0/capabilities', function (_req, res) {
    const port = options.port !== undefined
      ? Number(options.port) : Number(process.env.NV_CONTAINER_CONTROL_API_PORT || 59012);
    let settled = false;
    function fail(reason) {
      if (settled) return;
      settled = true;
      res.status(503).json({
        schemaVersion: 1,
        readOnly: true,
        availability: 'Unavailable',
        policyValid: false,
        codeText: reason
      });
    }
    if (!Number.isInteger(port) || port < 1 || port > 65535) {
      return fail('CAPTURE_CAPABILITIES_INVALID_PORT');
    }

    const request = http.get({
      hostname: '127.0.0.1',
      port: port,
      path: '/v1/capture/capabilities',
      timeout: options.timeoutMs || 2000
    }, function (upstream) {
      let length = 0;
      const chunks = [];
      upstream.on('data', function (chunk) {
        length += chunk.length;
        if (length > 131072) {
          upstream.destroy(new Error('Capture capabilities exceeded 128 KiB'));
          return;
        }
        chunks.push(chunk);
      });
      upstream.on('error', function () {
        fail('CAPTURE_CAPABILITIES_UPSTREAM_FAILED');
      });
      upstream.on('end', function () {
        if (settled) return;
        if (upstream.statusCode !== 200) {
          return fail('CAPTURE_CAPABILITIES_UPSTREAM_UNAVAILABLE');
        }
        let body;
        try {
          body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        } catch (_e) {
          return fail('CAPTURE_CAPABILITIES_INVALID_JSON');
        }
        if (!body || typeof body !== 'object' || Array.isArray(body) ||
            body.schemaVersion !== 1 || body.source !== 'NvCapture' ||
            (body.availability !== 'Available' && body.availability !== 'Unavailable') ||
            typeof body.policyValid !== 'boolean' ||
            !Array.isArray(body.providers) || !body.workflows ||
            typeof body.workflows !== 'object' || Array.isArray(body.workflows)) {
          return fail('CAPTURE_CAPABILITIES_INVALID_SCHEMA');
        }
        settled = true;
        res.status(200).json(body);
      });
    });
    request.on('timeout', function () {
      request.destroy(new Error('Capture capabilities timed out'));
    });
    request.on('error', function () {
      fail('CAPTURE_CAPABILITIES_UPSTREAM_UNAVAILABLE');
    });
  });
};
