'use strict';

var assert = require('assert');
var validation = require('../gpuRuntimeValidation.js');
var timestamps = require('../timestampValidation.js');

function nowUtc() {
    return new Date().toISOString();
}

function staleUtc() {
    return new Date(Date.now() - 3 * 60 * 1000).toISOString();
}

var fresh = nowUtc();
assert.strictEqual(timestamps.isFreshTimestamp(fresh, 2 * 60 * 1000), true);
assert.strictEqual(timestamps.isFreshTimestamp(staleUtc(), 2 * 60 * 1000), false);
assert.strictEqual(timestamps.isFreshTimestamp(new Date(Date.now() + 3 * 60 * 1000).toISOString(),
    2 * 60 * 1000), false);
assert.strictEqual(timestamps.isFreshTimestamp('not-a-date', 2 * 60 * 1000), false);
assert.strictEqual(timestamps.isFreshTimestamp(null, 2 * 60 * 1000), false);

var runtimeState = {
    schemaVersion: 1,
    eventId: 'runtime-001',
    state: 'Verified',
    exePath: 'C:\\Games\\sample.exe',
    reason: 'ok',
    pid: 1234,
    windowDurationMs: 5000,
    windowStartUtc: fresh,
    engineTypes: ['d3d11'],
    adapters: [{ adapterId: 'luid-1' }]
};
assert.strictEqual(validation.isValidGpuRuntimeState(runtimeState), true);
assert.strictEqual(validation.isValidGpuRuntimeState(
    Object.assign({}, runtimeState, { windowStartUtc: staleUtc() })), false);
assert.strictEqual(validation.isValidGpuRuntimeState(
    Object.assign({}, runtimeState, {
        adapters: [{ adapterId: 'luid-1' }, { adapterId: 'luid-2' }]
    })), false);

var preference = {
    schemaVersion: 1,
    eventId: 'preference-001',
    exePath: 'C:\\Games\\sample.exe',
    adapterId: 'pci-1',
    isSystemDefault: false,
    appliedUtc: nowUtc()
};
assert.strictEqual(validation.isValidGpuPreferenceApplied(preference), true);
assert.strictEqual(validation.isValidGpuPreferenceApplied(
    Object.assign({}, preference, { appliedUtc: staleUtc() })), false);
assert.strictEqual(validation.isValidGpuPreferenceApplied(
    Object.assign({}, preference, { adapterId: '', isSystemDefault: true })), true);

var inventory = {
    schemaVersion: 1,
    eventId: 'inventory-001',
    capturedUtc: nowUtc(),
    adapters: [{
        name: 'GPU',
        adapterId: 'pci-1',
        pciBusId: '1:0:0',
        dxgiLuids: ['luid-1'],
        utilizationPercent: 20,
        memoryUsedMiB: 500,
        memoryTotalMiB: 11264
    }]
};
assert.strictEqual(validation.isValidGpuAdapterInventory(inventory), true);
assert.strictEqual(validation.isValidGpuAdapterInventory(
    Object.assign({}, inventory, { capturedUtc: staleUtc() })), false);
assert.strictEqual(validation.isValidGpuAdapterInventory(
    Object.assign({}, inventory, {
        adapters: [Object.assign({}, inventory.adapters[0], { utilizationPercent: 101 })]
    })), false);

console.log('PASS: shared timestamp freshness and GPU runtime/preference/inventory validation.');
