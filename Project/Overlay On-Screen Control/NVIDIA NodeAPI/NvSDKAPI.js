/* Copyright (c) 2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

var api;
var _logger;

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
    };
}

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
    };
}

function RegisterExpressEndpoints(app, io, logger) {

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
            res.writeHead(httpCode, { 'Content-Type': 'text/html;charset=utf-8' });
        }
        else if ('invalidArgument' in err) {
            res.writeHead(400, { 'Content-Type': 'text/html;charset=utf-8' });
        }
        else {
            res.writeHead(500, { 'Content-Type': 'text/html;charset=utf-8' });
        }
        res.end(err.name + ': ' + err.message);
    }

    app.get('/SDK/v.1.0/Highlights/Active', function (req, res) {
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
            api.GetHighlightsActive(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.get('/SDK/v.1.0/Instance', function (req, res) {
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
            api.GetInstanceActive(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.post('/SDK/v.1.0/Highlights/Save', function (req, res) {
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
                api.SaveHighlight(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/UpdatePath', function (req, res) {
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
                api.UpdatePath(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/Get', function (req, res) {
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
                api.GetHighlight(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/GetAll', function (req, res) {
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
                api.GetAllHighlights(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/GetCount', function (req, res) {
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
                api.GetHighlightCount(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/Delete', function (req, res) {
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
                api.DeleteHighlights(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/DeleteGroup', function (req, res) {
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
                api.DeleteHighlightGroup(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/GetConfig', function (req, res) {
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
                api.GetConfig(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/SetConfig', function (req, res) {
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
                api.SetConfig(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Recover Space
    app.post('/SDK/v.1.0/Highlights/RecoverSpace', function (req, res) {
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
            api.RecoverSpace(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.post('/SDK/v.1.0/GetPermissions', function (req, res) {
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
                api.GetPermissions(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/SetPermissions', function (req, res) {
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
                api.SetPermissions(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/NotifyOverlayState', function (req, res) {
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
                api.NotifyOverlayState(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/SDK/v.1.0/Highlights/Enable', function (req, res) {
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
                api.HighlightsEnabled(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.get('/SDK/v.1.0/Highlights/Enable', function (req, res) {
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
            api.HighlightsEnabled(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.post('/SDK/v.1.0/Highlights/GetRecent', function (req, res) {
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
                api.GetRecentHighlights(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.get('/SDK/v.1.0/Highlights/GetGamesConfig', function (req, res) {
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
            api.GetGamesConfig(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.post('/SDK/v.1.0/Highlights/GetHighlights', function (req, res) {
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
                api.GetGameHighlights(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    // Callback Handling
    function EmitNotification(name, data) {
        setImmediate(function () { io.emit(name, data); });
    }

    function CreateNotificationCallback(data) {
        logger.info('SDK Callback', JSON.stringify(data));
        EmitNotification('/SDK/v.1.0/Notification', data);
    }
    api.SetOscCallback(CreateNotificationCallback);
}

module.exports = function (app, io, logger, definedApi) {
    if (app === undefined || io === undefined || logger == undefined || definedApi == undefined) {
        throw 'You need to provide express app, socket io, logging, and api';
    }

    api = definedApi;
    
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
                logger.info('SDK module initialized');
            });
        }
    };
};
