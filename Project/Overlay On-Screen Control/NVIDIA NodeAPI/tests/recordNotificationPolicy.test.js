'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const policy = require('../shims/record-notification-policy');

test('desired-state toggles never claim a recording started or saved', () => {
  assert.deepEqual(policy.desiredEvents(false), []);
  assert.deepEqual(policy.desiredEvents(true), []);
});
test('actual recording start notifies only on authoritative transition', () => {
  assert.deepEqual(policy.actualEvents(false, { running: true }), [
    { name: '/ShadowPlay/v.1.0/Record/Enable', data: { status: true } },
    { name: '/ShadowPlay/v.1.0/Record/Running', data: { running: true } }
  ]);
  assert.deepEqual(policy.actualEvents(true, { running: true }), []);
});
test('stop without file never claims a saved recording', () => {
  const events = policy.actualEvents(true, { running: false });
  assert.deepEqual(events, [
    { name: '/ShadowPlay/v.1.0/Record/Running', data: { running: false } }
  ]);
});
test('failed session and malformed saved evidence never emit saved toast', () => {
  for (const body of [
    { running: false, recordingSaved: false, outputPath: 'video.mp4' },
    { running: false, recordingSaved: true, outputPath: '' },
    { running: false, recordingSaved: true, outputPath: null },
    { running: false, recordingSaved: 'true', outputPath: 'video.mp4' },
    { running: false, recordingSaved: true, outputPath: 'x'.repeat(4096) }
  ]) {
    assert.deepEqual(policy.actualEvents(true, body), [
      { name: '/ShadowPlay/v.1.0/Record/Running', data: { running: false } }
    ]);
  }
});
test('verified completed session emits saved notification once on actual stop', () => {
  const body = { running: false, recordingSaved: true,
    outputPath: 'C:\\Videos\\recording.mp4' };
  assert.deepEqual(policy.actualEvents(true, body), [
    { name: '/ShadowPlay/v.1.0/Record/Enable', data: { status: false } },
    { name: '/ShadowPlay/v.1.0/Record/Running', data: { running: false } }
  ]);
  assert.deepEqual(policy.actualEvents(false, body), []);
});
test('irrelevant malformed actual payloads are ignored', () => {
  for (const body of [null, undefined, {}, { running: 'false' }]) {
    assert.deepEqual(policy.actualEvents(true, body), []);
  }
});
