'use strict';

// Prevent browser-based cross-site requests from reaching NvContainer via NodeAPI.
// A same-user local process can still call the loopback API: this is a CSRF guard.
function loopback(hostname) {
    var value = String(hostname || '').toLowerCase();
    return value === '127.0.0.1' || value === 'localhost' ||
        value === '::1' || value === '[::1]' ||
        value === '::ffff:127.0.0.1' || value === '[::ffff:127.0.0.1]';
}

function authorize(req) {
    var remote = req && req.socket && req.socket.remoteAddress;
    if (!loopback(remote)) return false;
    var headers = (req && req.headers) || {};
    if (typeof headers.host !== 'string') return false;
    var authority;
    try {
        authority = new URL('http://' + headers.host);
        if (!loopback(authority.hostname) || authority.pathname !== '/' ||
            authority.username || authority.password || authority.search || authority.hash)
            return false;
    } catch (e) { return false; }

    if (headers.origin !== undefined) {
        try {
            var origin = new URL(headers.origin);
            if (origin.protocol !== 'http:' || !loopback(origin.hostname) ||
                origin.host.toLowerCase() !== authority.host.toLowerCase() ||
                origin.pathname !== '/' || origin.search || origin.hash ||
                origin.username || origin.password) return false;
        } catch (e) { return false; }
    }

    var site = headers['sec-fetch-site'];
    if (site === 'cross-site' || (site === 'same-site' && !headers.origin)) return false;
    return true;
}

module.exports = { authorize: authorize };
