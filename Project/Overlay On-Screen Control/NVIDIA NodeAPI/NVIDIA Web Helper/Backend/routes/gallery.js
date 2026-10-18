'use strict'
// routes/gallery.js — the Gallery HTTP surface the osc page exercises
// (docs/OSC-GALLERY-IPC-EXTRACTION.md §B: the page calls the /Gallery/ HTTP
// family through the galleryEndpoints SDK in vendor.js — dual base
// node v.1.0 (setLocalConfig) and local v.1.2; listing/folder flows are
// HTTP, NOT QUERY_WIN_DIR_INFO).
//
//   POST /Gallery/v.1.2/GetFolderListing  → {directories:[name], files:[{name}]}
//   GET  /Gallery/v.1.0/EnumerateDrives   → {drives:[{name,type}]}
//   POST /Gallery/v.1.0/isDirectoryWritable → boolean JSON body
//   POST /Gallery/v.1.0/Remove            {file, forceDelete}  → 200 JSON true
//   POST /Gallery/v.1.0/CopyFile          {source, destination}→ 200 JSON true
//   POST /Gallery/v.1.0/GetImageFileDimensions {file} → {width, height}
//   POST /Gallery/v.1.0/GetThumbnail      → error floor (native thumb
//        pipeline — response field names not extractable from the page)
//   POST /Gallery/v.1.0/GetFileMetaData   → error floor (same reason)
//   POST /Gallery/v.1.0/TranscodeMediaFile / TranscodeVideoToGIF → error
//        floor (needs the Phase-3 engine; blocker recorded)
//
// Response-field authority: the page consumers (app.js:778200-781150 —
// e.directories / e.files[].name / e.drives[].name+type / e.width+e.height
// / boolean writability). Playback stays local-file HTML5 video — no media
// HTTP server, no custom scheme.

const fs = require('fs');
const path = require('path');

module.exports = function galleryRoutes(app, ctx) {
    const logger = ctx.logger;

    function dataReply(res, data) {
        res.status(200).json(data);
    }
    // production error path: replyWithError → text/html status + message
    function errorReply(res, err, httpCode) {
        res.status(httpCode || 500).type('text').send(String(err && err.message ? err.message : err));
    }

    // ── GetFolderListing (v.1.2 — the listing the gallery/picker uses) ──
    app.post('/Gallery/v.1.2/GetFolderListing', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const dir = String(body.directory || '');
        let entries;
        try {
            entries = fs.readdirSync(dir, { withFileTypes: true });
        } catch (err) {
            logger.warn('GetFolderListing failed for ' + dir + ': ' + err.message);
            errorReply(res, err);
            return;
        }
        const directories = [];
        const files = [];
        for (const entry of entries) {
            if (entry.isDirectory()) {
                directories.push(entry.name);
            } else if (body.includeFiles !== false && entry.isFile()) {
                // page consumer reads files[].name (app.js:780100 region)
                files.push({ name: entry.name });
            }
        }
        directories.sort(function (a, b) { return a.localeCompare(b); });
        files.sort(function (a, b) { return a.name.localeCompare(b.name); });
        dataReply(res, { directories: directories, files: files });
    });

    // ── EnumerateDrives ─────────────────────────────────────────────
    app.get('/Gallery/v.1.0/EnumerateDrives', function (req, res) {
        const drives = [];
        for (let code = 65; code <= 90; code++) {
            const letter = String.fromCharCode(code);
            const root = letter + ':\\';
            try {
                fs.statSync(root);
                drives.push({ name: root, type: 'fixed' });
            } catch (err) { /* no such drive */ }
        }
        dataReply(res, { drives: drives });
    });

    // ── isDirectoryWritable (page calls the lowercase route) ─────────
    app.post('/Gallery/v.1.0/isDirectoryWritable', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const dir = String(body.directory || '');
        let writable = false;
        try {
            const probe = path.join(dir, '.nvsp-write-probe-' + process.pid);
            fs.writeFileSync(probe, 'probe');
            fs.unlinkSync(probe);
            writable = true;
        } catch (err) { /* missing dir, read-only, ACL — not writable */ }
        // consumer chain (verbatim bundle evidence): vendor.js ugc-lib
        // galleryService resolves e.data.writable and the FolderBrowser
        // consumes that value (isReadOnly = !n) — the body must be the
        // {writable} OBJECT; a bare boolean reads as undefined → every
        // folder marked read-only.
        res.status(200).json({ writable: writable });
    });
    // capital-I alias — NvGalleryAPI.js:513 registers this spelling too
    app.post('/Gallery/v.1.0/IsDirectoryWritable', function (req, res) {
        req.url = '/Gallery/v.1.0/isDirectoryWritable';
        app.handle(req, res);
    });

    // ── Remove / CopyFile ───────────────────────────────────────────
    app.post('/Gallery/v.1.0/Remove', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        try {
            fs.unlinkSync(body.file);
            dataReply(res, true);
        } catch (err) {
            errorReply(res, err);
        }
    });
    app.post('/Gallery/v.1.0/CopyFile', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        try {
            fs.copyFileSync(body.source, body.destination);
            dataReply(res, true);
        } catch (err) {
            errorReply(res, err);
        }
    });

    // ── GetImageFileDimensions (page consumer reads e.width / e.height) ──
    app.post('/Gallery/v.1.0/GetImageFileDimensions', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        try {
            const dims = imageDimensions(body.file);
            dataReply(res, dims);
        } catch (err) {
            // the page marks the file corrupt on the error path (app.js:780780)
            errorReply(res, err);
        }
    });

    // ── error floors: response payloads live in NvGalleryAPINode.node ──
    // (native passthrough — field names not extractable from the page;
    // blocked per mission card "STOP that sub-part, record the blocker")
    app.post('/Gallery/v.1.0/GetThumbnail', function (req, res) {
        errorReply(res, new Error('GetThumbnail: native NvGalleryAPINode thumbnail pipeline not implemented (blocked — response shape requires runtime capture)'));
    });
    app.post('/Gallery/v.1.0/GetFileMetaData', function (req, res) {
        errorReply(res, new Error('GetFileMetaData: native NvGalleryAPINode metadata not implemented (blocked — response shape requires runtime capture)'));
    });
    app.post('/Gallery/v.1.0/TranscodeMediaFile', function (req, res) {
        errorReply(res, new Error('TranscodeMediaFile: requires the Phase-3 engine (blocked)'));
    });
    app.post('/Gallery/v.1.0/TranscodeVideoToGIF', function (req, res) {
        errorReply(res, new Error('TranscodeVideoToGIF: requires the Phase-3 engine (blocked)'));
    });
    app.post('/Gallery/v.1.0/GetFolderCRC', function (req, res) {
        errorReply(res, new Error('GetFolderCRC: native NvGalleryAPINode not implemented (blocked)'));
    });
    app.post('/Gallery/v.1.0/GetStats', function (req, res) {
        errorReply(res, new Error('GetStats: native NvGalleryAPINode not implemented (blocked)'));
    });

    // ── minimal image header parsers (PNG IHDR / JPEG SOF / GIF) ────
    function imageDimensions(file) {
        const buf = fs.readFileSync(file);
        // PNG: 8-byte signature, IHDR width/height at 16/20 (big-endian)
        if (buf.length > 24 && buf[0] === 0x89 && buf[1] === 0x50 && buf[2] === 0x4E && buf[3] === 0x47) {
            return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
        }
        // GIF: 'GIF8' — little-endian width/height at 6/8
        if (buf.length > 10 && buf[0] === 0x47 && buf[1] === 0x49 && buf[2] === 0x46) {
            return { width: buf.readUInt16LE(6), height: buf.readUInt16LE(8) };
        }
        // JPEG: scan SOFn markers (0xFFC0-0xFFCF except C4/C8/CC)
        if (buf.length > 4 && buf[0] === 0xFF && buf[1] === 0xD8) {
            let i = 2;
            while (i + 9 < buf.length) {
                if (buf[i] !== 0xFF) { i++; continue; }
                const marker = buf[i + 1];
                if (marker === 0xD8 || marker === 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                const len = buf.readUInt16BE(i + 2);
                if (marker >= 0xC0 && marker <= 0xCF && marker !== 0xC4 && marker !== 0xC8 && marker !== 0xCC) {
                    return { height: buf.readUInt16BE(i + 5), width: buf.readUInt16BE(i + 7) };
                }
                i += 2 + len;
            }
        }
        throw new Error('unsupported image format: ' + path.extname(file));
    }
};
