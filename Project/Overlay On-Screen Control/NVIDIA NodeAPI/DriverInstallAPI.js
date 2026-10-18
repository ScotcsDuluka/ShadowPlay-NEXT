/* Copyright (c) 2015-2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

var driverInstall = require('./shims/DriverInstall.js');

var _logger;

//! Helper function that receives body of POST request and calls callback for that data.
//! @param req      Request object provided by Express.
//! @param callback Callback that is triggered on succesfully downloaded data.
function getPostDataAndDo(req, callback) {
    var content = '';

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
            _logger.info('Content: ' + content);
            parsed = JSON.parse(content);
        }
        catch (e) {
            _logger.error('Caught exception in parseAndCallback');
            _logger.error(e.name + ': ' + e.message);
            res.writeHead(400, { 'Content-Type': 'text/html' });
            res.end(e.message);
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
        res.writeHead(httpCode, { 'Content-Type': 'text/html' });
    }
    else if ('invalidArgument' in err) {
        res.writeHead(400, { 'Content-Type': 'text/html' });
    }
    else {
        res.writeHead(500, { 'Content-Type': 'text/html' });
    }
    _logger.error('replyWithError: ' + err.message);
    res.end(err.message);
}

module.exports = function (app, io, logger)
{
    if (app === undefined || io === undefined || logger == undefined)
    {
        throw 'You need to provide express app, socket io and logger';
    }
    
    //! Allows global access to logging module
    _logger = logger;

    app.post("/DriverInstall/v.0.1/Start", function (req, res) {
        function doReply(err) {
            if (err) {
                _logger.error('Error in response to DriverInstall Start: ' + err.message);
                replyWithError(res, err);
            }
            else {
                res.writeHead(201);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                var contentAsString = JSON.stringify(content);
                _logger.info('Calling DriverInstall Start with: ' + contentAsString);
                driverInstall.Start(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        });
    });

    app.post("/DriverInstall/v.0.1/ChangeState", function (req, res) {
        function doReply(err) {
            if (err) {
                _logger.error('Error in response to DriverInstall ChangeState: ' + err.message);
                replyWithError(res, err);
            }
            else {
                res.writeHead(201);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                var contentAsString = JSON.stringify(content);
                _logger.info('Calling DriverInstall ChangeState with: ' + contentAsString);
                driverInstall.ChangeState(doReply, contentAsString);
            }
            catch (err) {
                replyWithError(res, err);
                console.error(err);
            }
        });
    });

    function ShowFormCallback(statusAsString) {
        _logger.info('Emiting DriverInstall ShowForm: ' + statusAsString);
        var status = JSON.parse(statusAsString);
        setImmediate(function () {
            io.emit('/DriverInstall/v.0.1/ShowForm', status);
        });
    }
    driverInstall.SetShowFormCallback(ShowFormCallback);

    function ProgressUpdateCallback(statusAsString) {
        //
        // Let's not log progress update for now. This is only progress update and it will 
        // flood like anything based on  number of  packages.  Needed for UI to  show  the 
        // progress bar at  appropriate percentage. Enabling log for this will increase it 
        // significantly
        // 
        // _logger.info('Emiting DriverInstall ProgressUpdate: ' + statusAsString);
        //
        var status = JSON.parse(statusAsString);
        setImmediate(function () {
            io.emit('/DriverInstall/v.0.1/ProgressUpdate', status);
        });
    }
    driverInstall.SetProgressUpdateCallback(ProgressUpdateCallback);

    //
    // TODO: Implement the GetVersion method in DriverInstall 
    // Node which will return the correct version information
    // 
    return {
        version: function () {
            return "1.0.0";
        }
    };
};
