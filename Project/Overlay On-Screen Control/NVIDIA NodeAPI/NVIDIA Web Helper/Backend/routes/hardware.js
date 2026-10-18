'use strict'
// routes/hardware.js — per-machine hardware surface.
//
//   GET /HardwareInformation/v.0.1   — hardware floor (both spellings the
//   GET /HardwareInformation/v.0.2     page's hardwareSdk uses; v.0.2 proven
//                                      live on the 1080 Ti backend — every
//                                      value a STRING, GPU[] present)
//   GET /HardwareInformation/v.1.0/GPU    — 404 {} (real floor on the
//   GET /HardwareInformation/v.1.0/Info    standalone backend; the page
//                                           handles the 404 path)
//   GET /SystemInfo/v.1.0/GPU | /Driver   — 404 {} (real floor)
//
// The floor itself: lib/hardwareProbe.js (WMI at boot → data/hardware-floor.json,
// generic shape elsewhere). install-host.ps1's generator is superseded by it.

module.exports = function hardwareRoutes(app, ctx) {

    function floorReply(req, res) {
        res.status(200).json(ctx.hardware.get());
    }

    app.get('/HardwareInformation/v.0.1', floorReply);
    app.get('/HardwareInformation/v.0.1/', floorReply);
    app.get('/HardwareInformation/v.0.2', floorReply);
    app.get('/HardwareInformation/v.0.2/', floorReply);
    app.get('/HardwareInformation/v.0.2/generic', floorReply);

    app.get('/HardwareInformation/v.1.0/GPU', function (req, res) { res.status(404).json({}); });
    app.get('/HardwareInformation/v.1.0/Info', function (req, res) { res.status(404).json({}); });

    app.get('/SystemInfo/v.1.0/GPU', function (req, res) { res.status(404).json({}); });
    app.get('/SystemInfo/v.1.0/Driver', function (req, res) { res.status(404).json({}); });
};
