'use strict';

// Validator for NvContainer -> OSC game-watcher snapshots.
// Snapshots are loopback-only events, schema v1, and must remain fresh.
var MAX_EVENT_AGE_MS = 2 * 60 * 1000;
var MAX_GAMES = 128;
var timestampValidation = require('./timestampValidation.js');

function isString(value, min, max) {
    return typeof value === 'string' && value.length >= min && value.length <= max;
}

function isCount(value, max) {
    return typeof value === 'number' && isFinite(value) &&
        Math.floor(value) === value && value >= 0 && value <= max;
}

function isUtc(value, allowNull) {
    if (allowNull && value === null) return true;
    if (typeof value !== 'string') return false;
    return isFinite(Date.parse(value));
}

function isFreshUtc(value) {
    return timestampValidation.isFreshTimestamp(value, MAX_EVENT_AGE_MS);
}

function isValidMatchedGame(game) {
    if (!game || typeof game !== 'object' || Array.isArray(game)) return false;
    if (!isCount(game.processId, 4194304) || game.processId === 0) return false;
    if (!isString(game.executableName, 1, 260)) return false;
    if (!isString(game.executablePath, 3, 2048)) return false;
    if (!isString(game.gameId, 1, 128) || !isString(game.displayName, 1, 128)) return false;
    if (!isString(game.catalogVersion, 0, 64) || !isUtc(game.matchedUtc, false)) return false;
    return true;
}

function isValidGameWatcherStatus(data) {
    if (!data || typeof data !== 'object' || Array.isArray(data) || data.schemaVersion !== 1) return false;
    if (!isString(data.eventId, 1, 64) || !isFreshUtc(data.publishedUtc)) return false;

    var s = data.status;
    if (!s || typeof s !== 'object' || Array.isArray(s)) return false;
    if (typeof s.enabled !== 'boolean' || typeof s.running !== 'boolean') return false;
    if (typeof s.catalogReady !== 'boolean' || typeof s.catalogReachable !== 'boolean' ||
        typeof s.catalogEmpty !== 'boolean') return false;
    if (!isString(s.catalogVersion, 0, 64)) return false;
    if (!isCount(s.catalogGameCount, 100000) ||
        !isCount(s.knownExecutableNames, 200000) ||
        !isCount(s.cachedExecutableResolutions, 200000) ||
        !isCount(s.candidateProcessCount, 100000) ||
        !isCount(s.matchedGameCount, MAX_GAMES) ||
        !isCount(s.lastRefreshStatusCode, 599)) return false;
    if (!Array.isArray(s.matchedGames) || s.matchedGames.length > MAX_GAMES ||
        s.matchedGameCount !== s.matchedGames.length) return false;
    if (!isUtc(s.lastCatalogRefreshUtc, true) || !isUtc(s.lastScanUtc, true)) return false;
    if (!isString(s.catalogError, 0, 2048) || !isString(s.lastError, 0, 2048)) return false;

    for (var i = 0; i < s.matchedGames.length; i++) {
        if (!isValidMatchedGame(s.matchedGames[i])) return false;
    }
    return true;
}

module.exports = {
    isValidGameWatcherStatus: isValidGameWatcherStatus,
    MAX_EVENT_AGE_MS: MAX_EVENT_AGE_MS,
    MAX_GAMES: MAX_GAMES
};
