/* gpuRuntimeValidation.js — validation for /Duluka/v1/GpuRuntimeState events.
 * Mirrors the NvGraphics.Contracts validator (schema v1). Kept as a module so the
 * allowlist handler in index.js and the automated tests share one implementation.
 * Stale windows (>2 min) are rejected so a dead publisher's result is never
 * re-emitted to OSC as fresh state. */

'use strict';

var timestampValidation = require('./timestampValidation.js');
var GPU_RUNTIME_STATES = { Unknown: 1, Inactive: 1, Verified: 1, MultipleAdapters: 1 };
var GPU_RUNTIME_SCHEMA_VERSION = 1;
var GPU_RUNTIME_STALE_MS = 2 * 60 * 1000;

function isValidGpuRuntimeState(data) {
    if (!data || data.schemaVersion !== GPU_RUNTIME_SCHEMA_VERSION) return false;
    if (typeof data.eventId !== 'string' || data.eventId.length < 1 || data.eventId.length > 64) return false;
    if (typeof data.state !== 'string' || !GPU_RUNTIME_STATES[data.state]) return false;
    if (typeof data.exePath !== 'string' || data.exePath.length === 0 || data.exePath.length > 512) return false;
    if (data.state === 'Unknown' && (typeof data.reason !== 'string' || data.reason.length === 0)) return false;
    if (typeof data.reason !== 'string' || data.reason.length > 2048) return false;
    if (!(data.pid >= 0 && data.pid <= 4194304)) return false;
    if (!(data.windowDurationMs >= 0 && data.windowDurationMs <= 600000)) return false;
    if (!timestampValidation.isFreshTimestamp(data.windowStartUtc, GPU_RUNTIME_STALE_MS)) return false;
    if (!Array.isArray(data.engineTypes) || data.engineTypes.length > 16) return false;
    if (!Array.isArray(data.adapters) || data.adapters.length > 8) return false;
    for (var i = 0; i < data.adapters.length; i++) {
        var a = data.adapters[i];
        if (!a || typeof a.adapterId !== 'string' || a.adapterId.length === 0 || a.adapterId.length > 128) return false;
    }
    if (data.state === 'Verified' && data.adapters.length !== 1) return false;
    if (data.state === 'MultipleAdapters' && data.adapters.length < 2) return false;
    if ((data.state === 'Unknown' || data.state === 'Inactive') && data.adapters.length > 0) return false;
    return true;
}

module.exports = {
    isValidGpuRuntimeState: isValidGpuRuntimeState,
    isValidGpuPreferenceApplied: isValidGpuPreferenceApplied,
    GPU_RUNTIME_STATES: GPU_RUNTIME_STATES,
    GPU_RUNTIME_SCHEMA_VERSION: GPU_RUNTIME_SCHEMA_VERSION,
    GPU_RUNTIME_STALE_MS: GPU_RUNTIME_STALE_MS
};

/* ---------------------------------------------------------------------------
 * GPU preference-applied events (/Duluka/v1/GpuPreferenceApplied).
 * Mirrors NvGraphics.Contracts GpuPreferencePayloadValidator (schema v1).
 * Reports a verified Windows registry write — NEVER actual runtime GPU usage.
 * ------------------------------------------------------------------------- */
function isValidGpuPreferenceApplied(data) {
    if (!data || data.schemaVersion !== 1) return false;
    if (typeof data.eventId !== 'string' || data.eventId.length < 1 || data.eventId.length > 64) return false;
    if (typeof data.exePath !== 'string' || data.exePath.length < 3 || data.exePath.length > 512) return false;
    if (typeof data.adapterId !== 'string' || data.adapterId.length > 128) return false;
    if (data.isSystemDefault !== true && data.adapterId.length === 0) return false;
    /* optional display fields — must be strings when present (C# allows empty) */
    if (data.adapterName != null && (typeof data.adapterName !== 'string' || data.adapterName.length > 256)) return false;
    if (data.displayName != null && (typeof data.displayName !== 'string' || data.displayName.length > 128)) return false;
    if (!timestampValidation.isFreshTimestamp(data.appliedUtc, GPU_RUNTIME_STALE_MS)) return false;
    return true;
}

function isValidGpuAdapterInventory(data) {
    if (!data || data.schemaVersion !== 1) return false;
    if (typeof data.eventId !== 'string' || data.eventId.length < 1 || data.eventId.length > 64) return false;
    if (!timestampValidation.isFreshTimestamp(data.capturedUtc, GPU_RUNTIME_STALE_MS)) return false;
    if (!Array.isArray(data.adapters) || data.adapters.length > 16) return false;
    for (var i = 0; i < data.adapters.length; i++) {
        var a = data.adapters[i];
        if (!a || typeof a.name !== 'string' || a.name.length < 1 || a.name.length > 256) return false;
        if (a.adapterId != null && (typeof a.adapterId !== 'string' || a.adapterId.length > 128)) return false;
        if (a.pciBusId != null && (typeof a.pciBusId !== 'string' || a.pciBusId.length > 32)) return false;
        if (!Array.isArray(a.dxgiLuids) || a.dxgiLuids.length > 8 ||
            a.dxgiLuids.some(function (luid) { return typeof luid !== 'string' || !luid || luid.length > 64; })) return false;
        if (!validPercent(a.utilizationPercent) || !validMemory(a.memoryUsedMiB) ||
            !validMemory(a.memoryTotalMiB)) return false;
        if (a.memoryUsedMiB != null && a.memoryTotalMiB != null &&
            a.memoryUsedMiB > a.memoryTotalMiB) return false;
    }
    return true;
}

function validPercent(value) {
    return value == null || (typeof value === 'number' && isFinite(value) && value >= 0 && value <= 100);
}

function validMemory(value) {
    return value == null || (typeof value === 'number' && isFinite(value) && value >= 0 && value <= 1000000);
}

module.exports.isValidGpuAdapterInventory = isValidGpuAdapterInventory;
