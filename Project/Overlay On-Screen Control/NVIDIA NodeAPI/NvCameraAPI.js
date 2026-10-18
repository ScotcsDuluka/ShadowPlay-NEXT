/* Copyright (c) 2015-2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

var api = require('./shims/NvCameraAPINode.js');

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
    else if ('notFound' in err) {
        res.writeHead(501, { 'Content-Type': 'application/json' });
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

function RegisterExpressEndpoints(app, logger) {

    //! Gets the NVCamera availability for use
    app.post('/NvCamera/v.1.0/GetAvailable', function (req, res) {
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
            api.Available(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Checks for NVCamera version compatibility on system
    app.get('/NvCamera/v.1.0/Compatible', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.Compatible(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the NVCamera game integration state
    app.post('/NvCamera/v.1.0/GetIntegration', function (req, res) {
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
            api.Integration(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the supported capture types
    app.post('/NvCamera/v.1.0/Capture/GetTypes', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.CaptureType(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets the capture control state
    //! Send "{"capture":"true"|"false"} for capture control enable|disable
    app.post('/NvCamera/v.1.0/Capture/Control', function (req, res) {
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
                api.CaptureControl(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Requests the capture control state
    app.post('/NvCamera/v.1.0/Capture/GetControl', function (req, res) {
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
            api.GetCaptureControl(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets resolutions supported by a specific capture type
    app.post('/NvCamera/v.1.0/Capture/GetResolutions/:type', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        // No content is expected
        getJSONDataAndDo(req, res, function () {
            try {
                api.CaptureResolutions(doReply, req.params.type);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Gets supported filter types
    app.post('/NvCamera/v.1.0/GetFilters', function (req, res) {
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
            api.FilterType(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Resets all Filters
    app.post('/NvCamera/v.1.0/Filter/ResetAll', function (req, res) {
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
            api.ResetFilter(doReply, true);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Resets a Filter
    app.post('/NvCamera/v.1.0/Filter/Reset', function (req, res) {
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
                api.ResetFilter(doReply, false, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    //! Get current filter info
    app.post('/NvCamera/v.1.0/Filter/GetInfo', function (req, res) {
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
                api.GetCurrentFilterInfo(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Sets the filter type
    //! Send "type" with a valid type string
    app.post('/NvCamera/v.1.0/Filter', function (req, res) {
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
                api.FilterType(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Sets the value of an attribute of a filter type
    app.post('/NvCamera/v.1.0/Filter/:id/Attribute', function (req, res) {
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
                api.SetFilterAttribute(doReply, 0, req.params.id, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Sets the value of an attribute of a filter type
    app.post('/NvCamera/v.1.1/Filter/:id/Attribute', function (req, res) {
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
                api.SetFilterAttribute(doReply, 1, req.params.id, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });


    //! Sets the filter type and its attributes in single call
    app.post('/NvCamera/v.1.0/Filter/:id/SetFilterAndAttributes', function (req, res) {
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
                api.SetFilterAndAttributes(doReply, req.params.id, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Inserts a Filter into the stack
    app.post('/NvCamera/v.1.0/Filter/Insert', function (req, res) {
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
                api.InsertFilter(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    //! Removes a Filter from the stack
    app.post('/NvCamera/v.1.0/Filter/Remove', function (req, res) {
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
                api.RemoveFilter(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    //! Changes the filter(s) in the stack order
    app.post('/NvCamera/v.1.0/Filter/Reorder', function (req, res) {
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
                api.ReorderFilters(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    //! Gets the filter IDs of the stack
    app.post('/NvCamera/v.1.0/Filter/GetStackInfo', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200)
                res.end(JSON.stringify(data));
            }
        }

        api.GetFilterStackInfo(doReply);
    });

    //! Reset the whole stack
    app.post('/NvCamera/v.1.0/Filter/ResetStack', function (req, res) {
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
            api.ResetEntireStack(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets the current camera adjustments
    app.post('/NvCamera/v.1.0/Camera/GetAdjust', function (req, res) {
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
                api.CameraPosition(doReply, false, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Adjustment camera settings
    app.post('/NvCamera/v.1.0/Camera/Adjust', function (req, res) {
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
                api.CameraPosition(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Requests the camera ranges for roll and FOV
    app.post('/NvCamera/v.1.0/Camera/GetRange', function (req, res) {
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
                api.CameraSettingsRange(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Captures a screenshot with the current settings
    app.post('/NvCamera/v.1.0', function (req, res) {
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
                api.CaptureScreenshot(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Cancels an in-progress screenshot
    app.post('/NvCamera/v.1.0/Cancel', function (req, res) {
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
            api.CaptureScreenshotCancel(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Sets language
    app.post('/NvCamera/v.1.0/Language', function (req, res) {
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
                api.Language(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Enable/disables IPC mode
    app.post('/NvCamera/v.1.0/IPC', function (req, res) {
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
                api.IPC(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Informs Ansel that the UI is ready to receive new controls
    app.get('/NvCamera/v.1.0/uiReady', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.UIReady(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Informs Ansel about a change in UI control values
    app.post('/NvCamera/v.1.0/uiControlChanged', function (req, res) {
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
                api.UIControlChanged(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Reports the visibility of a UI control as requested by Ansel
    app.post('/NvCamera/v.1.0/reportControlVisibility', function (req, res) {
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
                api.ReportControlVisibility(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Retrieves process info from Ansel
    app.get('/NvCamera/v.1.0/GetProcessInfo', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                var dataString = JSON.stringify(data);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetProcessInfo(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Enable/disables GridOfThirds
    app.post('/NvCamera/v.1.0/GridOfThirds', function (req, res) {
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
                api.SetGridOfThirds(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    // Style Transfer support
    app.post('/NvCamera/v.1.0/StyleTransfer', function (req, res) {
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
                api.StyleTransferEnable(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/NvCamera/v.1.0/StyleTransfer/GetStatus', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }
        api.StyleTransferGetStatus(doReply);
    });

    /* TODO: Implement once supported by IPC
    app.post('/NvCamera/v.1.0/StyleTransfer/GetDirectory', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }
        api.StyleTransferGetDirectory(doReply);
    });
    */
    app.post('/NvCamera/v.1.0/StyleTransfer/Style', function (req, res) {
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
                api.StyleTransferSetStyle(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    app.post('/NvCamera/v.1.0/StyleTransfer/GetModels', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }
        api.StyleTransferGetModels(doReply);
    });

    app.post('/NvCamera/v.1.0/StyleTransfer/Model', function (req, res) {
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
                api.StyleTransferSetModel(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Game Engine controls 
    app.post('/NvCamera/v.1.0/GameEngine', function (req, res) {
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
            api.GameSpecificFilters(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Gets whether game is Freestyle support or not
    app.post('/NvCamera/v.1.0/GetFreestyleSupport', function (req, res) {
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
                api.FreestyleSupported(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Enable/Disable freestyle On/Off globally
    app.post('/NvCamera/v.1.0/EnableMods', function (req, res) {
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
                api.EnableGlobalFreestyle(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Get the NvCamera config regkey values
    app.get('/NvCamera/v.1.0/GetNvCameraConfig', function (req, res) {
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
            api.GetNvCameraConfig(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Get whether NvCamera supports SetFilterAndAttributes IPC
    app.get('/NvCamera/v.1.0/SetFilterAndAttributesSupported', function (req, res) {
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
            api.SetFilterAndAttributesSupported(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Get whether NvCamera supports Reshade IPC
    app.get('/NvCamera/v.1.0/ReshadeSupported', function (req, res) {
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
            api.ReshadeSupported(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    //! Set multiple filters and their attributes in a single call
    app.post('/NvCamera/v.1.0/Filter/SetMultipleFiltersAndAttributes', function (req, res) {
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
                api.SetMultipleFilterAndAttributes(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        })
    });

    //! Get whether NvCamera supports SetMultipleFiltersAndAttributes API
    app.get('/NvCamera/v.1.0/SetMultipleFilterAPISupport', function (req, res) {
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
            api.SetMultipleFilterAPISupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.post('/NvCamera/v.1.0/SetSharpnessForApp', function (req, res) {
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
                api.SetSharpnessForApp(doReply, content);
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

    /////////////////////////////////////////////////////////////////////////////////
    // Async Notification handling

    function EmitNotification(name, data) {
        setImmediate(function () { io.emit(name, data); });
    }

    function NotificationCallback(data) {
        logger.info('Notification with data:' + JSON.stringify(data));
        EmitNotification('/NvCamera/v.1.0/Notifications', data);
    }
    api.SetNotificationCallback(NotificationCallback);

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
                RegisterExpressEndpoints(app, logger);
                logger.info('NvCameraAPI module initialized');
            });
        }
    };
};
