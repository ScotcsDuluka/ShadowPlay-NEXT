'use strict';

// Read-only Gallery duration probe. FFprobe never changes the recording.
// Cached by full file name, size and mtime; bounded cache avoids memory growth.
const path = require('path');
const execFile = require('child_process').execFile;
const cache = new Map();
const MAX_ITEMS = 512;

function timeText(seconds) {
    if (typeof seconds !== 'number' || !Number.isFinite(seconds) ||
        seconds <= 0 || seconds >= 100000000) return '';
    const whole = Math.floor(seconds);
    const hh = Math.floor(whole / 3600);
    const mm = Math.floor((whole % 3600) / 60);
    const ss = whole % 60;
    const pad = n => String(n).padStart(2, '0');
    return pad(hh) + ':' + pad(mm) + ':' + pad(ss);
}

function enrich(metadata, filename, stats, ffmpeg, callback, deps) {
    if (!metadata || metadata.fileType !== 'video' ||
        typeof ffmpeg !== 'string' || !ffmpeg) {
        return callback(null, metadata);
    }
    deps = deps || {};
    const probe = typeof deps.execFile === 'function' ? deps.execFile : execFile;
    const key = filename + '|' + stats.size + '|' + stats.mtimeMs;
    if (cache.has(key)) {
        metadata.duration = cache.get(key);
        return callback(null, metadata);
    }

    const executable = path.join(path.dirname(ffmpeg), 'ffprobe.exe');
    probe(executable, ['-v', 'error', '-show_entries', 'format=duration',
        '-of', 'json', filename], {
        windowsHide: true,
        timeout: 4500,
        maxBuffer: 64 * 1024
    }, function (error, stdout) {
        let duration = '';
        if (!error) {
            try {
                const value = JSON.parse(stdout);
                duration = timeText(Number(value.format && value.format.duration));
            } catch (_error) {
                // Failed inspection should never blank thumbnails or 500 Gallery.
            }
        }
        if (cache.size >= MAX_ITEMS) cache.delete(cache.keys().next().value);
        cache.set(key, duration);
        metadata.duration = duration;
        callback(null, metadata);
    });
}

module.exports = { enrich, timeText };
