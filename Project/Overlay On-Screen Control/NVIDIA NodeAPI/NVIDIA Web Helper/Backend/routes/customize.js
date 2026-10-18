'use strict'
// routes/customize.js — the customize + slider-bounds surface.
//
//   POST /ShadowPlay/v1.0/OSC/GetCustomize/<Name>   (v1.0 — NO dot — EXACTLY
//   POST /ShadowPlay/v.1.0/OSC/GetCustomize/<Name>    as the native module:
//                                                     the page calls BOTH
//                                                     spellings for this
//                                                     endpoint family only)
//     <Name> ∈ Record | InstantReplay | Broadcast
//     Body MUST carry quality + resolution + framerate + bitrateBps — the
//     native backend 500s on the first missing one and the page relies on
//     that contract (commit 01b6a02).
//     The response reports the CURRENT SAVED settings — the page persists
//     them via the per-feature /Settings POSTs, so read the store here.
//
//   GET /ShadowPlay/v.1.0/Resolutions[/x]   — plain string array (a263b4d:
//                                             object arrays stuck the page
//                                             init forever)
//   GET /ShadowPlay/v.1.0/FrameRates        — {framerates:[60,30]}
//   GET /ShadowPlay/v.1.0/Framerates[/x]    — both spellings the page uses
//   GET /ShadowPlay/v.1.0/BitRates/<q>/<r>  — slider bounds

const defaults = require('../lib/defaults');

function BuildCustomizeResponse(name, section) {
    const d = defaults.SETTINGS_DEFAULTS[name] || defaults.SETTINGS_DEFAULTS.record;
    const s = Object.assign({}, d, section || {});
    const bounds = name === 'broadcast' ? defaults.BITRATE_BROADCAST : defaults.BITRATE_RECORD;
    const out = {
        resolutions: defaults.RESOLUTIONS,
        framerates: defaults.FRAMERATES,
        quality: s.quality,
        resolution: s.resolution,
        framerate: s.framerate,
        bitrate: {
            current: s.bitrateBps,
            min: bounds.min,
            max: bounds.max,
            default: bounds.default
        }
    };
    if (name === 'instantreplay') out.replayLengthSeconds = s.replayLengthSeconds;
    if (name === 'broadcast') out.provider = s.provider;
    return out;
}

module.exports = function customizeRoutes(app, ctx) {

    function getCustomize(req, res) {
        const name = String(req.params.name || '').toLowerCase();
        if (['record', 'instantreplay', 'broadcast'].indexOf(name) < 0) {
            // unknown customize family — native style miss
            res.status(404).json({});
            return;
        }
        const body = req.body;
        if (!body || typeof body !== 'object' || Array.isArray(body)) {
            res.status(500).json({
                type: 'Error', code: 3, codeText: 'Invalid argument',
                message: "Argument doesn't have 'quality' property"
            });
            return;
        }
        for (const field of ['quality', 'resolution', 'framerate', 'bitrateBps']) {
            if (body[field] === undefined) {
                res.status(500).json({
                    type: 'Error', code: 3, codeText: 'Invalid argument',
                    message: "Argument doesn't have '" + field + "' property"
                });
                return;
            }
        }
        const stored = Object.assign({},
            ctx.store.getSection(defaults.SETTINGS_KEY[name]),
            ctx.store.getSection('customize:' + name));
        res.status(200).json(BuildCustomizeResponse(name, stored));
    }

    app.post('/ShadowPlay/v1.0/OSC/GetCustomize/:name', getCustomize);
    app.post('/ShadowPlay/v.1.0/OSC/GetCustomize/:name', getCustomize);

    app.get('/ShadowPlay/v.1.0/Resolutions', function (req, res) {
        res.status(200).json({ resolutions: defaults.RESOLUTIONS });
    });
    app.get('/ShadowPlay/v.1.0/Resolutions/*', function (req, res) {
        res.status(200).json({ resolutions: defaults.RESOLUTIONS });
    });

    app.get('/ShadowPlay/v.1.0/FrameRates', function (req, res) {
        res.status(200).json({ framerates: defaults.FRAMERATES });
    });
    app.get('/ShadowPlay/v.1.0/Framerates', function (req, res) {
        res.status(200).json({ framerates: defaults.FRAMERATES });
    });
    app.get('/ShadowPlay/v.1.0/Framerates/*', function (req, res) {
        res.status(200).json({ framerate: 60 });
    });

    app.get('/ShadowPlay/v.1.0/BitRates/:quality/:resolution', function (req, res) {
        const bounds = defaults.BITRATE_RECORD;
        res.status(200).json({
            bitrateBpsMin: bounds.min,
            bitrateBpsMax: bounds.max,
            bitrateBpsDefault: bounds.default
        });
    });
};
