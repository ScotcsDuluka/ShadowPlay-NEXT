/* Copyright (c) 2015-2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict';

let https = require('https'),
    api = require('./shims/NVAccountAPINode.js'),
    config = require('./config.json'),
    _logger,
    refreshInterval,
    _onConsentError,
    jarvisServer = '';

//! Helper function that receives body of POST request and calls callback for that data.
//! @param req      Request object provided by Express.
//! @param callback Callback that is triggered on succesfully downloaded data.
function getPostDataAndDo(req, callback) {
    var content = '';

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
    var parts = errorString.split("::");
    for (var i = 0; i < parts.length; i++) {
        var index = parts[i].search("Text:");
        if (index !== -1) {
            returnText = parts[i].substring(index + 5);
        }
        index = parts[i].search("Code:");
        if (index !== -1) {
            returnCode = Number(parts[i].substring(index + 5));
        }
        index = parts[i].search("Message:");
        if (index !== -1) {
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
            setImmediate(function () { reject(error); });
        }
        else {
            setImmediate(function () { resolve(data); });
        }
    };
}

function UpdateAccountsFile(doReply, userInfo, userData) {
    try {
        let data = {
            userToken: userInfo.userToken,
            userInfo: JSON.stringify(userInfo)
        };
        api.UserToken((err, data) => {
            if (err) {
                _logger.error(err);
            } else {
                _logger.info('Writing updated user info to accounts file.');
            }
        }, true, data, false);
        doReply(undefined, userData);
    } catch (err) {
        doReply(err);
    }
}

function GetJarvisPrivacyData(doReply, sessionData, userInfo) {
    let privacyData = '';

    const options = {
        hostname: new URL(jarvisServer).hostname,
        path: '/api/1/profile/user/privacy',
        method: 'GET',
        headers: {
            'accept': 'application/json',
            'Authorization': 'Basic ' + Buffer.from(sessionData.sessionToken + ':').toString('base64'),
        }
    };

    const req = https.request(options, (res) => {
        res.setEncoding('utf8');
        res.on('data', (chunk) => {
            privacyData += chunk;
        });
        res.on('end', () => {
            try {
                let newConsent = JSON.parse(privacyData).dataTracking;
                if (userInfo.dataTracking.trackTechnicalData.level === newConsent.trackTechnicalData.level &&
                    userInfo.dataTracking.trackBehavioralData.level === newConsent.trackBehavioralData.level) {
                    // Nothing has changed so bail out with no data
                    doReply();
                    return;
                }
                userInfo.dataTracking = Object.assign(userInfo.dataTracking, newConsent);
                let userData = {
                    userId: sessionData.userId,
                    consentSettings: userInfo.dataTracking
                };
                UpdateAccountsFile(doReply, userInfo, userData);
            } catch (err) {
                doReply(err);
            }
        })
    });

    req.on('error', (err) => {
        doReply(err);
    });

    req.end();
}

function GetJarvisSessionToken(doReply, userInfo) {
    let data = '';

    const postData = JSON.stringify({
        clientId: config.jarvis.clientId, // GeForce Experience Client
        clientDescription: config.jarvis.clientDescription,
        deviceId: userInfo.deviceId
    });

    const options = {
        hostname: new URL(jarvisServer).hostname, // Jarvis endpoint
        path: '/api/1/authentication/client/login',
        method: 'POST',
        headers: {
            'accept': 'application/json',
            'Authorization': 'Basic ' + Buffer.from(userInfo.userToken + ':').toString('base64'),
            'content-type': 'application/json'
        }
    };

    const req = https.request(options, (res) => {
        res.setEncoding('utf8');
        res.on('data', (chunk) => {
            data += chunk;
        });
        res.on('end', () => {
            try {
                var sessionData = JSON.parse(data);
                GetJarvisPrivacyData(doReply, sessionData, userInfo);
            } catch (err) {
                _logger.error(`Failed to get Jarvis session token: ${err}`);
                doReply(err);
            }
        })
    });

    req.on('error', (err) => {
        doReply(err);
    });

    req.write(postData);
    req.end();
}

function HandleOutput(err, data) {
    if (err) {
        _logger.error(err);
    } else if (data) {
        _logger.info(data);
    }
}

function StoreUserConsent(err, data) {
    try {
        if (data) {
            _logger.info('Updating user consent with new data.');
            api.SetPrivacySettings(HandleOutput, data);
        }
    } catch (err) {
        HandleOutput(`Store User Consent failed: ${err}`);
    }
}

function RefreshUserConsentFromJarvis(err, data) {
    _logger.info(`Jarvis target environment: ${jarvisServer}`);
    try {
        GetJarvisSessionToken(StoreUserConsent, JSON.parse(data.userInfo));
    } catch (err) {
        HandleOutput(`Refresh User Consent from Jarvis failed: ${err}`);
    }
}

function RestartUserConsentRefreshInterval() {
    // Cancel any existing inveral
    clearInterval(refreshInterval);
    // Update user consent information from Jarvis regularly
    refreshInterval = setInterval(() => {
        api.UserToken(RefreshUserConsentFromJarvis, false);
    }, config.jarvis.userConsentRefreshWaitMins * 60 * 1000);
    _logger.info(`Restarted user consent refresh timer of ${config.jarvis.userConsentRefreshWaitMins} minutes.`);
}

module.exports = function (app, io, logger, onConsentError, NvBackendAPI, NvPiplConfig) {
    if (app === undefined || io === undefined || logger === undefined || NvBackendAPI === undefined) {
        throw 'You need to provide express app, socket io, logging and NvBackendAPI';
    }

    _onConsentError = onConsentError;

    //! Allows global access to logging module
    _logger = logger;

    function logError(err, data) {
        if (err) {
            _logger.error('SetLocalizedEndpoints internal error:', err);
        }
    }

    function onPiplConfigUpdate(cvData) {
        try {
            jarvisServer = cvData.configData.jarvis.server;
            const eventsUrl = cvData.configData.nvTelemetry.eventsServer;
            const feedbacksUrl = cvData.configData.nvTelemetry.feedbackServer;
            const attachmentsUrl = cvData.configData.nvTelemetry.feedbackAttachmentServer;
            api.SetLocalizedEndpoints(logError, eventsUrl, feedbacksUrl, attachmentsUrl);
            _logger.info('Updated PiplConfig in NvAccount');
        } catch (err) {
            _logger.error('Unable to update PiplConfig in NvAccount:', err);
        }
    }

    NvPiplConfig.setNvAccountCallback(onPiplConfigUpdate);

    //! Set User Token
    app.post('/Account/v.1.0/UserToken', function (req, res) {
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
                RestartUserConsentRefreshInterval();
                content.userInfo = JSON.stringify(content.userInfo);
                // true means "set"
                api.UserToken(doReply, true, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    //! Get User Token
    app.get('/Account/v.1.0/UserToken', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                try {
                    data.userInfo = JSON.parse(data.userInfo);
                } catch(e) { }
                var dataString = JSON.stringify(data);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            // false means "get"
            api.UserToken(doReply, false);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.get('/Settings/v.1.0/RewardsNotificationPreference', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                logger.info('rewards notification preference:' + data);
                var responseData = {};
                responseData.preference = data;
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(responseData));
            }
        }
        try {
            api.GetRewardNotificationSetting(doReply);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });

    app.post('/Settings/v.1.0/RewardsNotificationPreference', function (req, res) {
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
                logger.info('new notification preference:' + content.preference);
                api.SetRewardNotificationSetting(doReply, content.preference);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    //! Set Client and User Telemetry Consent Data
    app.post('/Account/v.1.0/PrivacySettings', function (req, res) {
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
                RestartUserConsentRefreshInterval();
                api.SetPrivacySettings(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    //! Get Client and User Telemetry Consent Data
    app.get('/Account/v.1.0/PrivacySettings', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
                onConsentError("GetPrivacySettings failed: " + err.toString());
            }
            else {
                var dataString = JSON.stringify(data);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(dataString);
            }
        }

        try {
            api.GetPrivacySettings(doReply, req.query);
        }
        catch (err) {
            replyWithError(res, err);
        }
    });
    
 ////////////////////////////////////////////////////////////////////////////////

function onSetLocalizedEndpoints(req, res) {

    function doReply(err) {
        if (err) {
            replyWithError(res, err);
        }
        else {
            res.writeHead(200);
            res.end();
        }
    }
    getJSONDataAndDo(req, res, function (urlData) {
        try {
            logger.debug('Setting url endpoints.');
            api.SetLocalizedEndpoints(doReply, urlData.eventUrl, urlData.feedbackUrl, urlData.feedbackAttachmentUrl);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });
}

app.post('/Applications/v.1.0/SetLocalizedEndpoints', onSetLocalizedEndpoints);

////////////////////////////////////////////////////////////////////////////////

    //! Refresh User Telemetry Consent Data from Jarvis
    app.post('/Account/v.1.0/RefreshPrivacySettings', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end(JSON.stringify(data));
            }
        }

        function storeUserConsent(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                try {
                    if (data) {
                        _logger.info('Updating user consent with new data.');
                        api.SetPrivacySettings(doReply, data);
                    }
                } catch (err) {
                    doReply(err);
                }
            }
        }

        function refreshUserConsentFromJarvis(err, data) {
            if (err || !data) {
                replyWithError(res, err);
            }
            else {
                _logger.info(`Jarvis target environment: ${jarvisServer}`);
                try {
                    GetJarvisSessionToken(storeUserConsent, JSON.parse(data.userInfo));
                } catch (err) {
                    doReply(err);
                }                
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                RestartUserConsentRefreshInterval();
                api.UserToken(refreshUserConsentFromJarvis, false);
            }
            catch (err) {
                replyWithError(res, err);
            }
        });
    });

    RestartUserConsentRefreshInterval();

    function UserTokenCallback(sessionData) {
        try {
            sessionData.userInfo = JSON.parse(sessionData.userInfo);
            if(sessionData.userInfo && sessionData.userInfo.userId)
            {
                NvBackendAPI.notifyUserIdChange(sessionData.userInfo.userId);
            }
        } catch(e) { }
        _logger.info('User Token Change Notification with data: ' + JSON.stringify(sessionData));
        setImmediate(function () { io.emit('/Account/v.1.0/UserToken', sessionData); });
    }
    api.SetUserTokenCallback(UserTokenCallback);

    function PrivacySettingsCallback(consentData) {
        _logger.info('Consent Change Notification with data: ' + JSON.stringify(consentData));
        setImmediate(function () { io.emit('/Account/v.1.0/PrivacySettings', consentData); });
    }
    api.SetPrivacySettingsCallback(PrivacySettingsCallback);

    //! Returns the version of this module
    //! This must be the last declaration in the file. Anything below it will be masked.
    return {
        version: function version() {
            return api.GetVersion();
        },
        initialize: function initialize() {
            return new Promise(function CreateInitializationPromise(resolve, reject) {
                api.Initialize(NativeCallbackToPromise(resolve, reject));
            });
        },
        cleanup: function cleanup() {
            api.Cleanup();
        },
        UserToken: api.UserToken
    };
};
