/* Copyright (c) 2015-2023, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

let https = require('https');
let fs = require('fs');

//! Callback registered from outside to be notified about automatic driver
//! download setting change.
let autoDriverDownloadCallback;


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
            res.writeHead(400, { 'Content-Type': 'text/html;charset=utf-8' });
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

function RegisterExpressEndpoints(app, api, logger, httpServer) {

    function onGetApplications(req, res) {

        function doReply(err, apps) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(apps));
            }
        }

        try {
            api.GetApplications(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/Applications/v.1.0/', onGetApplications);

    function onPostApplicationsRefresh(req, res) {
        function doReply(err, isScanRunning) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(202, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ 'scanAlreadyInProgress': isScanRunning }));
            }
        }

        try {
            var type = req.query.type;
            if (type === 'file') {
                getJSONDataAndDo(req, res, function (content) {
                    api.Scan(doReply, type, content.path);
                });
            } else {
                api.Scan(doReply, type);
            }
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.post('/Applications/v.1.0/refresh', onPostApplicationsRefresh);

    function onPostApplicationsRefreshStates(req, res) {
        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(202);
                res.end();
            }
        }

        try {
            api.RefreshApplicationsState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.post('/Applications/v.1.0/refreshStates', onPostApplicationsRefreshStates);

    function onGetLastApplicationScanTime(req, res) {

        function doReply(err, t) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                if (t) {
                    res.writeHead(200, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify(t));
                }
                else {
                    res.writeHead(404, { 'Content-Type': 'text/html;charset=utf-8' });
                    res.end('There were no application scans');
                }
            }
        }

        try {
            api.GetLastApplicationScanTime(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/Applications/v.1.0/lastRefreshTime', onGetLastApplicationScanTime);

    function onPostApplicationsLaunch(req, res) {
        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(202);
                res.end();
            }
        }

        try {
            api.LaunchApplication(doReply, parseInt(req.params.id));
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.post('/Applications/v.1.0/:id/launch', onPostApplicationsLaunch);

    function onGetApplicationsSliderSettings(req, res) {

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
            api.GetSliderSettings(doReply, parseInt(req.params.id));
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/Applications/v.1.0/:id/sliderSettings', onGetApplicationsSliderSettings);

    function onGetApplicationsSettingsSpace(req, res) {
        function doReply(err, data) {
            //additional check for bug 200230315
            if (data && (typeof data === 'object') && data['settings'] && (data['settings'] instanceof Array) && data['settings'].length > 0) {
                logger.debug("GetSettingsSpace was successful for id " + req.params.id);
            }
            else {
                logger.error("GetSettingsSpace returned invalid result for id " + req.params.id + ": " + JSON.stringify(data));
            }
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        var tweak = {}
        try {
            tweak = JSON.parse(req.params.tweak);
        }
        catch (err) {
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetSettingsSpace(doReply, parseInt(req.params.id), tweak);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/Applications/v.1.0/:id/settingsSpace/:tweak', onGetApplicationsSettingsSpace);

    function onGetApplicationsState(req, res) {

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
            api.GetApplicationState(doReply, parseInt(req.params.id));
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/Applications/v.1.0/:id/state', onGetApplicationsState);

    function onPostApplicationsTargetACPosition(req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(202);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetTargetACPosition(doReply, parseInt(req.params.id), content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    }

    app.post('/Applications/v.1.0/:id/targetACPosition', onPostApplicationsTargetACPosition);

    function onPostApplicationsTargetDCPosition(req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(202);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetTargetDCPosition(doReply, parseInt(req.params.id), content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    }

    app.post('/Applications/v.1.0/:id/targetDCPosition', onPostApplicationsTargetDCPosition);

    ////////////////////////////////////////////////////////////////////////////////

    function onSetUrls(req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                logger.debug("Setting jarvis url to " + content.jarvis.server + " and gfwsl url to " + content.gfwsl.server);
                api.SetUrls(doReply, content.jarvis.server, content.gfwsl.server);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    }

    app.post('/Applications/v.1.0/setUrls', onSetUrls);

    ////////////////////////////////////////////////////////////////////////////////

    function onGetLauncherPid(req, res) {

        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ 'pid': data }));
            }
        }

        try {
            api.LauncherPid_Get(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/Applications/v.1.0/LauncherPID', onGetLauncherPid);

    ////////////////////////////////////////////////////////////////////////////////

    function onGetSupportedApplications(req, res) {

        function doReply(err, apps) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(apps));
            }
        }

        try {
            api.GetSupportedApplications(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/SupportedApplications/v.1.0/', onGetSupportedApplications);

    ////////////////////////////////////////////////////////////////////////////////

    function onGetSettingsTranslation(req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        var shortname = req.query.shortname;
        if (!shortname) {
            var err = new Error('"shortname" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        var locale = req.query.locale;
        if (!locale) {
            var err = new Error('"locale" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetTranslation(doReply, shortname, locale);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/ApplicationSettingsTranslation/v.1.0/', onGetSettingsTranslation);

    ////////////////////////////////////////////////////////////////////////////////

    app.get('/FramerateLimiter/v.0.1/support', function (req, res) {

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
            api.GetFramerateLimiterSupport(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.get('/FramerateLimiter/v.0.1/state', function (req, res) {

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
            api.GetFramerateLimiterState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/FramerateLimiter/v.0.1/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetFramerateLimiterState(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/FramerateLimiter/v.0.1/:id/frlstate', function (req, res) {

        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.id) {
            var err = new Error('"id" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetBatteryBoostFRLState(doReply, parseInt(req.params.id));
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/FramerateLimiter/v.0.1/:id/frlstate', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        if (!req.params.id) {
            var err = new Error('"id" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetBatteryBoostFRLState(doReply, parseInt(req.params.id), content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    ////////////////////////////////////////////////////////////////////////////////

    app.get('/QuietMode/v.1.0/state', function (req, res) {

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
            api.GetQuietModeState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/QuietMode/v.1.0/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetQuietModeState(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/QuietMode/v.1.0/support', function (req, res) {
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
            api.GetQuietModeSupportState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.get('/QuietMode/v.1.0/:id/frlstate', function (req, res) {

        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.id) {
            var err = new Error('"id" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetQuietModeFRLState(doReply, parseInt(req.params.id));
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/QuietMode/v.1.0/:id/frlstate', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        if (!req.params.id) {
            var err = new Error('"id" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetQuietModeFRLState(doReply, parseInt(req.params.id), content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    ////////////////////////////////////////////////////////////////////////////////

    app.get('/QuietMode2/v.1.0/state', function (req, res) {

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
            api.GetQuietMode2State(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/QuietMode2/v.1.0/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetQuietMode2State(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/QuietMode2/v.1.0/support', function (req, res) {
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
            api.GetQuietMode2SupportState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });
	
	    app.get('/Settings/v.1.0/globalConfig', function (req, res) {

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
            api.GetGlobalConfigurationSettings(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/Settings/v.1.0/globalConfig', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetGlobalConfigurationSettings(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/Nis2/v.1.0/:cmsId/state', function (req, res) {

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
            let cmsId;
            if (!req.params.cmsId) {
                // get the global state for NIS2.0
                cmsId = "0";
            }
            else {
                // get the NIS2.0 sharpen values for the given cmsId
                cmsId = req.params.cmsId;
            }
            api.GetNIS2State(doReply, cmsId);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/Nis2/v.1.0/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetNIS2State(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    ////////////////////////////////// DeepDVC //////////////////////////////////////////////

    app.get('/DeepDVC/v.1.0/:cmsId/state', function (req, res) {

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
            let cmsId;
            if (!req.params.cmsId) {
                // get the global support state for DeepDVC
                cmsId = "0";
            }
            else {
                // get the vibrance values for the given cmsId
                cmsId = req.params.cmsId;
            }

            api.GetDeepDVCState(doReply, cmsId);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/DeepDVC/v.1.0/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetDeepDVCState(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });


    ////////////////////////////////////////////////////////////////////////////////
    // Whisper Mode 2.1
    ////////////////////////////////////////////////////////////////////////////////
    app.get('/WhisperMode2.1/v.1.0/state', function (req, res) {

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
            api.GetWhisperMode21State(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/WhisperMode2.1/v.1.0/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetWhisperMode21State(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/WhisperMode2.1/v.1.0/support', function (req, res) {
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
            api.GetWhisperMode21SupportState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });
    ////////////////////////////////////////////////////////////////////////////////
    // Battery Mode 2.0
    ////////////////////////////////////////////////////////////////////////////////

    app.get('/FramerateLimiter2/v.1.0/state', function (req, res) {

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
            api.GetBatteryBoost2State(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/FramerateLimiter2/v.1.0/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetBatteryBoost2State(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/FramerateLimiter2/v.1.0/support', function (req, res) {
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
            api.GetBatteryBoost2SupportState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

   /////////////////////////////////////////////////////////////////////////////////
    app.get('/PerformanceMode/v.1.0/state', function (req, res) {

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
            api.GetPerformanceModeState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/PerformanceMode/v.1.0/state', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetPerformanceModeState(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/PerformanceMode/v.1.0/support', function (req, res) {
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
            api.GetPerformanceModeSupportState(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });
    ////////////////////////////////////////////////////////////////////////////////

    app.get('/VisualOPS/v.1.0/:shortname', function (req, res) {

        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                if (data.ready) {
                    data.manifestURL = 'http://localhost:' + httpServer.address().port + '/VisualOPS/v.1.0/' + req.params.shortname + '/manifest.json';
                }
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(data));
            }
        }

        if (!req.params.shortname) {
            var err = new Error('"shortname" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetVOPSStatus(doReply, req.params.shortname);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.get('/VisualOPS/v.1.0/:shortname/:filename', function (req, res) {

        function doManifestReply(path) {
            return function doReply(err, data) {
                if (err) {
                    replyWithError(res, err);
                }
                else {
                    for (var i = 0; i < data.settingInfo.length; i++) {
                        data.settingInfo[i].image = ('file:///' + path + '/' + data.settingInfo[i].image).replace(/\\/g, '/');
                    }
                    res.writeHead(200, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify(data));
                }
            }
        }

        function onPathAvailable(err, path) {
            if (err) {
                replyWithError(res, err, 404);
            }
            else {
                //
                // Manifest is a special case.
                //
                if (req.params.filename === 'manifest.json') {
                    var manifestPath = path + '\\manifest.xml';
                    api.LoadVOPSManifest(doManifestReply(path), manifestPath);
                    return;
                } else {
                    var err = new Error('Invalid file requested');
                    err.invalidArgument = true;
                    replyWithError(res, err);
                }
            }
        }

        if (!req.params.shortname) {
            var err = new Error('"shortname" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        if (!req.params.filename) {
            var err = new Error('"filename" argument is required');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        if (req.params.filename.indexOf("./") != -1
            || req.params.filename.indexOf("/.") != -1
            || req.params.filename.indexOf(".\\") != -1
            || req.params.filename.indexOf("\\.") != -1) {
            var err = new Error('"filename" argument is invalid');
            err.invalidArgument = true;
            replyWithError(res, err);
            return;
        }

        try {
            api.GetVOPSPath(onPathAvailable, req.params.shortname);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    ////////////////////////////////////////////////////////////////////////////////

    app.get('/DriverUpdates/v.1.0/', function (req, res) {
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
            api.GetDriverUpdates(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.get('/DriverUpdates/v.1.0/GetDriverTypePreference', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ 'driverType': data }));
            }
        }

        try {
            api.GetUserDriverTypePreference(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/DriverUpdates/v.1.0/SetDriverTypePreference', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ 'driverType': data }));
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SetUserDriverTypePreference(doReply, content.driverType);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.get('/DriverUpdates/v.1.0/AutomaticDriverDownloadEnabled', function (req, res) {
        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ 'enabled': data }));
            }
        }

        try {
            api.GetAutomaticDriverDownloadEnabled(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/DriverUpdates/v.1.0/AutomaticDriverDownloadEnabled', function (req, res) {

        var enabled;

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
                if (enabled && autoDriverDownloadCallback) {
                    autoDriverDownloadCallback();
                }
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                try {
                    enabled = content.enabled;
                } catch (e) {
                    e.invalidArgument = true;
                    replyWithError(e);
                    return;
                }
                api.SetAutomaticDriverDownloadEnabled(doReply, enabled);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    app.post('/DriverUpdates/v.1.0/refresh', function (req, res) {
        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(202);
                res.end();
            }
        }

        try {
            api.StartDriverUpdatesCheck(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    ////////////////////////////////////////////////////////////////////////////////

    app.get('/Settings/v.1.0/EditorPreviewMode', function (req, res) {
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
            api.GetGRDEditorPreviewMode(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    ////////////////////////////////////////////////////////////////////////////////

    app.get('/TrayIcon/v.1.0/DriverUpdateNotification', function (req, res) {

        function doReply(err, data) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ 'enabled': data }));
            }
        }

        try {
            api.GetTrayDriverUpdateNotification(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    });

    app.post('/TrayIcon/v.1.0/DriverUpdateNotification', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                var enabled;
                try {
                    enabled = content.enabled;
                } catch (e) {
                    e.invalidArgument = true;
                    replyWithError(e);
                    return;
                }
                api.SetTrayDriverUpdateNotification(doReply, enabled);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });

    ////////////////////////////////////////////////////////////////////////////////

    app.post('/Feedback/v.0.1/', function (req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(202);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.AddFeedback(doReply, content)
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    });
    
    ////////////////////////////////////////////////////////////////////////////////

    function HwInfoHandler(type) {
        return function (req, res) {
            function doReply(err, data) {
                if (err) {
                    logger.error('Request ' + req.method + ' ' + req.originalUrl + ' failed: ' + err);
                    replyWithError(res, err);
                }
                else {
                    logger.debug('Request ' + req.method + ' ' + req.originalUrl + ' succeeded and driver version is ' + data.DriverVersion);
                    res.writeHead(200, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify(data));
                }
            }

            try {
                api.GetHardwareInformation(doReply, type);
            }
            catch (err) {
                logger.error('Request ' + req.method + ' ' + req.originalUrl + ' failed: ' + err);
                replyWithError(res, err);
                logger.error(err);
            }
        };
    };

    ////////////////////////////////////////////////////////////////////////////////

    app.get('/HardwareInformation/v.0.1/', HwInfoHandler());
    app.get('/HardwareInformation/v.0.2/', HwInfoHandler());
    app.get('/HardwareInformation/v.0.2/generic', HwInfoHandler('generic'));

    ////////////////////////////////////////////////////////////////////////////////

    function onPostOOTBStatus(req, res, status) {
        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        try {
            api.SetOOTBStatus(doReply, status);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    function onPostOOTBStarted(req, res) {
        onPostOOTBStatus(req, res, 1);
    }

    function onPostOOTBFinished(req, res) {
        onPostOOTBStatus(req, res, 2);
    }

    app.post('/OOTB/v.1.0/started', onPostOOTBStarted);
    app.post('/OOTB/v.1.0/finished', onPostOOTBFinished);

    ////////////////////////////////////////////////////////////////////////////////

    function onGetSearchPaths(req, res) {

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
            api.SearchPaths_Get(doReply);
        }
        catch (err) {
            replyWithError(res, err);
            logger.error(err);
        }
    }

    app.get('/SearchPaths/v.1.0/', onGetSearchPaths);

    function onPostSearchPathAdd(req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SearchPaths_Add(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    }

    app.post('/SearchPaths/v.1.0/add', onPostSearchPathAdd);

    function onPostSearchPathRemove(req, res) {

        function doReply(err) {
            if (err) {
                replyWithError(res, err);
            }
            else {
                res.writeHead(200);
                res.end();
            }
        }

        getJSONDataAndDo(req, res, function (content) {
            try {
                api.SearchPaths_Remove(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        });
    }

    app.post('/SearchPaths/v.1.0/remove', onPostSearchPathRemove);

    ////////////////////////////////////////////////////////////////////////////////

    app.post('/SignedGPUID/v.1.0/', function (req, res) {
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
                api.GetSignedGPUID(doReply, content);
            }
            catch (err) {
                replyWithError(res, err);
                logger.error(err);
            }
        })
    });
}

module.exports = function (httpServer, app, io, logger, NvPiplConfig) {
    if (app === undefined || io === undefined || logger === undefined) {
        throw 'You need to provide express app, socket io and logger';
    }

    let api = require('./shims/NvBackendAPINode.js');
    logger.debug('NvBackendAPI.js: native dependency loaded');

    function EmitNotification(name, data) {
        setImmediate(function () { io.emit(name, data); });
    }

    function logError(err, data) {
        if (err) {
            logger.error('SetUrls internal error:', err);
        }
    }

    function onPiplConfigUpdate(cvData) {
        try {
            const jarvisServer = cvData.configData.jarvis.server;
            const gfwslServer = cvData.configData.gfwsl.server;
            api.SetUrls(logError, jarvisServer, gfwslServer);
            logger.debug('Updated PiplConfig in NvBackend');
        } catch (err) {
            logger.error('Unable to update PiplConfig in NvBackend:', err);
        }
    }

    NvPiplConfig.setNvBackendCallback(onPiplConfigUpdate);

    function ScanStatusCallback(status) {
        EmitNotification('/Applications/v.1.0/refresh', status);
    }

    api.SetScanStatusCallback(ScanStatusCallback);

    function ApplicationRemovedCallback(applicationId) {
        var notification = {};
        notification[applicationId] = {};
        EmitNotification('/Applications/v.1.0/', notification);
    }

    api.SetApplicationRemovedCallback(ApplicationRemovedCallback);

    function ApplicationChangedCallback(application) {
        var notification = {};
        notification[application.id] = application;
        EmitNotification('/Applications/v.1.0/', notification);
    }

    api.SetApplicationChangedCallback(ApplicationChangedCallback);

    function DriverUpdateCheckStatusCallback(status) {
        EmitNotification('/DriverUpdates/v.1.0/refresh', status);
        if (status == 'FINISHED' && autoDriverDownloadCallback) {
            autoDriverDownloadCallback();
        }
    }

    api.SetDriverUpdateCheckStatusCallback(DriverUpdateCheckStatusCallback);

    function RewardsCheckStatusCallback(status, data) {
        EmitNotification('/Rewards/v.1.0/refresh', { status: status, data: data });
    }

    api.SetRewardsCheckStatusCallback(RewardsCheckStatusCallback);

    function ApplicationStateChangedCallback(data) {
        EmitNotification('/Applications/v.1.0/state', data);
    }

    api.SetApplicationStateChangedCallback(ApplicationStateChangedCallback);

    function VopsReadyCallback(data) {
        EmitNotification('/VisualOPS/v.1.0/', data);
    }

    api.SetVopsReadyCallback(VopsReadyCallback);

    function QuietModeStateCallback(data) {
        EmitNotification('/QuietMode/v.1.0/state', data);
    }
    api.SetQuietModeStateCallback(QuietModeStateCallback);

    function QuietModeSupportStateCallback(data) {
        EmitNotification('/QuietMode/v.1.0/support', JSON.parse(data));
    }
    api.SetQuietModeSupportStateCallback(QuietModeSupportStateCallback);

    // QuietMode 2
    function QuietMode2StateCallback(data) {
        EmitNotification('/QuietMode2/v.1.0/state', data);
    }
    api.SetQuietMode2StateCallback(QuietMode2StateCallback);

    function QuietMode2SupportStateCallback(data) {
        EmitNotification('/QuietMode2/v.1.0/support', JSON.parse(data));
    }
    api.SetQuietMode2SupportStateCallback(QuietMode2SupportStateCallback);

    // NIS2.0
    function NIS2StateCallback(data) {
        EmitNotification('/Nis2/v.1.0/state', data);
    }

    api.SetNIS2StateCallback(NIS2StateCallback);
    ////////////////////

    //BB2.0
    function BatteryBoost2UIStateCallback(data) {
        EmitNotification('/FramerateLimiter2/v.1.0/state', data);
    }
    api.SetBatteryBoost2NodeCallback(BatteryBoost2UIStateCallback);
    ///////////////////
    function PerformanceModeStateCallback(data) {
        logger.info('emitting PerformanceMode state update', data);
        EmitNotification('/PerformanceMode/v.1.0/state', data);
    }
    api.SetPerformanceModeStateCallback(PerformanceModeStateCallback);

    function PerformanceModeSupportStateCallback(data) {
        logger.info('emitting PerformanceMode support update', data);
        EmitNotification('/PerformanceMode/v.1.0/support', JSON.parse(data));
    }
    api.SetPerformanceModeSupportStateCallback(PerformanceModeSupportStateCallback);

    function NvBackendReadyCallback(data) {
        EmitNotification('/NvBackend/v.1.0/ready', data);
        logger.info('Emitted "/NvBackend/v.1.0/ready" notification');
        NvPiplConfig.getConfig()
            .then(cvData => onPiplConfigUpdate(cvData))
            .catch(err => logger.error('Error setting NvBackend PiplConfig:', err));
    }
    api.SetNvBackendReadyCallback(NvBackendReadyCallback);

    function getDriverUpdates() {
        return new Promise(function (resolve, reject) {

            try {
                api.GetDriverUpdates(NativeCallbackToPromise(resolve, reject));
            } catch (err) {
                logger.error(err);
                reject(err);
            }
        });
    }

    function getAutomaticDriverDownloadEnabled() {
        return new Promise(function (resolve, reject) {

            try {
                api.GetAutomaticDriverDownloadEnabled(NativeCallbackToPromise(resolve, reject));
            } catch (err) {
                logger.error(err);
                reject(err);
            }
        });
    }
	

		 
    return {
        version: function GetVersion() {
            return api.GetVersion();
        },
        initialize: function Initialize() {
            return new Promise(function CreateInitializationPromise(resolve, reject) {
                api.Initialize(NativeCallbackToPromise(resolve, reject));
            }).then(function OnNativeAPIInitialized() {
                RegisterExpressEndpoints(app, api, logger, httpServer);
                logger.info('NvBackendAPI module initialized');
            });
        },
        getAutomaticDriverDownloadEnabled: getAutomaticDriverDownloadEnabled,
        getDriverUpdates: getDriverUpdates,
        reportHttpSuccess: function (callback, data) {
            return api.ReportHttpSuccess(callback, data);
        },
        reportHttpFailure: function (callback, data) {
            return api.ReportHttpFailure(callback, data);
        },
        addNodeJSCrashFeedbackSync: function (data) {
            return api.AddNodeJSCrashFeedbackSync(data);
        },
        setAutoDriverDownloadCallback: function (callback) {
            autoDriverDownloadCallback = callback;
        },
        notifyUiLanguageChange: function (data) {
            return api.NotifyUiLanguageChange(function(err){},data);
        },
        notifyExperimentalFeaturesChange: function (data) {
            return api.NotifyExperimentalFeaturesChange(function(err){}, data);
        },
        getHardwareInformation: function (callback,type) {
            return api.GetHardwareInformation(callback, type);
        },
        notifyUserIdChange: function (data) {
            return api.NotifyUserIdChange(function(err){}, data);
        }
    };
};
