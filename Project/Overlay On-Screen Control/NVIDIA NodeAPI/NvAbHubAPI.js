/* Copyright (c) 2019, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

let abHubAPI = require('./shims/NvABHubAPI.js');
let _logger;

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

//! Helper function that receives body of POST request and calls callback for that data.
//! @param req      Request object provided by Express.
//! @param callback Callback that is triggered on successfully downloaded data.
function getPostDataAndDo(req, callback) {
    let content = '';

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
//! @param callback Callback that is triggered on successfully parsed data.
function getJSONDataAndDo(req, res, callback) {
    function parseAndCallback(content) {
        let parsed = {};
        try {
            _logger.info('Content: ' + content);
            parsed = JSON.parse(content);
        } catch (e) {
            _logger.error('Caught exception in parseAndCallback');
            _logger.error(e.name + ': ' + e.message);
            res.writeHead(400, {
                'Content-Type': 'text/html'
            });
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
        res.writeHead(httpCode, {
            'Content-Type': 'text/html'
        });
    } else if ('invalidArgument' in err) {
        res.writeHead(400, {
            'Content-Type': 'text/html'
        });
    } else {
        res.writeHead(500, {
            'Content-Type': 'text/html'
        });
    }
    _logger.error('replyWithError: ' + err.message);
    res.end(err.message);
}

function RegisterExpressEndpoints(app, io) {
    function doReply(res, err, endpoint, httpCode) {
        if (err) {
            let errMsg = 'Error in response to abHubAPI endpoint ' + endpoint + ' : ' + err ;
            replyWithError(res, {message: errMsg}, httpCode);
        } else {
            res.writeHead(200);
            res.end();
        }
    }

    function stringifyVariantData(abMessage) {
        if (abMessage && Array.isArray(abMessage.experiments)) {
            for (let item of abMessage.experiments) {
                if (item.variant && typeof item.variant.data !== 'string') {
                    item.variant.data = JSON.stringify(item.variant.data);
                }
            }
        }
    }

    function postMessage(res, content, endpoint) {
        try {
            stringifyVariantData(content);
            let contentAsString = JSON.stringify(content);
            _logger.info('Called abHubAPI PostAbMessage : ' + content.clientName);
            abHubAPI.PostAbMessage(function (err) {
                doReply(res, err, endpoint);
            }, contentAsString)
        } catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.post('/abHubAPI/v.0.1/Post', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            postMessage(res, content, '/Post');
        });
    });
    app.post('/abHubAPI/v.0.1/Add', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            postMessage(res, content, '/Add');
        });
    });
    app.post('/abHubAPI/v.0.1/Delete', function (req, res) {
        getJSONDataAndDo(req, res, function (content) {
            postMessage(res, content, '/Delete');
        });
    });
    app.get('/abHubAPI/v.0.1/Status', function (req, res) {
        _logger.info('Called abHubAPI Status.');
        doReply(res, false, '/Status', 503);
    });
}

module.exports = function (app, io, logger) {
    if (app === undefined || io === undefined || logger == undefined) {
        throw 'You need to provide express app, socket io and logger';
    }

    _logger = logger;

    return {
        version: function () {
            return abHubAPI.GetVersion();
        },
        initialize: function Initialize() {
            return new Promise(function CreateInitializationPromise(resolve, reject) {
                abHubAPI.Initialize(NativeCallbackToPromise(resolve, reject));
            }).then(function OnNativeAPIInitialized() {
                RegisterExpressEndpoints(app, io);
                logger.info('NvAbHubAPI: Module initialized');

                setImmediate(function () {
                    io.emit('/abHubAPI/v.0.1/Status', true);
                });
            });
        },
        cleanup: function cleanup() {
            abHubAPI.Cleanup();
        }
    };
};
