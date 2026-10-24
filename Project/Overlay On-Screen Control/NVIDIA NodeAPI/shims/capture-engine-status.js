'use strict';

// API-only bridge. Mounted after NodeAPI's existing security middleware.
// Never writes config, starts/stops recording, or touches OSC UI.
var http = require('http');

module.exports = function registerCaptureEngineStatus(app, options) {
  options = options || {};
  app.get('/DulukaCapture/v.1.0/engines', function (_req, res) {
    var configured = Number(options.port || process.env.NV_CONTAINER_CONTROL_API_PORT || 59012);
    var port = Number.isInteger(configured) && configured >= 1 && configured <= 65535
      ? configured : 59012;
    var settled = false;
    function fail(message) {
      if (settled) return;
      settled = true;
      res.status(503).json({
        schemaVersion: 1, readOnly: true, captureRuntimeReady: false,
        codeText: 'CAPTURE_CATALOG_UNAVAILABLE', message: message
      });
    }
    var request = http.get({
      hostname: '127.0.0.1', port: port, path: '/v1/capture/engines',
      timeout: 1500
    }, function (response) {
      var length = 0;
      var chunks = [];
      response.on('data', function (chunk) {
        length += chunk.length;
        if (length > 65536) {
          response.destroy(new Error('Capture engine catalog exceeded 64 KiB'));
          return;
        }
        chunks.push(chunk);
      });
      response.on('end', function () {
        if (settled) return;
        if (response.statusCode !== 200) {
          fail('NvContainer capture catalog request failed');
          return;
        }
        try {
          var body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          if (!body || body.schemaVersion !== 1 || !Array.isArray(body.providers) ||
              body.readOnly !== true) {
            fail('NvContainer returned an invalid capture catalog');
            return;
          }
          settled = true;
          res.status(200).json(body);
        } catch (_e) {
          fail('NvContainer returned malformed capture catalog JSON');
        }
      });
      response.on('error', function () { fail('NvContainer capture catalog stream failed'); });
    });
    request.on('timeout', function () { request.destroy(new Error('Capture catalog timeout')); });
    request.on('error', function () { fail('NvContainer capture catalog is unavailable'); });
  });

  // Same existing NodeAPI registration point, no edits to the shared
  // index.js (which is concurrently owned by another agent).
  if (typeof app.post === 'function') {
    require('./capture-display-routing')(app, options);
  }
};
