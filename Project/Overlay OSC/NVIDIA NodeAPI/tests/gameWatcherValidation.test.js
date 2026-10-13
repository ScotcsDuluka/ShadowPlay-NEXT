'use strict';

var assert = require('assert');
var validator = require('../gameWatcherValidation.js');
var now = new Date().toISOString();

var valid = {
    schemaVersion: 1,
    eventId: 'test-event-001',
    publishedUtc: now,
    status: {
        enabled: true,
        running: true,
        catalogReady: true,
        catalogReachable: true,
        catalogEmpty: false,
        catalogVersion: '1.1.0',
        catalogGameCount: 7,
        knownExecutableNames: 7,
        cachedExecutableResolutions: 2,
        candidateProcessCount: 2,
        matchedGameCount: 2,
        matchedGames: [
            {
                processId: 101,
                executableName: 'Dungeons-Win64-Shipping.exe',
                executablePath: 'C:\\Games\\Minecraft Dungeons\\Dungeons-Win64-Shipping.exe',
                gameId: 'mojang.minecraft-dungeons',
                displayName: 'Minecraft Dungeons',
                catalogVersion: '1.1.0',
                matchedUtc: now
            },
            {
                processId: 202,
                executableName: 'Smash_Legends.exe',
                executablePath: 'C:\\Games\\SMASH LEGENDS\\Smash_Legends.exe',
                gameId: '5minlab.smash-legends',
                displayName: 'SMASH LEGENDS',
                catalogVersion: '1.1.0',
                matchedUtc: now
            }
        ],
        lastCatalogRefreshUtc: now,
        lastScanUtc: now,
        lastRefreshStatusCode: 200,
        catalogError: '',
        lastError: ''
    }
};

assert.strictEqual(validator.isValidGameWatcherStatus(valid), true, 'valid snapshot should pass');

var stale = JSON.parse(JSON.stringify(valid));
stale.publishedUtc = new Date(Date.now() - 3 * 60 * 1000).toISOString();
assert.strictEqual(validator.isValidGameWatcherStatus(stale), false, 'stale snapshot should fail');

var badCount = JSON.parse(JSON.stringify(valid));
badCount.status.matchedGameCount = 1;
assert.strictEqual(validator.isValidGameWatcherStatus(badCount), false, 'inconsistent matched count should fail');

var badGameId = JSON.parse(JSON.stringify(valid));
badGameId.status.matchedGames[0].gameId = '';
assert.strictEqual(validator.isValidGameWatcherStatus(badGameId), false, 'missing game identity should fail');

var badTimestamp = JSON.parse(JSON.stringify(valid));
badTimestamp.publishedUtc = 'not-a-date';
assert.strictEqual(validator.isValidGameWatcherStatus(badTimestamp), false, 'invalid timestamp should fail');

console.log('PASS: valid snapshot, stale snapshot, count mismatch, missing game identity, and invalid timestamp.');
