'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const guard = require('../localGpuRequestGuard.js');

function request(headers = {}, remoteAddress = '127.0.0.1') {
    return { socket: { remoteAddress }, headers: { host: '127.0.0.1:59011', ...headers } };
}

test('native loopback clients are accepted', () => {
    assert.equal(guard.authorize(request()), true);
    assert.equal(guard.authorize(request({}, '::ffff:127.0.0.1')), true);
});
test('same-origin OSC browser requests are accepted', () => {
    assert.equal(guard.authorize(request({
        origin: 'http://127.0.0.1:59011',
        'sec-fetch-site': 'same-origin'
    })), true);
});
test('cross-origin browser requests are rejected before proxying', () => {
    assert.equal(guard.authorize(request({ origin: 'https://attacker.example' })), false);
    assert.equal(guard.authorize(request({ origin: 'null' })), false);
    assert.equal(guard.authorize(request({ origin: 'http://127.0.0.1:9090' })), false);
    assert.equal(guard.authorize(request({ 'sec-fetch-site': 'cross-site' })), false);
});
test('non-loopback or rebinding Host is rejected', () => {
    assert.equal(guard.authorize(request({}, '192.0.2.10')), false);
    assert.equal(guard.authorize(request({ host: 'attacker.example:59011' })), false);
});
