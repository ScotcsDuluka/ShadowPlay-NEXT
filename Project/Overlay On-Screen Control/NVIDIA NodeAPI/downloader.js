/* Copyright (c) 2015-2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'
var lastReceivedTime = undefined;
var exitCriteria = false;
var nvLogger;
var DOWNLOAD_STATUS  = {
	UNDEFINED: -1,
    DOWNLOADING: 0,
    PAUSED: 1,
    COMPLETED: 2,
    RETRYING: 3,
	PAUSED_FOR_FAILED: 4,
	STOPPED_FOR_FAILED: 5,
    CHECKSUM_VERIFY_FAILS: 6,
    SIGNATURE_VERIFICATION_FAILS: 7,
    DISK_WRITE_FAIL: 8,
	DOWNLOAD_ERROR: 9
};
var NV_ERRORCODES = {
	CHECKSUM_VERIFY_FAILS: 1001,
	SIGNATURE_VERIFICATION_FAILS: 1002,
	DISK_WRITE_FAIL: 1003
};
var CURLE_OK = 0;

function NativeCallbackToPromise(resolve, reject) {
    return function (err, data) {
        //
        // HACK: for some reasons node may delay resolving promises and calling .then
        // until next tick when reject or resolve is called from native addon.
        // This setImmediate forces execution to be continued in next tick.
        //
		if(err){
			setImmediate((function () { reject(err); }));
		}
        else {
			setImmediate((function () { resolve(data); }));
        }
    }
};

module.exports = function(app, io, logger){

	nvLogger = logger;
    try {
        var downloaderApi = require('./shims/Downloader.js');
    } catch (e) {
        nvLogger.info('Failed to load downloader Addon', e);
        return null;
    }

    function handleException(err, res, httpCode){
        if (httpCode) {
        res.writeHead(httpCode, { 'Content-Type': 'text/html;charset=utf-8' });
		}
		else if ('invalidArgument' in err) {
			res.writeHead(400, { 'Content-Type': 'text/html;charset=utf-8' });
		}
		else if ('notFound' in err) {
			res.writeHead(404, { 'Content-Type': 'text/html;charset=utf-8' });
		}
		else {
			res.writeHead(500, { 'Content-Type': 'text/html;charset=utf-8' });
		}
		res.end(err.name + ': ' + err.message);
    }

	function startDownload(version, url, downloadType){
		var args = Array.prototype.slice.call(arguments);
		return new Promise(function(resolve, reject){

			try{
				args.unshift(NativeCallbackToPromise(resolve, reject));
				downloaderApi.startDownload.apply(this, args);
			} catch (err){
				nvLogger.error(err);
				reject(err);
			}
		});
	}

    app.post('/download/v.0.1/start/:version/:url/:downloadType', function (req, res) {

      var version = req.params.version;
      var url = req.params.url;
	  var downloadType = parseInt(req.params.downloadType);

      startDownload(version, url, downloadType).then(function(retValue){
		  res.writeHead(200, { 'Content-Type': 'application/json' });
		  res.end(JSON.stringify(retValue));
	  }, function(err){
		  return handleException(err, res);
	  });
    });

    app.post('/download/v.0.1/pause/:taskId', function (req, res) {

      function doReply(err, retValue){
		  if(err){
			handleException(err, res);
		  }
		  else{
			res.writeHead(200, { 'Content-Type': 'application/json' });
			res.end(JSON.stringify(retValue));
		  }
      }

      var taskId = req.params.taskId;

      var retValue;
      try {
        retValue = downloaderApi.pauseDownload(doReply, taskId);
      } catch  (e) {
        return handleException(e, res);
      }
    });

    app.post('/download/v.0.1/resume/:taskId', function (req, res) {

      function doReply(err, retValue){
		  if(err){
			handleException(err, res);
		  }
		  else{
			res.writeHead(200, { 'Content-Type': 'application/json' });
			res.end(JSON.stringify(retValue));
		  }
      }

      var taskId = req.params.taskId;

      var retValue;
      try {
        retValue = downloaderApi.resumeDownload(doReply, taskId);
      } catch  (e) {
        return handleException(e, res);
      }

    });

    app.post('/download/v.0.1/stop/:taskId', function (req, res) {

      function doReply(err, retValue){
		  if(err){
			handleException(err, res);
		  }
		  else{
			res.writeHead(200, { 'Content-Type': 'application/json' });
			res.end(JSON.stringify(retValue));
		  }
      }

      var taskId = req.params.taskId;

      var retValue;
      try {
        retValue = downloaderApi.stopDownload(doReply, taskId);
      } catch  (e) {
        return handleException(e, res);
      }

    });

	function getStatus(version, url){
		var args = Array.prototype.slice.call(arguments);
		return new Promise(function(resolve, reject){
			try{
				args.unshift(NativeCallbackToPromise(resolve, reject));
				downloaderApi.getStatus.apply(this, args);
			} catch (err){
				nvLogger.error(err);
				reject(err);
			}
		});
	}

    app.get('/download/v.0.1/status/:taskId', function (req, res) {

		var taskId = req.params.taskId;

		getStatus(taskId).then(function (retValue){
		  res.writeHead(200, { 'Content-Type': 'application/json' });
		  res.end(JSON.stringify(retValue));
		}, function(err){
		  handleException(err, res);
		});
    });

	app.get('/download/v.0.1/status/:version/:url', function (req, res) {

		var version = req.params.version;
		var url = req.params.url;

		getStatus(version, url).then(function (retValue){
		  res.writeHead(200, { 'Content-Type': 'application/json' });
		  res.end(JSON.stringify(retValue));
		}, function(err){
		  handleException(err, res);
		});
    });

    function statusUpdate(status){
		lastReceivedTime = Date.now();
		logger.info(status);
        setImmediate(function () {
            io.emit('/download/v.0.1/status', status);
        });
    }

    downloaderApi.setDownloadStatusCallback(statusUpdate);

    return {
        version: function () {
            return downloaderApi.getVersion();
        },
        initialize: function Initialize() {
            return new Promise(function CreateInitializationPromise(resolve, reject) {
                downloaderApi.initialize(NativeCallbackToPromise(resolve, reject));
            });
        },
        startDownload: startDownload,
        getStatus: getStatus,
        canNodeExitNow: function () {
            return exitCriteria;
        },
        cleanup: function () {
            downloaderApi.cleanup();
        }
    };
}