'use strict';

// Read-only NvCapture health bridge for OSC Video Settings.
// Both GET and legacy POST are accepted; neither changes capture state.
const http = require('http');

module.exports = function registerCaptureRuntimeStatus(app, options) {
  options = options || {};

  function status(_req, res) {
    const enabled = options.enabled !== undefined
      ? options.enabled === true
      : process.env.NV_CAPTURE_RUNTIME_ENABLED === '1';

    if (!enabled) {
      return res.status(200).json({ enabled: false, ready: false, recording: false });
    }

    const configured = Number(options.port || process.env.NV_CAPTURE_RUNTIME_API_PORT || 59077);
    if (!Number.isInteger(configured) || configured < 1 || configured > 65535) {
      return res.status(503).json({
        enabled: true, ready: false, recording: false,
        codeText: 'CAPTURE_RUNTIME_INVALID_PORT'
      });
    }

    let finished = false;
    function fail(code, reason) {
      if (finished) return;
      finished = true;
      return res.status(code).json({
        enabled: true, ready: false, recording: false, codeText: reason
      });
    }

    const upstream = http.get({
      hostname: '127.0.0.1',
      port: configured,
      path: '/health',
      timeout: Number(options.timeoutMs) || 2000
    }, function (reply) {
      let length = 0;
      const chunks = [];
      reply.on('data', function (chunk) {
        length += chunk.length;
        if (length > 32768) {
          reply.destroy(new Error('NvCapture health exceeds 32 KiB'));
          return;
        }
        chunks.push(chunk);
      });
      reply.on('error', function () {
        fail(503, 'CAPTURE_RUNTIME_STREAM_FAILED');
      });
      reply.on('end', function () {
        if (finished) return;
        if (reply.statusCode !== 200) {
          return fail(503, 'CAPTURE_RUNTIME_UNAVAILABLE');
        }

        let body;
        try {
          body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        } catch (_e) {
          return fail(502, 'CAPTURE_RUNTIME_INVALID_RESPONSE');
        }
        if (!body || typeof body !== 'object' || Array.isArray(body) ||
            body.ok !== true || typeof body.ready !== 'boolean' ||
            typeof body.recording !== 'boolean') {
          return fail(502, 'CAPTURE_RUNTIME_INVALID_RESPONSE');
        }

        finished = true;
        res.status(200).json({
          enabled: true,
          ready: body.ready,
          recording: body.ready && body.recording
        });
      });
    });
    upstream.on('timeout', function () {
      upstream.destroy(new Error('NvCapture health timed out'));
    });
    upstream.on('error', function () {
      fail(503, 'CAPTURE_RUNTIME_UNAVAILABLE');
    });
  }

  app.get('/DulukaCapture/v.1.0/status', status);
  app.post('/DulukaCapture/v.1.0/status', status); // historical read-only compatibility
};
