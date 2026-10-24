'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const probe = require('../shims/gallery-media-metadata');

test('duration uses Gallery native HH:MM:SS string contract', () => {
    assert.equal(probe.timeText(50.76), '00:00:50');
    assert.equal(probe.timeText(3661.2), '01:01:01');
    assert.equal(probe.timeText(0), '');
    assert.equal(probe.timeText(NaN), '');
    assert.equal(probe.timeText(Infinity), '');
});

function enrich(metadata, filename, stats, command) {
    return new Promise((resolve, reject) =>
        probe.enrich(metadata, filename, stats, '/tools/ffmpeg.exe',
            (error, body) => error ? reject(error) : resolve(body),
            { execFile: command }));
}

test('reads duration from FFprobe with no media edits and caches it', async () => {
    let commands = 0;
    const execFile = (exe, args, opts, callback) => {
        commands++;
        assert.equal(path.basename(exe), 'ffprobe.exe');
        assert.deepEqual(args.slice(0, 4), ['-v', 'error', '-show_entries', 'format=duration']);
        assert.equal(opts.windowsHide, true);
        callback(null, '{"format":{"duration":"50.76"}}', '');
    };
    const stats = { size: 64000, mtimeMs: 110 };
    const first = await enrich({ fileType: 'video', duration: '' },
        '/test/gallery-duration-uniqa.mp4', stats, execFile);
    assert.equal(first.duration, '00:00:50');
    const second = await enrich({ fileType: 'video', duration: '' },
        '/test/gallery-duration-uniqa.mp4', stats, execFile);
    assert.equal(second.duration, '00:00:50');
    assert.equal(commands, 1, 'cache must avoid second probe');
    const changed = await enrich({ fileType: 'video', duration: '' },
        '/test/gallery-duration-uniqa.mp4', { size: 64001, mtimeMs: 120 }, execFile);
    assert.equal(changed.duration, '00:00:50');
    assert.equal(commands, 2, 'cache invalidates for modified recording');
});

test('probe errors fail open for Gallery metadata but never claim fake duration', async () => {
    const result = await enrich({ fileType: 'video', duration: '', thumbnail: 'valid' },
        '/test/broken-gallery-uniqb.mp4', { size: 4, mtimeMs: 20 },
        (exe, args, opts, callback) => callback(new Error('invalid file')));
    assert.equal(result.duration, '');
    assert.equal(result.thumbnail, 'valid');
});

test('images do not invoke FFprobe', async () => {
    const result = await enrich({ fileType: 'image', duration: '' },
        '/test/shot-uniqc.png', { size: 10, mtimeMs: 22 },
        () => { throw Error('FFprobe must not be invoked for images'); });
    assert.equal(result.duration, '');
});

test('malformed and maliciously huge duration values are rejected', async () => {
    const cases = ['invalid', '{"format":{"duration":"Infinity"}}',
        '{"format":{"duration":"999999999999"}}'];
    for (let i = 0; i < cases.length; i++) {
        const text = cases[i];
        const result = await enrich({ fileType: 'video', duration: '' },
            '/test/badformat-uniqd-' + i + '.mp4', { size: 14, mtimeMs: i },
            (exe, args, opts, callback) => callback(null, text));
        assert.equal(result.duration, '');
    }
});
