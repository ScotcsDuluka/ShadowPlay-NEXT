'use strict';

// Browser-origin and loopback boundary for NVIDIA NodeAPI.
// This is a CSRF / DNS-rebinding guard, NOT authentication of local processes.
// Keep the legacy NVIDIA cookie flag unchanged for OSC/CEF compatibility.
var base = require('./localGpuRequestGuard.js');

function sameOriginReferrer(req) {
    var headers = req.headers || {};
    if (headers.referer === undefined) return true;
    if (typeof headers.referer !== 'string') return false;
    try {
        var referrer = new URL(headers.referer);
        var host = new URL('http://' + headers.host);
        return referrer.protocol === 'http:' && referrer.origin.toLowerCase() === host.origin.toLowerCase();
    } catch (error) {
        return false;
    }
}

function authorize(req) {
    return !!req && base.authorize(req) && sameOriginReferrer(req);
}

function isHotkeyGet(req) {
    if (req.method !== 'GET' && req.method !== 'HEAD') return false;
    var path = String(req.url || '').split('?')[0];
    if (path !== '/') return false;
    var query = String(req.url || '').split('?').slice(1).join('?');
    return /(?:^|&)hk(?:=|&|$)/i.test(query);
}

function httpMiddleware(req, res, next) {
    if (!authorize(req)) {
        res.setHeader('Cache-Control', 'no-store');
        res.status(403).json({
            codeText: 'LOCAL_API_ORIGIN_DENIED',
            message: 'NVIDIA NodeAPI accepts only local, same-origin requests'
        });
        return;
    }
    // Legacy browser URL navigation must never be able to trigger a hotkey.
    // Native helper traffic is dispatched by Container or POST.
    if (isHotkeyGet(req)) {
        res.setHeader('Allow', 'POST');
        res.status(405).json({ codeText: 'HOTKEY_POST_REQUIRED' });
        return;
    }
    next();
}

function socketAllowRequest(req, done) {
    done(null, authorize(req));
}

module.exports = {
    authorize: authorize,
    httpMiddleware: httpMiddleware,
    socketAllowRequest: socketAllowRequest
};
