/* Copyright (c) 2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'
var api = require('./shims/NvGameShareAPINode.js');
var https = require('https');
var Utils = require('./Utils.js')();


//! Helper function for using promises with nodebacks.
//! @param resolve The promise resolve.
//! @param reject  The promise reject.
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

module.exports = function (app, io, logger) {

    if (app === undefined || io === undefined || logger == undefined) {
        throw 'You need to provide express app, socket io and logger.';
    }

    function EmitNotification(name, data) {
        setImmediate(function () { io.emit(name, data); });
    }

    function RegisterExpressEndpoints() {

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
            logger.error(err.name + ': ' + err.message);
        }

        app.get('/GameShare/v.1.0/FullScreenProcessId/*', function (req, res) {
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
                var activeWindow = req.params[0];
                if (activeWindow) {
                    api.GetFullScreenProcessId(doReply, activeWindow);
                }
                else {
                    res.writeHead(400);
                    res.end("Error: No active Window provided in the request.");
                }
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });

        app.put('/GameShare/v.1.0/ConfigureControllerMapping', function (req, res) {
            function doReply(err) {
                if (err) {
                    replyWithError(res, err);
                }
                else {
                    res.writeHead(200)
                    res.end();
                }
            }

            Utils.getJSONRequestDataAndDo(req, res, function (jsonData) {
                try {
                    api.ConfigureControllerMapping(doReply, jsonData);
                }
                catch (err) {
                    replyWithError(res, err);
                    logger.error(err);
                }
            });
        });

        app.post('/GameShare/v.1.0/CreateSession', function (req, res) {
            function doReply(err) {
                if (err) {
                    replyWithError(res, err);
                }
                else {
                    res.writeHead(202)
                    res.end();
                }
            }

            Utils.getJSONRequestDataAndDo(req, res, function (jsonData) {
                try {
                    api.CreateSession(doReply, jsonData);
                }
                catch (err) {
                    replyWithError(res, err);
                    logger.error(err);
                }
            });
        });

        app.put('/GameShare/v.1.0/ModifySession/*', function (req, res) {
            function doReply(err) {
                if (err) {
                    replyWithError(res, err);
                }
                else {
                    res.writeHead(200);
                    res.end();
                }
            }

            Utils.getJSONRequestDataAndDo(req, res, function (jsonData) {
                var sessionId = req.params[0];
                if (sessionId) {
                    try {
                        api.ModifySession(doReply, sessionId, jsonData);
                    }
                    catch (err) {
                        replyWithError(res, err);
                        logger.error(err);
                    }
                }
                else {
                    res.writeHead(400);
                    res.end("Error: No session id in the request.");
                }
            });
        });

        app.delete('/GameShare/v.1.0/Session/*', function (req, res) {
            function doReply(err) {
                if (err) {
                    replyWithError(res, err);
                }
                else {
                    res.writeHead(200);
                    res.end();
                }
            }

            var sessionId = req.params[0];
            if (sessionId) {
                try {
                    api.DeleteSession(doReply, sessionId);
                }
                catch (err) {
                    replyWithError(res, err);
                    logger.error(err);
                }
            }
            else {
                res.writeHead(400);
                res.end("Error: No session id in the request.");
            }
        });
    }

    function CreateSessionCallback(resultData, inviteMode, httpOption)
    {
        try {
            logger.info("CreateSessionCallback result: "+ JSON.stringify(resultData));

            if (resultData['status']=='created') {
                // update the status to OSC.
                if( inviteMode == "email") {
                    resultData['status'] ='sendingInvite';
                } else {
                    resultData['status'] ='preparingInvite';
                }
                EmitNotification('/GameShare/v.1.0/CreateSession', resultData);

                var sessionId = resultData["sessionInfo"].sessionId;

                var inviteHttpRequestOptions = {
                    host: httpOption['host'],
                    path: httpOption['path'],
                    port: '443',
                    method: 'POST',
                    headers: {
                      'Content-Type': 'application/x-www-form-urlencoded'
                    }
                };

                var postData = '';
                for( var element in httpOption.parameters) {
                    if(!httpOption.parameters.hasOwnProperty(element)) {
                        continue;
                    }
                    postData+= element + "=" + encodeURIComponent(httpOption.parameters[element]) + "&";
                }
                // remove the last '&' char
                postData = postData.slice(0,-1);

                function onInviteRequestFailure(error) {
                    if (!error) {
                        error = "inviteError"
                    }
                    logger.error("Failed to prepare GameShare invite. Error: "+error);
                    resultData["status"] = "failed";
                    resultData["error"] = error;
                    EmitNotification('/GameShare/v.1.0/CreateSession', resultData);

                    api.DeleteSession(function(err){}, sessionId);
                }

                function onInvitationPostCB(response) {
                    var str = ''

                    response.on('data', function (chunk) {
                        str += chunk;
                    });

                    response.on('end', function () {
                        try {
                            if(inviteMode=="email") {
                                if( str=="Success") {
                                    resultData["status"] = "inviteSent";
                                    EmitNotification('/GameShare/v.1.0/CreateSession', resultData);
                                    logger.info("GameShare email invite successfully sent.");
                                } else {
                                    logger.error("Sending invite failed. Error: "+ str);
                                    onInviteRequestFailure(str);
                                }
                            } else {
                                if(str.indexOf("http") > -1){
                                    resultData["status"] = "invitePrepared";
                                    resultData["invitationLink"] = str;
                                    EmitNotification('/GameShare/v.1.0/CreateSession', resultData);
                                    logger.info("Successfully prepared GameShare invite link.");
                                } else {
                                    logger.error("Preparing invite failed. Error: "+ str);
                                    onInviteRequestFailure(str);
                                }
                            }
                        } catch(err) {
                            logger.error("Exception in handling the gameshare invite response. Error: "+ err);
                        }
                    });
                }

                var inviteRequest = https.request(inviteHttpRequestOptions, onInvitationPostCB);
                inviteRequest.on('error', function(err){
                    logger.error("Error during sending/preparing GameShare invite: " + err);
                    onInviteRequestFailure();
                });
                inviteRequest.write(postData);
                inviteRequest.end();

            } else {
                logger.error("Failed to create GameShare session. Error: "+ JSON.stringify(resultData));
                EmitNotification('/GameShare/v.1.0/CreateSession', resultData);
            }
        } catch(err) {
            logger.error("Exception in CreateSessionCallback. Error: "+ err);
        }
    }

    api.SetCreateSessionCallback(CreateSessionCallback);

    function SessionUpdateCallback(data)
    {
        try {
            logger.info("SessionUpdate callback data: "+ JSON.stringify(data));
            EmitNotification('/GameShare/v.1.0/SessionUpdate', data);
            // HACK: SSAU still expects the terminate request from OSC for stopped state.
            if (data["state"]=="stopped") {
                api.DeleteSession(function(err){}, data["sessionId"]);
            }
        } catch(err) {
            logger.error("Exception in SessionUpdateCallback. Error: "+ err);
        }
    }

    api.SetSessionUpdateCallback(SessionUpdateCallback);

    function AudioDeviceChangeCallback(data)
    {
        try {
            logger.info("Sending notification for object " + JSON.stringify(data));
            EmitNotification('/GameShare/v.1.0/SessionUpdate', data);
        } catch (err) {
            logger.error("Error sending data.." + err);
        }
    }

    api.SetAudioDeviceChangeCallback(AudioDeviceChangeCallback);

    return {
        version: function () {
            return api.GetVersion();
        },
        initialize: function () {
            return new Promise(function CreateInitializationPromise(resolve, reject) {
                api.Initialize(NativeCallbackToPromise(resolve, reject));
            }).then(function OnNativeAPIInitialized() {
                RegisterExpressEndpoints();
                logger.info('NvGameShareAPI module initialized');
            });
        },
    };
};
