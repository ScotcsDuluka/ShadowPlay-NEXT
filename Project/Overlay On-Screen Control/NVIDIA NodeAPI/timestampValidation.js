'use strict';

// Shared freshness check for versioned NodeAPI event contracts.
// Keeps the existing symmetric tolerance for clock skew around Date.now().
function isFreshTimestamp(value, maxAgeMs) {
    if (typeof value !== 'string') return false;
    var timestamp = Date.parse(value);
    if (!isFinite(timestamp)) return false;
    var age = Date.now() - timestamp;
    return age <= maxAgeMs && age >= -maxAgeMs;
}

module.exports = {
    isFreshTimestamp: isFreshTimestamp
};
