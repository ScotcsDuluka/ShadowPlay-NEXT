'use strict'
// logger.js — leveled logger: console + data/logs/backend.log.
// Storage failures must never take the backend down (DulukaFloor rule).

const fs = require('fs');
const path = require('path');

const LEVELS = { debug: 10, info: 20, warn: 30, error: 40 };

function createLogger(logDir, level) {
    const min = LEVELS[level] || LEVELS.info;
    let logPath = null;
    try {
        fs.mkdirSync(logDir, { recursive: true });
        logPath = path.join(logDir, 'backend.log');
    } catch (err) { /* console-only mode */ }

    function write(lvl, args) {
        if ((LEVELS[lvl] || 0) < min) return;
        const line = '[' + new Date().toISOString() + '] [' + lvl + '] ' +
            args.map(function (a) {
                if (a instanceof Error) return a.stack || String(a);
                if (typeof a === 'object') { try { return JSON.stringify(a); } catch (e) { return String(a); } }
                return String(a);
            }).join(' ');
        try { console.log(line); } catch (e) { /* stdin gone — keep going */ }
        if (logPath) {
            try { fs.appendFileSync(logPath, line + '\n'); } catch (e) { /* never crash on logs */ }
        }
    }

    return {
        debug: function () { write('debug', [].slice.call(arguments)); },
        info: function () { write('info', [].slice.call(arguments)); },
        warn: function () { write('warn', [].slice.call(arguments)); },
        error: function () { write('error', [].slice.call(arguments)); },
        filePath: function () { return logPath; }
    };
}

module.exports = { createLogger };
