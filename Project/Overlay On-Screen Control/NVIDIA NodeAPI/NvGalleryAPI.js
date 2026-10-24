/* Copyright (c) 2015-2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

var fs = require('fs');
var path = require('path');
var crypto = require('crypto');
var os = require('os');
var execFile = require('child_process').execFile;
var api = require('./shims/NvGalleryAPINode.js');
var shadowPlayApi = require('./shims/NvShadowPlayAPINode.js');
var _logger;
var thumbnailJobs = {};
var playbackJobs = {};

var VIDEO_EXTENSIONS = {
    '.avi': true, '.mkv': true, '.mov': true, '.mp4': true,
    '.mpeg': true, '.mpg': true, '.webm': true, '.wmv': true
};
var IMAGE_EXTENSIONS = {
    '.bmp': true, '.gif': true, '.jpeg': true, '.jpg': true,
    '.png': true
};

//! Helper function that receives body of POST request and calls callback for that data.
//! @param req      Request object provided by Express.
//! @param callback Callback that is triggered on succesfully downloaded data.
function getPostDataAndDo(req, callback) {
    var content = ''

    function onData(data) {
        content += data;
    }

    function onEnd() {
        callback(content);
    }

    req.on('data', onData);
    req.on('end', onEnd);
}

//! Helper function converts the returned error string into a JSON object
//! @param errorType      String type of error
//! @param errorString    String of error message
//! @return               JSON object of error data
function BuildErrorObject(errorType, errorString) {
    var returnCode;
    var returnText;
    var returnMessage;
    var returnUserData;
    var parts = errorString.split("::");
    for (var i = 0; i < parts.length; i++) {
        var index = parts[i].search("Text:")
        if (index != -1) {
            returnText = parts[i].substring(index + 5);
        }
        index = parts[i].search("Code:")
        if (index != -1) {
            returnCode = parts[i].substring(index + 5);
        }
        index = parts[i].search("Message:")
        if (index != -1) {
            returnMessage = parts[i].substring(index + 8);
        }
        index = parts[i].search("UserData:")
        if (index != -1) {
            returnUserData = parts[i].substring(index + 9);
        }
    }
    var errorResult = { type: errorType, code: returnCode, codeText: returnText, message: returnMessage, userData: returnUserData };
    _logger.info(errorResult);
    return errorResult;
}

//! Helper function that receives JSON body of POST request and calls callback for the parsed data.
//! @param req      Request object provided by Express.
//! @param res      Response object provided by Express.
//! @param callback Callback that is triggered on succesfully parsed data.
function getJSONDataAndDo(req, res, callback) {
    function parseAndCallback(content) {
        var parsed = {};
        try {
            parsed = JSON.parse(content);
        }
        catch (e) {
            res.writeHead(400, { 'Content-Type': 'text/html' });
            res.end(e.name + ': ' + e.message);
            return;
        }
        callback(parsed);
    }
    getPostDataAndDo(req, parseAndCallback);
}

//! Formats the error and makes a reply with appropriate HTTP code.
//! @param res Response object provided by Express.
//! @param err Error object.
function replyWithError(res, err, httpCode) {
    if (httpCode) {
        res.writeHead(httpCode, { 'Content-Type': 'application/json' });
    }
    else if ('invalidArgument' in err) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
    }
    else {
        res.writeHead(500, { 'Content-Type': 'application/json' });
    }
    var errorResult = BuildErrorObject(err.name, err.message);
    var errorString = JSON.stringify(errorResult);
    _logger.error(errorString);
    res.end(errorString);
}

//! Helper function for using promises with nodebacks.
//! @param resolve The promise resolve.
//! @param reject  The promise reject.
function CallbackToPromise(resolve, reject) {
    return function (error, data) {
        if (error) {
            reject(error);
        }
        else {
            resolve(data);
        }
    }
};

function NativeCallbackToPromise(resolve, reject) {
    return function (error, data) {
        //
        // HACK: for some reasons node may delay resolving promises and calling .then
        // until next tick when reject or resolve is called from native addon.
        // This setImmediate forces execution to be continued in next tick.
        //
        if (error) {
            setImmediate((function () { reject(error); }));
        }
        else {
            setImmediate((function () { resolve(data); }));
        }
    }
};

function fallbackFolderListing(options, callback) {
    var directory = options && typeof options.directory === 'string'
        ? options.directory.trim()
        : '';
    if (!directory) {
        callback(new Error('Gallery folder listing requires a directory path'));
        return;
    }
    if (/^[a-zA-Z]:$/.test(directory))
        directory += '\\';

    fs.readdir(path.resolve(directory), { withFileTypes: true }, function (err, entries) {
        if (err) {
            callback(err);
            return;
        }

        var directories = [];
        var files = [];
        var excludeHidden = typeof options.excludeDirectoryType === 'string' &&
            options.excludeDirectoryType.toLowerCase().indexOf('hidden') !== -1;
        entries.forEach(function (entry) {
            if (excludeHidden && (entry.name.charAt(0) === '.' ||
                entry.name.charAt(0) === '$'))
                return;
            if (entry.isDirectory()) {
                directories.push(entry.name);
            } else if (entry.isFile()) {
                var ext=path.extname(entry.name).toLowerCase();
                var file={name:entry.name};
                if(VIDEO_EXTENSIONS[ext]||IMAGE_EXTENSIONS[ext]){file.type=VIDEO_EXTENSIONS[ext]?'video':'image';file.subtype='Normal';file.source='ShadowPlay';}
                files.push(file);
            }
        });
        directories.sort(function (a, b) { return a.localeCompare(b); });
        files.sort(function (a, b) { return a.name.localeCompare(b.name); });
        callback(null, { directories: directories, files: files });
    });
}

function readMediaFiles(directory, callback) {
    var normalizedDirectory = typeof directory === 'string' ? directory.trim() : '';
    if (!normalizedDirectory) {
        callback(new Error('Gallery media scan requires a directory path'));
        return;
    }
    if (/^[a-zA-Z]:$/.test(normalizedDirectory))
        normalizedDirectory += '\\';

    fs.readdir(path.resolve(normalizedDirectory), { withFileTypes: true }, function (err, entries) {
        if (err) {
            callback(err);
            return;
        }
        var candidates = entries.filter(function (entry) {
            var ext = path.extname(entry.name).toLowerCase();
            return entry.isFile() && (VIDEO_EXTENSIONS[ext] || IMAGE_EXTENSIONS[ext]);
        });
        if (!candidates.length) {
            callback(null, []);
            return;
        }

        var remaining = candidates.length;
        var files = [];
        var failed = false;
        candidates.forEach(function (entry) {
            var filename = path.join(normalizedDirectory, entry.name);
            fs.stat(filename, function (statError, stats) {
                if (failed) return;
                if (statError) {
                    failed = true;
                    callback(statError);
                    return;
                }
                files.push({
                    filename: filename,
                    name: entry.name,
                    type: VIDEO_EXTENSIONS[path.extname(entry.name).toLowerCase()]
                        ? 'video'
                        : 'image',
                    stats: stats
                });
                remaining--;
                if (remaining === 0) {
                    files.sort(function (a, b) {
                        return b.stats.mtimeMs - a.stats.mtimeMs;
                    });
                    callback(null, files);
                }
            });
        });
    });
}

function fallbackGalleryStats(options, callback) {
    readMediaFiles(options && options.directory, function (err, files) {
        if (err) {
            callback(err);
            return;
        }
        var videos = 0;
        var screenshots = 0;
        files.forEach(function (file) {
            if (file.type === 'video') videos++;
            else screenshots++;
        });
        callback(null, { videos: videos, screenshots: screenshots, thumbnail: null });
    });
}

function findFfmpegPath() {
    var candidates = [];
    if (process.env.NV_FFMPEG_PATH)
        candidates.push(process.env.NV_FFMPEG_PATH);
    candidates.push(path.join(__dirname, 'ffmpeg.exe'));
    candidates.push(path.resolve(__dirname, '..', '..', 'NVIDIA Capture', 'FFmpeg', 'ffmpeg.exe'));
    candidates.push(path.resolve(__dirname, '..', '..', 'FFmpeg', 'ffmpeg.exe'));
    candidates.push(path.resolve(__dirname, '..', '..', '..', 'Project',
        'ShadowPlay', 'FFmpeg Capture', 'FFmpeg', 'ffmpeg.exe'));
    candidates.push(path.resolve(__dirname, '..', '..', '..', '..', 'Project',
        'ShadowPlay', 'FFmpeg Capture', 'FFmpeg', 'ffmpeg.exe'));

    for (var i = 0; i < candidates.length; i++) {
        try {
            if (fs.statSync(candidates[i]).isFile())
                return candidates[i];
        } catch (err) {
            if (err.code !== 'ENOENT' && err.code !== 'ENOTDIR')
                _logger.error('Could not inspect FFmpeg candidate: ' + err.message);
        }
    }
    return null;
}

function prepareRecordingPlayback(media, callback, segmentStart, segmentTrack) {
    var ffmpegPath = findFfmpegPath();
    if (!ffmpegPath) {
        callback(new Error('FFmpeg executable is not available'));
        return;
    }
    var ffprobePath = path.join(path.dirname(ffmpegPath), 'ffprobe.exe');
    var cacheDir = path.join(os.tmpdir(), 'NVIDIA-ShadowPlay', 'GalleryPlayback');
    var cacheKey = crypto.createHash('sha256').update([
        media.filename,
        media.stats.size,
        media.stats.mtimeMs,
        segmentStart === undefined ? 'full' : 'segment-v7-' + segmentStart + '-' + (segmentTrack || 'both')
    ].join('|')).digest('hex');
    var cacheFile = path.join(cacheDir, cacheKey + '.webm');

    function useCacheOrPrepare() {
        fs.stat(cacheFile, function (cacheError, cacheStats) {
            if (!cacheError && cacheStats.isFile() && cacheStats.size > 0) {
                callback(null, { filename: cacheFile, stats: cacheStats });
                return;
            }
            if (cacheError && cacheError.code !== 'ENOENT') {
                callback(cacheError);
                return;
            }
            if (playbackJobs[cacheKey]) {
                playbackJobs[cacheKey].push(callback);
                return;
            }
            playbackJobs[cacheKey] = [callback];

            function finish(err, prepared) {
                var callbacks = playbackJobs[cacheKey] || [];
                delete playbackJobs[cacheKey];
                callbacks.forEach(function (done) {
                    done(err, prepared);
                });
            }

            fs.mkdir(cacheDir, { recursive: true }, function (mkdirError) {
                if (mkdirError) {
                    finish(mkdirError);
                    return;
                }

                execFile(ffprobePath, [
                    '-v', 'error',
                    '-show_entries', 'stream=codec_type,codec_name,pix_fmt',
                    '-of', 'json',
                    media.filename
                ], {
                    windowsHide: true,
                    timeout: 30000,
                    maxBuffer: 1024 * 1024
                }, function (probeError, stdout) {
                    if (probeError) {
                        finish(probeError);
                        return;
                    }

                    var streams;
                    try {
                        streams = JSON.parse(stdout).streams || [];
                    } catch (parseError) {
                        finish(parseError);
                        return;
                    }
                    if (!streams.some(function (stream) {
                        return stream.codec_type === 'video';
                    })) {
                        finish(new Error('FFmpeg found no video stream in recording'));
                        return;
                    }
                    var temporaryFile = path.join(cacheDir, cacheKey + '.' +
                        process.pid + '.' + Date.now() + '.tmp.webm');
                    var args = [
                        '-nostdin', '-v', 'error', '-y',
                        '-i', media.filename,
                        '-map', '0:v:0', '-map', '0:a:0?',
                        '-sn', '-dn',
                        '-c:v', 'libvpx',
                        '-deadline', 'realtime',
                        '-cpu-used', '8',
                        '-b:v', '4M',
                        '-c:a', 'libopus',
                        '-b:a', '128k',
                        '-f', 'webm',
                        temporaryFile
                    ];

                    if (segmentStart !== undefined) {
                        args.splice(4, 0, '-ss', String(segmentStart));
                        args[args.indexOf('-c:a') + 1] = 'libvorbis';
                        args.splice(args.length - 3, 0, '-af', 'asetpts=PTS+0.01/TB', '-g', '240', '-cluster_time_limit', '10000', '-output_ts_offset', String(segmentStart + 0.05), '-ac', '2', '-ar', '48000', '-t', '8', '-vf', 'scale=1280:720:force_original_aspect_ratio=decrease', '-r', '30', '-threads', '2');
                    }

                    if (segmentStart !== undefined && segmentTrack === 'video') {
                        var audioMap=args.indexOf('0:a:0?');
                        if(audioMap>=0)args.splice(audioMap-1,2);
                        args.splice(args.length-3,0,'-an');
                    }
                    if (segmentStart !== undefined && segmentTrack === 'audio') {
                        args=['-nostdin','-v','error','-y','-ss',String(segmentStart),'-i',media.filename,
                            '-map','0:a:0','-vn','-t','8','-c:a','libvorbis','-ac','2','-ar','48000','-af','asetpts=PTS+0.01/TB',
                            '-b:a','128k','-output_ts_offset',String(segmentStart+0.05),'-cluster_time_limit','10000','-f','webm',temporaryFile];
                    }

                    function startTranscode() {
                        execFile(ffmpegPath, args, {
                            windowsHide: true,
                            timeout: 30 * 60 * 1000,
                            maxBuffer: 1024 * 1024
                        }, function (transcodeError) {
                            if (transcodeError) {
                                fs.unlink(temporaryFile, function () {});
                                finish(transcodeError);
                                return;
                            }
                            fs.stat(temporaryFile, function (statError, outputStats) {
                                if (statError || !outputStats.isFile() ||
                                    outputStats.size === 0) {
                                    fs.unlink(temporaryFile, function () {});
                                    finish(statError || new Error(
                                        'FFmpeg produced an empty playback file'));
                                    return;
                                }
                                fs.rename(temporaryFile, cacheFile, function (renameError) {
                                    if (renameError) {
                                        fs.unlink(temporaryFile, function () {});
                                        finish(renameError);
                                        return;
                                    }
                                    finish(null, {
                                        filename: cacheFile,
                                        stats: outputStats
                                    });
                                });
                            });
                        });
                    }

                    fs.unlink(cacheFile, function (unlinkError) {
                        if (unlinkError && unlinkError.code !== 'ENOENT') {
                            finish(unlinkError);
                            return;
                        }
                        startTranscode();
                    });
                });
            });
        });
    }

    fs.stat(ffprobePath, function (probePathError, probePathStats) {
        if (probePathError || !probePathStats.isFile()) {
            callback(probePathError || new Error('FFprobe executable is unavailable'));
            return;
        }
        useCacheOrPrepare();
    });
}

function thumbnailDataUri(filename, stats, requestedWidth, callback) {
    var width = parseInt(requestedWidth, 10);
    if (!isFinite(width) || width < 64) width = 320;
    width = Math.min(width, 640);
    var key = crypto.createHash('sha256').update([
        path.resolve(filename),
        stats.size,
        stats.mtimeMs,
        width
    ].join('|')).digest('hex');
    var cacheDir = path.join(os.tmpdir(), 'NVIDIA-ShadowPlay', 'GalleryThumbnails');
    var cacheFile = path.join(cacheDir, key + '.png');
    var mimeType = 'data:image/png;base64,';

    function returnCached() {
        fs.readFile(cacheFile, function (err, image) {
            if (err) {
                finish(err);
                return;
            }
            finish(null, mimeType + image.toString('base64'));
        });
    }

    if (thumbnailJobs[key]) {
        thumbnailJobs[key].push(callback);
        return;
    }
    thumbnailJobs[key] = [callback];

    function finish(err, data) {
        var callbacks = thumbnailJobs[key] || [];
        delete thumbnailJobs[key];
        callbacks.forEach(function (done) {
            done(err, data);
        });
    }

    fs.readFile(cacheFile, function (cacheError, cachedImage) {
        if (!cacheError) {
            finish(null, mimeType + cachedImage.toString('base64'));
            return;
        }
        if (cacheError.code !== 'ENOENT') {
            finish(cacheError);
            return;
        }

        var ffmpegPath = findFfmpegPath();
        if (!ffmpegPath) {
            finish(new Error('FFmpeg executable is not available'));
            return;
        }
        fs.mkdir(cacheDir, { recursive: true }, function (mkdirError) {
            if (mkdirError) {
                finish(mkdirError);
                return;
            }

            var tempFile = path.join(cacheDir, key + '.' + process.pid + '.' +
                Date.now() + '.tmp.png');
            var isVideo = VIDEO_EXTENSIONS[path.extname(filename).toLowerCase()] === true;

            function generate(seekSeconds) {
                var args = ['-nostdin', '-v', 'error', '-y'];
                if (isVideo) args.push('-ss', String(seekSeconds));
                args.push('-i', filename, '-map', '0:v:0', '-frames:v', '1',
                    '-vf', 'scale=' + width + ':-2', '-f', 'image2', '-vcodec', 'png',
                    tempFile);
                execFile(ffmpegPath, args, {
                    windowsHide: true,
                    timeout: 15000,
                    maxBuffer: 1024 * 1024
                }, function (runError) {
                    if (runError) {
                        fs.unlink(tempFile, function (unlinkError) {
                            if (unlinkError && unlinkError.code !== 'ENOENT')
                                _logger.error('Could not remove failed thumbnail: ' + unlinkError.message);
                            if (isVideo && seekSeconds !== 0) {
                                generate(0);
                                return;
                            }
                            finish(runError);
                        });
                        return;
                    }
                    fs.rename(tempFile, cacheFile, function (renameError) {
                        if (renameError) {
                            fs.unlink(tempFile, function () {});
                            finish(renameError);
                            return;
                        }
                        returnCached();
                    });
                });
            }

            generate(isVideo ? 1 : 0);
        });
    });
}

function fallbackRecentFiles(count, callback) {
    function onPaths(err, paths) {
        if (err) {
            callback(err);
            return;
        }
        if (!paths || typeof paths.videos !== 'string' || !paths.videos.trim()) {
            callback(new Error('Gallery recording path is unavailable'));
            return;
        }
        // Recordings may live under Videos/Captures, Videos/NVIDIA, or
        // per-game subfolders. Use a bounded scanner, never symlink traversal.
        require('./shims/gallery-recent-scanner.js').recent(paths.videos, count, callback);
    }

    try {
        shadowPlayApi.RecordingPaths(onPaths, false);
    } catch (err) {
        callback(err);
    }
}

function resolveRecordingMedia(filename, callback) {
    if (typeof filename !== 'string' || !filename.trim()) {
        callback({ statusCode: 400, message: 'A recording file path is required' });
        return;
    }

    var requestedPath = path.resolve(filename.trim());
    shadowPlayApi.RecordingPaths(function (settingsError, settings) {
        if (settingsError) {
            callback(settingsError);
            return;
        }
        if (!settings || typeof settings.videos !== 'string' || !settings.videos.trim()) {
            callback({ statusCode: 500, message: 'The recording folder is not configured' });
            return;
        }

        fs.realpath(settings.videos, function (rootError, realRoot) {
            if (rootError) {
                callback(rootError);
                return;
            }
            fs.realpath(requestedPath, function (fileError, realFilename) {
                if (fileError) {
                    callback({ statusCode: fileError.code === 'ENOENT' ? 404 : 403,
                        message: 'Recording file is unavailable' });
                    return;
                }
                var relative = path.relative(realRoot, realFilename);
                if (!relative || relative === '..' ||
                    relative.indexOf('..' + path.sep) === 0 ||
                    path.isAbsolute(relative)) {
                    callback({ statusCode: 403, message: 'Playback is limited to files inside the recording folder' });
                    return;
                }

                fs.stat(realFilename, function (statError, stats) {
                    if (statError) {
                        callback(statError);
                        return;
                    }
                    if (!stats.isFile()) {
                        callback({ statusCode: 404, message: 'Recording is not a file' });
                        return;
                    }
                    callback(null, { filename: realFilename, stats: stats });
                });
            });
        });
    });
}

function fallbackFileMetadata(options, callback) {
    var filename = options && typeof options.file === 'string' ? options.file.trim() : '';
    if (!filename) {
        callback(new Error('Gallery metadata requires a file path'));
        return;
    }
    fs.stat(filename, function (err, stats) {
        if (err) {
            callback(err);
            return;
        }
        var ext = path.extname(filename).toLowerCase();
        var isVideo = VIDEO_EXTENSIONS[ext] === true;
        if (!isVideo && IMAGE_EXTENSIONS[ext] !== true) {
            callback(new Error('Unsupported Gallery media file: ' + filename));
            return;
        }
        var metadata = {
            fileType: isVideo ? 'video' : 'image',
            fileSubType: 'Normal',
            fileSource: 'ShadowPlay',
            date: stats.mtime.toLocaleString('en-US'),
            duration: '',
            fileSizeMB: stats.size / (1024 * 1024),
            audiotype: 'Unknown',
            hevc: '0',
            DRSName: '',
            DRSProfileName: '',
            thumbnail: null
        };
        thumbnailDataUri(filename, stats, options.width, function (thumbnailError, thumbnail) {
            if (thumbnailError) {
                _logger.error('Gallery thumbnail generation failed: ' + thumbnailError.message);
            } else {
                metadata.thumbnail = thumbnail;
            }
            require('./shims/gallery-media-metadata.js').enrich(metadata, filename, stats,
                isVideo ? findFfmpegPath() : null, callback);
        });
    });
}

function fallbackDriveEnumeration(callback) {
    var drives = [];
    for (var code = 65; code <= 90; code++) {
        var root = String.fromCharCode(code) + ':\\';
        try {
            if (fs.statSync(root).isDirectory()) {
                drives.push({
                    name: root,
                    type: code === 65 || code === 66 ? 'removable' : 'fixed'
                });
            }
        } catch (err) {
            if (err.code !== 'ENOENT' && err.code !== 'ENOTDIR')
                _logger.error('Could not enumerate drive ' + root + ': ' + err.message);
        }
    }
    callback(null, { drives: drives });
}

function fallbackDirectoryWritable(options, callback) {
    var directory = options && typeof options.directory === 'string'
        ? options.directory.trim()
        : '';
    if (/^[a-zA-Z]:$/.test(directory))
        directory += '\\';
    if (!directory) {
        callback(null, { writable: false });
        return;
    }
    fs.access(path.resolve(directory), fs.constants.W_OK, function (err) {
        callback(null, { writable: !err });
    });
}

function RegisterExpressEndpoints(app, logger) {
    //! Gets all files in a directory. 
    app.post('/Gallery/v.1.2/GetFolderListing', function (req, res) {
        var options = {};
        function doReply(err, data) {
            if (err || !data || !Array.isArray(data.directories) ||
                !Array.isArray(data.files) ||
                (data.directories.length === 0 && data.files.length === 0)) {
                if (!err)
                    logger.info('Gallery native folder listing unavailable; using filesystem listing');
                else
                    logger.info('Gallery native folder listing failed; using filesystem listing: ' +
                        err.message);
                fallbackFolderListing(options, function (fallbackError, fallbackData) {
                    if (fallbackError) {
                        replyWithError(res, fallbackError);
                        return;
                    }
                    logger.info(JSON.stringify(fallbackData));
                    res.writeHead(200, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify(fallbackData));
                });
                return;
            }
            logger.info(JSON.stringify(data));
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify(data));
        }
        getJSONDataAndDo(req, res, function (content) {
            options = content;
            try {
                api.GalleryGetFolderListing12(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    // Gets the count of the number of screenshots/videos in a directory
    app.post('/Gallery/v.1.0/GetStats', function (req, res) {
        var options = {};
        function doReply(err, data) {
            if (!err && data && typeof data.videos === 'number' &&
                typeof data.screenshots === 'number' &&
                (data.videos > 0 || data.screenshots > 0)) {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
                return;
            }
            logger.info('Gallery native statistics unavailable; using filesystem statistics' +
                (err ? ': ' + err.message : ''));
            fallbackGalleryStats(options, function (fallbackError, fallbackData) {
                if (fallbackError) {
                    replyWithError(res, fallbackError);
                    return;
                }
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(fallbackData));
            });
        }
        getJSONDataAndDo(req, res, function (content) {
            options = content;
            try {
                api.GalleryGetStats(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Gets "count" most recent items in the gallery.
    app.get('/Gallery/v.1.0/Recent/:count', function (req, res) {
        function doReply(err, data) {
            if (!err && data && Array.isArray(data.files) && data.files.length > 0) {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
                return;
            }
            logger.info('Gallery native recent files unavailable; using filesystem scan' +
                (err ? ': ' + err.message : ''));
            fallbackRecentFiles(req.params.count, function (fallbackError, fallbackData) {
                if (fallbackError) {
                    replyWithError(res, fallbackError);
                    return;
                }
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(fallbackData));
            });
        }

        try {
            api.GalleryGetRecent(doReply, parseInt(req.params.count));
        }
        catch (err) {
            replyWithError(res, err);
            console.error(err);
        }
    });

    app.get('/Gallery/v.1.0/PlaybackInfo', function(req,res) {
        resolveRecordingMedia(req.query && req.query.file, function(err,media) {
            if(err){res.status(err.statusCode||500).json({error:err.message});return;}
            var ffmpeg=findFfmpegPath();
            if(!ffmpeg){res.status(503).json({error:'Playback tools are unavailable'});return;}
            execFile(path.join(path.dirname(ffmpeg),'ffprobe.exe'),['-v','error','-show_entries','format=duration:stream=codec_type','-of','json',media.filename],
                {windowsHide:true,timeout:15000,maxBuffer:1024*1024},function(error,stdout) {
                    try {
                        if(error)throw error;
                        var data=JSON.parse(stdout),duration=Number(data.format&&data.format.duration);
                        if(!(duration>0))throw new Error('Video duration is unavailable');
                        res.json({duration:duration,segmentDuration:8,audio:(data.streams||[]).some(function(s){return s.codec_type==='audio';})});
                    }catch(e){res.status(500).json({error:'Unable to inspect video'});}
                });
        });
    });

    app.get('/Gallery/v.1.0/Media', function (req, res) {
        var segmentStart=req.query && req.query.segment!==undefined ? Number(req.query.segment) : undefined;
        var segmentTrack=req.query&&req.query.track;
        if(segmentTrack&&segmentTrack!=='video'&&segmentTrack!=='audio'){res.status(400).end('Invalid track');return;}
        if(segmentStart!==undefined && (!isFinite(segmentStart)||segmentStart<0)){res.status(400).end('Invalid segment');return;}
        resolveRecordingMedia(req.query && req.query.file, function (err, media) {
            if (err) {
                res.writeHead(err.statusCode || 500, { 'Content-Type': 'text/plain' });
                res.end(err.message || 'Unable to open recording');
                return;
            }

            prepareRecordingPlayback(media, function (prepareError, playback) {
                if (prepareError) {
                    _logger.error('Gallery FFmpeg playback preparation failed: ' +
                        prepareError.message);
                    res.writeHead(500, { 'Content-Type': 'text/plain' });
                    res.end('Unable to prepare video playback');
                    return;
                }

                var headers = {
                    'Accept-Ranges': 'bytes',
                    'Cache-Control': 'no-store',
                    'Content-Type': 'video/webm'
                };
                var range = req.headers && req.headers.range;
                var start = 0;
                var end = playback.stats.size - 1;
                var statusCode = 200;

                if (range) {
                    var match = /^bytes=(\d*)-(\d*)$/.exec(range);
                    if (!match || (!match[1] && !match[2]) || playback.stats.size === 0) {
                        res.writeHead(416, {
                            'Content-Range': 'bytes */' + playback.stats.size
                        });
                        res.end();
                        return;
                    }
                    if (!match[1]) {
                        var suffixLength = parseInt(match[2], 10);
                        start = Math.max(playback.stats.size - suffixLength, 0);
                    } else {
                        start = parseInt(match[1], 10);
                        if (match[2]) end = parseInt(match[2], 10);
                    }
                    if (start >= playback.stats.size || end < start) {
                        res.writeHead(416, {
                            'Content-Range': 'bytes */' + playback.stats.size
                        });
                        res.end();
                        return;
                    }
                    end = Math.min(end, playback.stats.size - 1);
                    statusCode = 206;
                    headers['Content-Range'] = 'bytes ' + start + '-' + end +
                        '/' + playback.stats.size;
                }

                headers['Content-Length'] = end - start + 1;
                res.writeHead(statusCode, headers);
                if (req.method === 'HEAD') {
                    res.end();
                    return;
                }
                var stream = fs.createReadStream(playback.filename, {
                    start: start,
                    end: end
                });
                stream.on('error', function (streamError) {
                    _logger.error('Gallery playback stream failed: ' + streamError.message);
                    if (!res.headersSent) res.writeHead(500);
                    res.end();
                });
                stream.pipe(res);
            }, segmentStart, segmentTrack);
        });
    });

    //! Clears the recent list.
    app.post('/Gallery/v.1.0/Recent/Clear', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end(JSON.stringify(data));
            }
        }

        try {
            api.GalleryClearUploadHistory(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
            console.error(err);
        }
    });

    //! Gets upload history.
    app.get('/Gallery/v.1.0/Upload/History', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        try {
            api.GalleryGetUploadHistory(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            console.error(err);
        }
    });

    //! Adds new upload history item.
    app.post('/Gallery/v.1.0/Upload/History', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end(JSON.stringify(data));
            }
        }
        getJSONDataAndDo(req, res, function (content) {
            try {
                api.GalleryAddUploadHistoryItem(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Removes an item from the upload history.
    app.post('/Gallery/v.1.0/Upload/History/Remove', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end(JSON.stringify(data));
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.GalleryRemoveUploadHistory(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Clears the entire upload history.
    app.post('/Gallery/v.1.0/Upload/History/Clear', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end(JSON.stringify(data));
            }
        }

        try {
            api.GalleryClearUploadHistory(doReply, true);
        }
        catch (err) {
            replyWithError(res, err);
            console.error(err);
        }
    });

    //! Removes videos from the fs/gallery.
    app.post('/Gallery/v.1.0/Remove', function (req, res) {
        // The standalone native shim does not delete files. Do not claim
        // success for a destructive operation that was never performed.
        res.status(501).json({
            type: 'Error', codeText: 'GALLERY_DELETE_UNAVAILABLE',
            message: 'Safe Gallery deletion is not implemented; no file was deleted'
        });
    });

    //! Gets metadata about a screenshot/video.
    app.post('/Gallery/v.1.0/GetFileMetaData', function (req, res) {
        var options = {};
        function doReply(err, data) {
            if (!err && data && typeof data.fileType === 'string' &&
                typeof data.fileSubType === 'string' &&
                typeof data.thumbnail === 'string' && data.thumbnail.length > 0) {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
                return;
            }
            logger.info('Gallery native file metadata unavailable; using filesystem metadata' +
                (err ? ': ' + err.message : ''));
            fallbackFileMetadata(options, function (fallbackError, fallbackData) {
                if (fallbackError) {
                    replyWithError(res, fallbackError);
                    return;
                }
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(fallbackData));
            });
        }
        getJSONDataAndDo(req, res, function (content) {
            options = content;
            try {
                api.GalleryGetMetaData(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Gets a thumbnail for a file.
    app.post('/Gallery/v.1.0/GetThumbnail', function (req, res) {
        var options = {};
        function doReply(err, data) {
            if (!err && data && typeof data.thumbnail === 'string' &&
                data.thumbnail.length > 0) {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
                return;
            }
            logger.info('Gallery native thumbnail unavailable; using filesystem thumbnail generation' +
                (err ? ': ' + err.message : ''));
            fallbackFileMetadata(options, function (fallbackError, fallbackData) {
                if (fallbackError) {
                    replyWithError(res, fallbackError);
                    return;
                }
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ thumbnail: fallbackData.thumbnail }));
            });
        }

        getJSONDataAndDo(req, res, function (content) {
            options = content;
            try {
                api.GalleryGetThumbnail(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    app.post('/Gallery/v.1.0/GetImageFileDimensions', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            require('./shims/gallery-image-dimensions.js').read(content,
                function (error, size) {
                    if (error) {
                        res.status(error.statusCode || 500).json({
                            type: 'Error', codeText: error.codeText || 'GALLERY_IMAGE_READ_FAILED'
                        });
                        return;
                    }
                    res.status(200).json(size);
                });
        });
    });

    app.post('/Gallery/v.1.0/TranscodeMediaFile', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.GalleryTranscodeMediaFile(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    app.post('/Gallery/v.1.0/TranscodeVideoToGIF', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.GalleryTranscodeMediaFile(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Gets system drive information.
    app.get('/Gallery/v.1.0/EnumerateDrives', function (req, res) {
        function doReply(err, data) {
            if (err || !data || !Array.isArray(data.drives) || data.drives.length === 0) {
                if (!err)
                    logger.info('Gallery native drive enumeration unavailable; using filesystem enumeration');
                else
                    logger.info('Gallery native drive enumeration failed; using filesystem enumeration: ' +
                        err.message);
                fallbackDriveEnumeration(function (fallbackError, fallbackData) {
                    if (fallbackError) {
                        replyWithError(res, fallbackError);
                        return;
                    }
                    res.writeHead(200, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify(fallbackData));
                });
                return;
            }
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify(data));
        }

        try {
            api.GalleryEnumerateDrives(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            console.error(err);
        }
    });

    //! Gets the CRC for a folder and all of its subfolders.
    app.post('/Gallery/v.1.0/GetFolderCRC', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.GalleryGetFolderCRC(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Copies an NV file to another location
    app.post('/Gallery/v.1.0/CopyFile', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            // No shim-based fake success. Copy only verified Gallery media,
            // and never overwrite an existing destination.
            require('./shims/gallery-copy-file.js').copy(
                content, resolveRecordingMedia, function (error, result) {
                    if (error) {
                        res.status(error.statusCode || 500).json({
                            type: 'Error', codeText: error.codeText || 'GALLERY_COPY_FAILED'
                        });
                        return;
                    }
                    res.status(200).json(result);
                });
        });
    });

    // Check if a directory is writable
    app.post('/Gallery/v.1.0/IsDirectoryWritable', function (req, res) {
        var options = {};
        function doReply(err, data) {
            if (!err && data && typeof data.writable === 'boolean') {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
                return;
            }
            logger.info('Gallery native directory writability unavailable; using filesystem check' +
                (err ? ': ' + err.message : ''));
            fallbackDirectoryWritable(options, function (fallbackError, fallbackData) {
                if (fallbackError) {
                    replyWithError(res, fallbackError);
                    return;
                }
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(fallbackData));
            });
        }
        getJSONDataAndDo(req, res, function (content) {
            options = content;
            try {
                api.GalleryIsDirectoryWritable(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Decrypts and writes an encrypted bmp file to temp folder.
    app.post('/Gallery/v.1.0/WriteEncryptedBmp', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.GalleryWriteEncryptedBmp(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

}


module.exports = function (app, io, logger) {
    if (app === undefined || io === undefined || logger == undefined) {
        throw 'You need to provide express app, socket io and logging';
    }

    //! Allows global access to logging module
    _logger = logger;

    //! This must be the last declaration in the file. Anything below it will be masked.
    return {
        version: function () {
            return api.GetVersion();
        },
        initialize: function () {
            return new Promise(function CreateInitializationPromise(resolve, reject) {
                api.Initialize(NativeCallbackToPromise(resolve, reject));
            }).then(function OnNativeAPIInitialized() {
                RegisterExpressEndpoints(app, logger);
                logger.info('NvGalleryAPI module initialized');
            });
        }
    };
};
