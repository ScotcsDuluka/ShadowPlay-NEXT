/* Copyright (c) 2015-2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

var api = require('./shims/NvShadowPlayAPINode.js');

var _logger;

//! Helper function that receives body of POST request and calls callback for that data.
//! @param req      Request object provided by Express.
//! @param callback Callback that is triggered on succesfully downloaded data.
function getPostDataAndDo(req, callback) {
    var content = ''

    function onData(data) {
        content += data;
        _logger.info('Post Data: ' + content);
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
    var parts = errorString.split("::");
    for (var i = 0; i < parts.length; i++) {
        var index = parts[i].search("Text:")
        if (index != -1) {
            returnText = parts[i].substring(index + 5);
        }
        index = parts[i].search("Code:")
        if (index != -1) {
            returnCode = Number(parts[i].substring(index + 5));
        }
        index = parts[i].search("Message:")
        if (index != -1) {
            returnMessage = parts[i].substring(index + 8);
        }
    }
    var errorResult = { type: errorType, code: returnCode, codeText: returnText, message: returnMessage };
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
            res.writeHead(400, { 'Content-Type': 'application/json' });
            var errorResult = BuildErrorObject(e.name, e.message);
            var errorString = JSON.stringify(errorResult);
            _logger.error(errorString);
            res.end(errorString);
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

//! Formats the error and makes a reply with appropriate HTTP code.
//! @param res Response object provided by Express.
//! @param err Error object.
function replyWithErrorObject(res, errorObject, httpCode) {
    if (httpCode) {
        res.writeHead(httpCode, { 'Content-Type': 'application/json' });
    }
    else {
        res.writeHead(400, { 'Content-Type': 'application/json' });
    }

    var errorString = JSON.stringify(errorObject);
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

function RegisterExpressEndpoints(app, io, logger) {

    //////////////////////////////////////////////////////////////////////////////////
    // Instant Replay

    //! Enables/disables Instant Replay
    //! send "{"status":true/false}" for enable/disable in request body
    app.post('/ShadowPlay/v.1.0/InstantReplay/Enable', function (req, res) {
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
                var enabled;
                try {
                    enabled = content.status;
                    if (enabled == undefined) {
                        replyWithErrorObject(res, { type: "Error", code: "3", codeText: "Invalid argument", message: "Status is not defined" });
                        return;
                    }
                } catch (err) {
                    replyWithError(res, err);
                    return;
                }
                if (enabled) {
                    logger.info('Instant Replay enable request');
                    api.InstantReplayAction(doReply, 3);
                }
                else {
                    logger.info('Instant Replay disable request');
                    api.InstantReplayAction(doReply, 4);
                }
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets the enable/disable state of Instant Replay
    //! Returns "{"status":true/false}" for enable/disable
    app.get('/ShadowPlay/v.1.0/InstantReplay/Enable', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Instant Replay Enable:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetInstantReplayStatus(doReply, "enable");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the running state of Instant Replay
    //! Returns "{"running":true/false}" for running/not running
    app.get('/ShadowPlay/v.1.0/InstantReplay/Running', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Instant Replay Running:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetInstantReplayStatus(doReply, "running");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the Instant Replay settings
    app.get('/ShadowPlay/v.1.0/InstantReplay/Settings', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Instant Replay Settings:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        try {
            api.GetInstantReplaySettings(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Saves Instant Replay settings
    app.post('/ShadowPlay/v.1.0/InstantReplay/Settings', function (req, res) {
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
                logger.info('Instant Replay settings post');
                api.SetInstantReplaySettings(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Saves Instant Replay
    app.post('/ShadowPlay/v.1.0/InstantReplay/Save', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        try {
            // Instant Replay Save requires no request data
            logger.info('Instant Replay save request');
            api.InstantReplayAction(doReply, 5);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the Instant Replay buffer length, in seconds.
    app.get('/ShadowPlay/v.1.0/InstantReplay/BufferLength', function (req, res) {
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
            api.GetInstantReplayBufferLength(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets data for OSC's Instant Replay Customize view
    app.post('/ShadowPlay/v1.0/OSC/GetCustomize/InstantReplay', function (req, res) {
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
                api.GetInstantReplayCustomizeData(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //////////////////////////////////////////////////////////////////////////////////
    // Manual Record

    //! Enables/disables Manual Record
    //! send "{"status":true/false}" for enable(start)/disable(stop and save) in request body
    app.post('/ShadowPlay/v.1.0/Record/Enable', function (req, res) {
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
                logger.info('Manual Record enable request');
                api.ManualRecordEnable(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets the enable/disable state of Manual Record
    //! Returns "{"status":true/false}" for enable/disable
    app.get('/ShadowPlay/v.1.0/Record/Enable', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Manual Record Enable:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetManualRecordStatus(doReply, "enable");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the running state of Manual Record
    //! Returns "{"running":true/false}" for running/not running
    app.get('/ShadowPlay/v.1.0/Record/Running', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Manual Record Running:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetManualRecordStatus(doReply, "running");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the Manual Record settings
    app.get('/ShadowPlay/v.1.0/Record/Settings', function (req, res) {
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
            api.GetManualRecordSettings(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Saves Manual Record settings
    app.post('/ShadowPlay/v.1.0/Record/Settings', function (req, res) {
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
                logger.info('Manual Record settings post');
                api.SetManualRecordSettings(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets the Manual Record Concurrency support with Broadcast or Gamecast
    app.get('/ShadowPlay/v.1.0/Record/Concurrency/:mode', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Concurrency support for ' + req.params.mode + ':' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            logger.info('Getting concurrency support for ' + req.params.mode);
            api.ManualRecordConcurrencySupport(doReply, req.params.mode);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets data for OSC's Manual Record Customize view
    app.post('/ShadowPlay/v1.0/OSC/GetCustomize/Record', function (req, res) {
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
                api.GetManualRecordCustomizeData(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //////////////////////////////////////////////////////////////////////////////////
    // Broadcast

    //! Enables/disables Broadcast
    //! send "{"status":true/false}" for enable(start)/disable(stop and save) in request body
    app.post('/ShadowPlay/v.1.0/Broadcast/Enable', function (req, res) {
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
                var enabled;
                try {
                    enabled = content.status;
                    if (enabled == undefined) {
                        replyWithErrorObject(res, { type: "Error", code: "3", codeText: "Invalid argument", message: "Status is not defined" });
                        return;
                    }
                } catch (err) {
                    replyWithError(res, err);
                    return;
                }
                if (enabled) {
                    logger.info('Broadcast enable request');
                    api.BroadcastAction(doReply, 6);
                }
                else {
                    logger.info('Broadcast disable request');
                    api.BroadcastAction(doReply, 7);
                }
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Pauses/resumes Broadcast
    //! send "{"pause":true/false}" for enable(start)/disable(stop and save) in request body
    app.post('/ShadowPlay/v.1.0/Broadcast/Pause', function (req, res) {
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
                var pause;
                try {
                    pause = content.status;
                    if (pause == undefined) {
                        replyWithErrorObject(res, { type: "Error", code: "3", codeText: "Invalid argument", message: "Pause is not defined" });
                        return;
                    }
                } catch (err) {
                    replyWithError(res, err);
                    return;
                }
                if (pause) {
                    logger.info('Broadcast pause request');
                    api.BroadcastAction(doReply, 8);
                }
                else {
                    logger.info('Broadcast resume request');
                    api.BroadcastAction(doReply, 9);
                }
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets the enable/disable state of Broadcast
    //! Returns "{"status":true/false}" for enable/disable
    app.get('/ShadowPlay/v.1.0/Broadcast/Enable', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Broadcast Enable:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetBroadcastStatus(doReply, "enable");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the running state of Broadcast
    //! Returns "{"running":true/false}" for running/not running
    app.get('/ShadowPlay/v.1.0/Broadcast/Running', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Broadcast Running:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetBroadcastStatus(doReply, "running");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the supported state of Broadcast
    app.get('/ShadowPlay/v.1.0/Broadcast/Support', function (req, res) {
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
            api.GetBroadcastSupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the Broadcast settings
    app.get('/ShadowPlay/v.1.0/Broadcast/Settings', function (req, res) {
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
            api.GetBroadcastSettings(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Saves Broadcast settings
    app.post('/ShadowPlay/v.1.0/Broadcast/Settings', function (req, res) {
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
                logger.info('Broadcast settings post');
                api.SetBroadcastSettings(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Saves BroadcastSession parameters such as URL
    app.post('/ShadowPlay/v.1.0/Broadcast/SessionParam', function (req, res) {
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
                api.SetBroadcastSessionParam(doReply, 0, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Saves BroadcastSession parameters such as URL
    app.post('/ShadowPlay/v.1.1/Broadcast/SessionParam/:type', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.type) {
            var err = new Error('"type" string argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetBroadcastSessionParam(doReply, 1, req.params.type, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets last broadcasted portal[provider] option
    app.get('/ShadowPlay/v.1.0/Broadcast/LastProvider', function (req, res) {
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
            api.GetBroadcastLastPortal(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Saves last broadcasted portal[provider] option
    app.post('/ShadowPlay/v.1.0/Broadcast/LastProvider', function (req, res) {
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
                api.SetBroadcastLastPortal(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets broadcast portal[provider] option
    app.get('/ShadowPlay/v.1.0/Broadcast/Provider', function (req, res) {
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
            api.BroadcastProvider(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Saves broadcast portal[provider] option
    app.post('/ShadowPlay/v.1.0/Broadcast/Provider', function (req, res) {
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
                api.BroadcastProvider(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets broadcast title
    app.get('/ShadowPlay/v.1.0/Broadcast/Title', function (req, res) {
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
            api.GetBroadcastTitle(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Updates the broadcast viewer count Image
    app.post('/ShadowPlay/v.1.0/Broadcast/Viewers', function (req, res) {
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
                api.SetBroadcastViewerCountImage(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Changes the broadcast maximum viewer count
    app.post('/ShadowPlay/v.1.0/Broadcast/Viewers/Max', function (req, res) {
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
                logger.info('Max viewer count: ' + JSON.stringify(content));
                api.SetBroadcastMaxViewerCount(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });


    //! Sets broadcast(twitch) ingest server
    app.post('/ShadowPlay/v.1.0/Broadcast/IngestServer', function (req, res) {
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
                logger.info('Broadcast Ingest Server: ' + JSON.stringify(content));
                api.BroadcastTwitchIngestServer(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets broadcast(twitch) ingest server
    app.get('/ShadowPlay/v.1.0/Broadcast/IngestServer', function (req, res) {
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
            api.BroadcastTwitchIngestServer(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets broadcast 2K support
    app.get('/ShadowPlay/v.1.0/Broadcast/2KSupport', function (req, res) {
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
            api.GetBroadcast2KSupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets broadcast 2K enable
    app.get('/ShadowPlay/v.1.0/Broadcast/2KEnable', function (req, res) {
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
            api.GetBroadcast2KEnable(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets Facebook Live support
    app.get('/ShadowPlay/v.1.0/Broadcast/FBLiveSupport', function (req, res) {
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
            api.GetFacebookLiveSupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets data for OSC's Broadcast Customize view
    app.post('/ShadowPlay/v1.0/OSC/GetCustomize/Broadcast', function (req, res) {
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
            api.GetBroadcastCustomizeData(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //////////////////////////////////////////////////////////////////////////////////
    // Basic OSC handling

    //! Open/closes OSC
    //! send "{"open":true/false}" for open/close in request body
    app.post('/ShadowPlay/v.1.0/OpenOsc', function (req, res) {
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
                api.OpenOsc(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Open OSC Preferences
    app.post('/ShadowPlay/v.1.0/OpenOscPreferences', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            try {
                logger.debug('OpenOscPreferences reporting: ' + JSON.stringify(content));
                EmitNotification('/ShadowPlay/v.1.0/DisplayOscPreferences', content);
                res.writeHead(200);
                res.end();
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        })
    });

	//! Open OSC State
    app.post('/ShadowPlay/v.1.0/OpenOscState', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            try {
                logger.debug('OpenOscState reporting: ' + JSON.stringify(content));
                EmitNotification('/ShadowPlay/v.1.0/DisplayOscState', content);
                res.writeHead(200);
                res.end();
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        })
    });

    //! Display specific OSC Notification
    app.post('/ShadowPlay/v.1.0/OscNotification', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            try {
                logger.info('OSC reporting: ' + JSON.stringify(content));
                EmitNotification('/ShadowPlay/v.1.0/DisplayOscNotification', content);
                res.writeHead(200);
                res.end();
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    // Gets all data OSC needs on initialization
    app.get('/ShadowPlay/v.1.0/OSC/Init', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('OSC Init info: ' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetOSCInitData(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    // Gets all data OSC needs on generating main view
    app.post('/ShadowPlay/v.1.0/OSC/MainView', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('OSC Main View info: ' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.GetOSCMainViewData(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //////////////////////////////////////////////////////////////////////////////////
    // Basic ShadowPlay handling

    //! Launch ShadowPlay
    //! send "{"launch":true/false}" for launch/shutdown in request body
    app.post('/ShadowPlay/v.1.0/Launch', function (req, res) {
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
                logger.info('Setting ShadowPlay Enable: ' + JSON.stringify(content));
                api.ShadowPlayEnable(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets ShadowPlay run state
    //! Returns "{"launch":true/false}" for running/shutdown
    app.get('/ShadowPlay/v.1.0/Launch', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {

                var dataString = JSON.stringify(data)
                logger.info('ShadowPlay Launch state: ' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetShadowPlayStatus(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets HDR Active state
    //! Returns "{"active":true/false}" for running/shutdown
    app.get('/ShadowPlay/v.1.0/GetHDRState', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {

                var dataString = JSON.stringify(data)
                logger.info('HDR Enabled state: ' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetHDRActiveStatus(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets ShadowPlay support state
    app.post('/ShadowPlay/v.1.0/GetSupported', function (req, res) {
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
                api.ShadowPlaySupported(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //////////////////////////////////////////////////////////////////////////////////
    // Video-related interfaces

    //! Requests a video be trimmed
    app.post('/ShadowPlay/v.1.0/Video/Trim', function (req, res) {
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
                api.TrimVideo(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    /////////////////////////////////////////////////////////////////////////////////
    // Webcam handling

    //! Enables/disables the webcam
    //! send "{"status":true/false}" for enable/disable in request body
    app.post('/ShadowPlay/v.1.0/Webcam/Enable', function (req, res) {
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
                logger.info('Webcam enable:' + JSON.stringify(content));
                api.WebcamEnable(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Toggles Webcam between show/hide
    //! No data to send
    app.post('/ShadowPlay/v.1.0/Webcam/Toggle', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        try {
            logger.info('Webcam toggle request');
            api.WebcamToggle(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets webcam enable state
    //! Returns "{"status":true/false}" for enabled/disabled
    app.get('/ShadowPlay/v.1.0/Webcam/Enable', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Webcam state:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetWebcamStatus(doReply, "enable");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets if webcam overlay is shown
    //! Returns "{"shown":true/false}" for enabled/disabled
    app.get('/ShadowPlay/v.1.0/Webcam/Shown', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Webcam shown:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetWebcamStatus(doReply, "shown");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets webcam presence
    //! Returns "{"present":true/false}" for present/not present
    app.get('/ShadowPlay/v.1.0/Webcam/Present', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Webcam present:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetWebcamStatus(doReply, "present");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets webcam overlay settings
    app.post('/ShadowPlay/v.1.0/Webcam/Settings', function (req, res) {
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
                logger.info('Setting webcam:' + JSON.stringify(content));
                api.WebcamOverlaySettings(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets webcam overlay settings
    app.get('/ShadowPlay/v.1.0/Webcam/Settings', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Webcam settings:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetWebcamOverlaySettings(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });


    /////////////////////////////////////////////////////////////////////////////////
    // Microphone handling

    //! Sets Microphone mode
    //! send "{mode":"PTT"/"AlwaysOn"/"Off}" in request body
    app.post('/ShadowPlay/v.1.0/Microphone', function (req, res) {
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
                api.MicMode(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets Microphone mode
    //! Returns "{mode":"PTT"/"AlwaysOn"/"Off}"
    app.get('/ShadowPlay/v.1.0/Microphone', function (req, res) {
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
            api.GetMicMode(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets Microphone Push-To-Talk (PTT) mode
    //! send "{mode":"on"/"off}" in request body
    app.post('/ShadowPlay/v.1.0/Microphone/PTT', function (req, res) {
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
                api.MicPTTMode(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets microphone count
    //! Returns "{"present":Integer}" for number of microphones present (0 or more).
    app.get('/ShadowPlay/v.1.0/Microphone/Present', function (req, res) {
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
            api.MicPresent(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets microphone settings for a specifically-indexed microphone
    app.get('/ShadowPlay/v.1.0/Microphone/:index/Settings', function (req, res) {
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
            api.GetMicSettings(doReply, parseInt(req.params.index));
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets microphone settings for selected microphone
    app.get('/ShadowPlay/v.1.0/Microphone/Settings', function (req, res) {
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
            api.GetMicSettings(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets Microphone settings
    app.post('/ShadowPlay/v.1.0/Microphone/:index/Settings', function (req, res) {
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
                api.SetMicSettings(doReply, parseInt(req.params.index), content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    /////////////////////////////////////////////////////////////////////////////////
    // Audio handling

    //! Sets Audio mode
    //! send "{"mode":"off"/"game"/"mic"/"both"}" in request body
    app.post('/ShadowPlay/v.1.0/Audio', function (req, res) {
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
                api.AudioMode(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets Audio mode
    //! Returns "{"mode":"off"/"game"/"mic"/"both"}"
    app.get('/ShadowPlay/v.1.0/Audio', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Audio Mode: ' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.AudioMode(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets Audio and Multi-track settings
    app.post('/ShadowPlay/v.1.0/AudioSettings', function (req, res) {
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
                api.AudioSettings(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets Audio and Multi-track settings
    app.get('/ShadowPlay/v.1.0/AudioSettings', function (req, res) {
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
            api.AudioSettings(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets 4K support
    app.get('/ShadowPlay/v.1.0/4KSupport', function (req, res) {
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
            api.Get4KSupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets 8K 60FPS support
    app.get('/ShadowPlay/v.1.0/8k60', function (req, res) {
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
            api.Get8k60Support(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the bitrate values for a specific recording quality and resolution
    app.get('/ShadowPlay/v.1.0/BitRates/:quality/:resolution', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.quality) {
            var err = new Error('"quality" string argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        if (!req.params.resolution) {
            var err = new Error('"resolution" string argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetBitrates(doReply, req.params.quality, req.params.resolution);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the list of supported resolutions
    app.get('/ShadowPlay/v.1.0/Resolutions', function (req, res) {
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
            api.GetResolutions(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the default resolution values for a specific recording quality
    app.get('/ShadowPlay/v.1.0/Resolutions/:quality/', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.quality) {
            var err = new Error('"quality" string argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetQualityDefaultData(doReply, req.params.quality, "resolution");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the list of supported framerates
    app.get('/ShadowPlay/v.1.0/Framerates', function (req, res) {
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
            api.GetFramerates(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the default framerate values for a specific recording quality
    app.get('/ShadowPlay/v.1.0/Framerates/:quality/', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.quality) {
            var err = new Error('"quality" string argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetQualityDefaultData(doReply, req.params.quality, "framerate");
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Set recording paths
    app.post('/ShadowPlay/v.1.0/RecordPaths', function (req, res) {
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
                api.RecordingPaths(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Get recording paths
    app.get('/ShadowPlay/v.1.0/RecordPaths', function (req, res) {
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
            api.RecordingPaths(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets Capture State
    app.get('/ShadowPlay/v.1.0/Capture/State', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Capture State:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CaptureState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets if it is valid to do capture control under PID mode
    app.get('/ShadowPlay/v.1.0/Capture/PIDMode', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Capture control under PID Mode:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CaptureControlUnderPIDMode(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets process info
    app.get('/ShadowPlay/v.1.0/Capture/ProcessInfo/:PID/', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Capture control under PID Mode:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        if (!req.params.PID) {
            var err = new Error('"PID" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.CaptureProcessInfo(doReply, parseInt(req.params.PID));
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    /////////////////////////////////////////////////////////////////////////////////
    // CoPlay handling

    //! Set CoPlay enable/disable
    app.post('/ShadowPlay/v.1.0/CoPlay/Enable', function (req, res) {
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
                api.CoplayEnable(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Get if CoPlay is enabled/disabled
    app.get('/ShadowPlay/v.1.0/CoPlay/Enable', function (req, res) {
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
            api.CoplayEnable(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets CoPlay support
    app.get('/ShadowPlay/v.1.0/CoPlay/Support', function (req, res) {
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
            api.GetCoplaySupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    /////////////////////////////////////////////////////////////////////////////////
    // Indicator Overlay handling

    //! Gets indicator settings for a specific indicator type
    app.get('/ShadowPlay/v.1.0/Indicator/:id/Settings', function (req, res) {
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
            api.IndicatorOverlaySettings(doReply, false, req.params.id);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets indicator settings for a specific indicator type
    app.post('/ShadowPlay/v.1.0/Indicator/:id/Settings', function (req, res) {
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
                api.IndicatorOverlaySettings(doReply, true, req.params.id, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Determines if a specific indicator is supported
    app.get('/ShadowPlay/v.1.0/Indicator/:id/Support', function (req, res) {
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
            api.GetIndicatorOverlaySupport(doReply, req.params.id);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    /////////////////////////////////////////////////////////////////////////////////
    // Desktop Capture handling

    //! Set Desktop Capture enable/disable
    app.post('/ShadowPlay/v.1.0/DesktopCapture/Enable', function (req, res) {
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
                api.DesktopCaptureEnable(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Get if Desktop Capture is enabled/disabled
    app.get('/ShadowPlay/v.1.0/DesktopCapture/Enable', function (req, res) {
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
            api.DesktopCaptureEnable(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets if Desktop Capture is supported
    app.get('/ShadowPlay/v.1.0/DesktopCapture/Support', function (req, res) {
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
            api.GetDesktopCaptureSupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the supported reasons for Desktop Capture. This is an extended version of /ShadowPlay/v.1.0/DesktopCapture/Support.
    app.get('/ShadowPlay/v.1.0/DesktopCapture/Support/Reason', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('DT Capture Support Reason:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetDesktopCaptureSupportReason(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    /////////////////////////////////////////////////////////////////////////////////
    // Custom Overlay handling

    //! Gets if custom overlay enabled/disabled
    app.get('/ShadowPlay/v.1.0/CustomOverlay/Enable', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Custom Overlay Enablement:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CustomOverlayEnable(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets custom overlay enabled/disabled
    app.post('/ShadowPlay/v.1.0/CustomOverlay/Enable', function (req, res) {
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
                logger.info('Setting Custom Overlay Enablement:' + JSON.stringify(content));
                api.CustomOverlayEnable(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets if multiple custom overlay enabled/disabled
    app.get('/ShadowPlay/v.1.1/CustomOverlay/Enable/:index', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Multiple Custom Overlay Enablement:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }
        if (!req.params.index) {
            var err = new Error('"index" integer argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            logger.info('req.param =' + req.params);
            api.MultipleCustomOverlayEnable(doReply, req.params.index, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets multiple custom overlay enabled/disabled
    app.post('/ShadowPlay/v.1.1/CustomOverlay/Enable/:index', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }
        if (!req.params.index) {
            var err = new Error('"index" integer argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }
        getJSONDataAndDo(req, res, function (content) {
            try {
                logger.info('Setting Multiple Custom Overlay Enablement:' + JSON.stringify(content));
                api.MultipleCustomOverlayEnable(doReply, req.params.index, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets custom overlay path
    app.get('/ShadowPlay/v.1.0/CustomOverlay/Path', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Custom Overlay Path:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CustomOverlayPath(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets custom overlay path
    app.post('/ShadowPlay/v.1.0/CustomOverlay/Path', function (req, res) {
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
                logger.info('Setting Custom Overlay Path:' + JSON.stringify(content));
                api.CustomOverlayPath(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets multiple custom overlay path
    app.get('/ShadowPlay/v.1.1/CustomOverlay/Path/:index', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Multiple Custom Overlay Path:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }
        if (!req.params.index) {
            var err = new Error('"index" integer argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.MultipleCustomOverlayPath(doReply, req.params.index, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //!sets multiple custom overlay path
    app.post('/ShadowPlay/v.1.1/CustomOverlay/Path/:index', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.index) {
            var err = new Error('"index" integer argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }
        getJSONDataAndDo(req, res, function (content) {
            try {
                logger.info('Setting Multiple Custom Overlay Path:' + JSON.stringify(content));
                api.MultipleCustomOverlayPath(doReply, req.params.index, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Determines if a custom overlay is supported
    app.get('/ShadowPlay/v.1.0/CustomOverlay/Support', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                if (typeof data === 'number') {
                    data = { support: ['none', 'single', 'multiple'][data] || 'none' };
                }
                var dataString = JSON.stringify(data);
                logger.info('Custom Overlay Support:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CustomOverlaySupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets custom overlay default path
    app.get('/ShadowPlay/v.1.0/CustomOverlay/DefaultPath', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Custom Overlay default Path:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CustomOverlayDefaultPath(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets custom overlay display status in true/false
    app.get('/ShadowPlay/v.1.0/CustomOverlay/Display', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Custom Overlay DisplayStatus:' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CustomOverlayDisplayState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });
    /////////////////////////////////////////////////////////////////////////////////
    // Hotkey handling

    //! Dual role endpoint:
    //! Either sets key combo for a specific hotkey or enables/disables hotkey monitor
    app.post('/ShadowPlay/v.1.0/Hotkey/:hk', function (req, res) {
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
                var paramString = req.params.hk.toLowerCase();
                if (paramString == 'monitor') {
                    logger.info('Setting hotkey monitoring with data ' + JSON.stringify(content));
                    api.HotKeyMonitor(doReply, true, content);
                }
                else if (paramString == 'dynamictoggle') {
                    logger.info('Hotkey' + req.params.hk + ' with data ' + JSON.stringify(content));
                    api.HotKeyDynamicEnable(doReply, content);
                }
                else {
                    logger.info('Setting hotkey ' + req.params.hk + ' with data ' + JSON.stringify(content));
                    api.HotKey(doReply, true, req.params.hk, content);
                }
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Dual role endpoint:
    //! Either gets key combo for a specific hotkey or get enable/disable state of hotkey monitor
    app.get('/ShadowPlay/v.1.0/Hotkey/:hk', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                if (req.params.hk.toLowerCase() == 'monitor') {
                    logger.info('Hotkey monitor has data ' + dataString);
                }
                else {
                    logger.info('Hotkey ' + req.params.hk + ' has data ' + dataString);
                }

                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            if (req.params.hk.toLowerCase() == 'monitor') {
                api.HotKeyMonitor(doReply, false);
            }
            else {
                api.HotKey(doReply, false, req.params.hk);
            }
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Accepts OSC start-readiness outcome to broadcast to all clients
    app.post('/ShadowPlay/v.1.0/Osc', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            try {
                _logger.info('OSC reporting: ' + JSON.stringify(content));
                EmitNotification('/ShadowPlay/v.1.0/Osc', content);
                res.writeHead(200);
                res.end();
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    //! Gets screenshot support
    app.get('/ShadowPlay/v.1.0/Screenshot/Support', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                logger.info('Screenshot: ' + dataString);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.ScreenshotSupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Capture Screenshot
    app.post('/ShadowPlay/v.1.0/Screenshot/Capture', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        try {
            api.CaptureScreenshot(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets input redirection
    app.post('/ShadowPlay/v.1.0/Input', function (req, res) {
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
                api.InputRedirection(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    /////////////////////////////////////////////////////////////////////////////////
    // Highlights related
    app.post('/ShadowPlay/v.1.0/Highlights/Customize', function (req, res) {
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
                api.HighlightsCustomize(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.get('/ShadowPlay/v.1.0/Highlights/Customize', function (req, res) {
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
            api.HighlightsCustomize(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Imports a hightlight into respective Gallery folder
    app.post('/ShadowPlay/v.1.0/Highlights/GalleryImport', function (req, res) {
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
                api.GalleryHighlightImport(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        })
    });

    app.get('/ShadowPlay/v.1.0/Highlights/Session', function (req, res) {
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
            api.HighlightsSession(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.post('/ShadowPlay/v.1.0/Screenshot/NGXShot', function (req, res) {

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
                api.NGXSuperResCapture(doReply, content.scale, content.path);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })

    });


    app.post('/ShadowPlay/v.1.0/Screenshot/NGXCancelShot', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        try {
            api.NGXSuperResTerminate(doReply);

        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    /////////////////////////////////////////////////////////////////////////////////
    // Callback handling

    function EmitNotification(name, data) {
        setImmediate(function () { io.emit(name, data); });
    }

    //! Callback function used to notify client of capture state change notifications
    function CaptureStateChangeNotificationCallback(data) {
        // See CaptureMode enum
        var result;
        var endpoint;
        switch (data.captureMode) {
            case 0:
                // Manual Record
                endpoint = '/ShadowPlay/v.1.0/Record/Enable';
                switch (data.recordingState) {
                    case 0x00:
                        result = { status: false };
                        break;
                    case 0x01:
                        result = { status: true };
                        break;
                    default:
                        logger.error('Unhandled recording state: ' + data.recordingState);
                        return;
                }
                break;
            case 1:
                // Instant Replay
                switch (data.recordingState) {
                    case 0x00:
                        result = { started: false };
                        endpoint = '/ShadowPlay/v.1.0/InstantReplay/Started';
                        break;
                    case 0x01:
                        result = { started: true };
                        endpoint = '/ShadowPlay/v.1.0/InstantReplay/Started';
                        break;
                    case 0x20:
                        result = { status: true };
                        endpoint = '/ShadowPlay/v.1.0/InstantReplay/Save';
                        break;
                    case 0x40:
                        result = { restarted: true };
                        endpoint = '/ShadowPlay/v.1.0/InstantReplay/Started';
                        break;
                    case 0x44:
                        result = { status: true };
                        endpoint = '/ShadowPlay/v.1.0/InstantReplay/Enable'
                        break;
                    case 0x45:
                        result = { status: false };
                        endpoint = '/ShadowPlay/v.1.0/InstantReplay/Enable'
                        break;
                    default:
                        logger.error('Unhandled instant replay state: ' + data.recordingState);
                        return;
                }
                break;
            case 2:
                // Broadcast
                switch (data.recordingState) {
                    case 0x00:
                        result = { status: false };
                        endpoint = '/ShadowPlay/v.1.0/Broadcast/Enable'
                        break;
                    case 0x01:
                        result = { status: true };
                        endpoint = '/ShadowPlay/v.1.0/Broadcast/Enable'
                        break;
                    case 0x02:
                    case 0x04:
                        result = { status: true };
                        endpoint = '/ShadowPlay/v.1.0/Broadcast/Pause'
                        break;
                    case 0x41:
                        result = { status: false };
                        endpoint = '/ShadowPlay/v.1.0/Broadcast/Pause'
                        break;
                    default:
                        logger.error('Unhandled broadcast state: ' + data.recordingState);
                        break;
                }
                break;
            default:
                logger.error('Unhandled capture mode: ' + data.captureMode);
                return;
        }
        logger.info('Notification:' + endpoint + ' Data:' + JSON.stringify(result));
        EmitNotification(endpoint, result);
    }
    api.SetOscCaptureStateChangeNotificationCallback(CaptureStateChangeNotificationCallback);

    function WindowStateChangeNotificationCallback(data) {
        logger.info('Window State Change Notification with data:' + JSON.stringify(data));
        EmitNotification('/ShadowPlay/v.1.0/WindowState', data);
    }
    api.SetOscWindowStateChangeNotificationCallback(WindowStateChangeNotificationCallback);

    function HotkeyCallback(hotkeyData) {
        logger.info('HotKey callback with data:' + JSON.stringify(hotkeyData));
        EmitNotification('/ShadowPlay/v.1.0/Hotkey', hotkeyData);
    }
    api.SetHotkeyCallback(HotkeyCallback);

    function BroadcastSessionNotificationCallback(sessionData) {
        logger.info('Broadcast session callback data : ' + JSON.stringify(sessionData));
        EmitNotification('/ShadowPlay/v.1.0/Broadcast/SessionEvent', sessionData);
    }
    api.SetBroadcastSessionNotificationCallback(BroadcastSessionNotificationCallback);

    function GeneralNotificationCallback(sessionData) {
        logger.info('General notification callback data : ' + JSON.stringify(sessionData));
        EmitNotification('/ShadowPlay/v.1.0/Notification', sessionData);
    }
    api.SetGeneralNotificationCallback(GeneralNotificationCallback);
}

module.exports = function (app, io, logger) {
    if (app === undefined || io === undefined || logger == undefined) {
        throw 'You need to provide express app, socket io and logging';
    }

    //! Allows global access to logging module
    _logger = logger;

    //! Returns the version of this module
    //! This must be the last declaration in the file. Anything below it will be masked.
    return {
        version: function () {
            return api.GetVersion();
        },
        Cleanup: function () {
            api.Cleanup();
        },
        initialize: function () {
            return new Promise(function CreateInitializationPromise(resolve, reject) {
                api.Initialize(NativeCallbackToPromise(resolve, reject));
            }).then(function OnNativeAPIInitialized() {
                RegisterExpressEndpoints(app, io, logger);
                logger.info('ShadowPlayAPI module initialized');
            });
        }
    };
};
