/* Copyright (c) 2019-2023, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

(function(){

    'use strict'
    var downloaderAPI     = require('./shims/Downloader.js'),
        https             = require("https"),
        _                 = require("underscore"),
        store             = require('nv-localstore'),
        fs                = require('fs');

    const config = require('./config.json');

    var NvAutoGFEDownloadVersion = '1.0.2';
    var downloadQueue = []; //temp queue
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
    var POST_DOWNLOAD_PROCESSING_STATUS = {
        UNDEFINED: -1,
        EXTRACTION_PROCESSING: 0,
        EXTRACTION_COMPLETED: 1,
        EXTRACTION_FAILED: 2,
        PATCH_PROCESSING: 3,
        PATCH_COMPLETED: 4,
        PATCH_FAILED: 5
    };

    var DOWNLOAD_TYPE = {
		OTHER: 0,
		DRIVER_DOWNLOAD: 1,
		AUTO_DRIVER_DOWNLOAD: 2,
		GFE_SELF_UPDATE: 3,
		GFE_SELF_UPDATE_BETA: 4,
        DIFF_DRIVER_DOWNLOAD: 5,
        DIFF_AUTO_DRIVER_DOWNLOAD: 6,
        DIFF_GFE_SELF_UPDATE: 7,
        DIFF_GFE_SELF_UPDATE_BETA: 8,
        NGXCORE: 9,
        CRD_DOWNLOAD: 10,
        AUTO_CRD_DOWNLOAD: 11
    };
	var NV_ERRORCODES = {
		CHECKSUM_VERIFY_FAILS: 1001,
		SIGNATURE_VERIFICATION_FAILS: 1002,
		DISK_WRITE_FAIL: 1003
    };
    var CHECKFORUPDATE_STATUS = {
        STARTED: 0,
        FINISHED: 1,
        FAILED: 2
    };

    var DIFF_DOWNLOAD_ENABLE_MASK = {
        GFE: 1,
        GFE_BETA: 2,
        DRIVER: 4,
        NGXCORE: 8,
        UNKNOWN: 0
    };
    
    var FEATURES_SUPPORTED = {
        UNDEFINED: 0,
        NGX: 1
    };
	
	var CURLE_OK = 0;
    var nvIO;
    var WSUrlVersion = 'v1.0';
    var WSHost = 'https://ota.nvidia.com/GFE/';
    var WSURLTimeout = 30 * 1000; //30sec
    var WSChannel;
    var pending_rest_responses = [];
    var clientVersion;
	var taskList = [];
    var nvAppDataPath = "";
    var GFEWSConfigFile = "config.json";
    var NvidiaPath = "NVIDIA GeForce Experience";
    var NvidiaWwwPath = NvidiaPath + "/www";
    var OSVersion = "";
    var OSArch64Bit = false;
    var Logger = undefined;
    var nvUtil = undefined;
    var NvAccountAPI = undefined;
    var NvBackendAPI = undefined;
    var buildPreference = null;
    var exitCriteria = false;
    var isBeta;
	var gpuArch = "";
    var isMandatory;
    let clientParams = '';
	var selfUpdateFile;

    function NativeCallbackToPromise(resolve, reject) {
        return function (err, data) {
            if (data) {
                //TODO: log
                setImmediate((function () { resolve(data); }));
            }
            else {
                setImmediate((function () { reject(err); }));
            }
        }
    };

    function isDiffDownloadEnabled(downloadType) {
        var diffDownloadEnabled = false;

        var diffDownloadToggleMask = downloaderAPI.getDiffDownloadToggleMask();
        if (diffDownloadToggleMask == undefined) {
            return diffDownloadEnabled;
        }

        switch (downloadType) {
            case DOWNLOAD_TYPE.GFE_SELF_UPDATE:
                // do not break, we still need to see if the diff download is enabled for GFE or not
            case DOWNLOAD_TYPE.DIFF_GFE_SELF_UPDATE:
                diffDownloadEnabled = ((diffDownloadToggleMask & DIFF_DOWNLOAD_ENABLE_MASK.GFE) == 0) ? false : true;
                break;
            case DOWNLOAD_TYPE.GFE_SELF_UPDATE_BETA:
                // do not break, we still need to see if the diff download is enabled for GFE beta or not
            case DOWNLOAD_TYPE.DIFF_GFE_SELF_UPDATE_BETA:
                diffDownloadEnabled = ((diffDownloadToggleMask & DIFF_DOWNLOAD_ENABLE_MASK.GFE_BETA) == 0) ? false : true;
                break;
            case DOWNLOAD_TYPE.DRIVER_DOWNLOAD:
                // do not break, we still need to see if the diff download is enabled for driver or not
            case DOWNLOAD_TYPE.DIFF_DRIVER_DOWNLOAD:
                diffDownloadEnabled = ((diffDownloadToggleMask & DIFF_DOWNLOAD_ENABLE_MASK.DRIVER) == 0) ? false : true;
                break;
            case DOWNLOAD_TYPE.NGXCORE:
                diffDownloadEnabled = ((diffDownloadToggleMask & DIFF_DOWNLOAD_ENABLE_MASK.NGXCORE) == 0) ? false : true;
                break;
            default:
        }

        return diffDownloadEnabled;
    }

    function statusUpdate(status) {
        if (!status) return;

        if (isDiffDownloadEnabled(status.downloadType)) {
            statusUpdateHandlerWithDiffDownload(status);
        } else {
            statusUpdateOld(status);
        }
    };

    function statusUpdateOld(status) {
        if (!status) return;

        if (status.status == DOWNLOAD_STATUS.COMPLETED && _.contains(taskList, status.taskId)) {
            extractInstaller(status);
            killRequested(); //request for exit if download is completed or previously downloaded.
        }
    };

    function statusUpdateHandlerWithDiffDownload(status) {
        if (!status) return;

        if ((status.status == DOWNLOAD_STATUS.COMPLETED)
            && _.contains(taskList, status.taskId)) {
            doPostProcessing(status)
            .then(function () {
                createUpdateFileAfterPostProcessing(status);
                killRequested();
            }).catch(function (d) {
                Logger.error('doPostProcessing: ' + d);
            });
        }
    };

    var deleteFolderRecursive = function (path) {
        if (fs.existsSync(path)) {
            fs.readdirSync(path).forEach(function (file, index) {
                var curPath = path + "/" + file;
                if (fs.lstatSync(curPath).isDirectory()) { // recurse
                    deleteFolderRecursive(curPath);
                } else { // delete file
                    fs.unlinkSync(curPath);
                }
            });
            fs.rmdirSync(path);
        }
    };

    function extractInstaller(status) {
        var downloadLoc = status.downloadedLocation;
        var path = downloadLoc.substring(0, downloadLoc.lastIndexOf('\\'));
        path = path.substring(0, path.lastIndexOf('\\'));

        nvIO.emit('/download/v.0.1/postProcessingStarted', status.taskId);
        var success = true;
        if (process.platform === 'win32') {
            var execFileSync = require('child_process').execFileSync;
            try {
                var extractPath = path + '\\latest';
                deleteFolderRecursive(extractPath);
                var buffer = execFileSync('../' + NvidiaPath + '/7z.exe',
                                          ['x', '-o' + extractPath, '-y', downloadLoc], { encoding: 'utf8' });
                if (buffer.toString().indexOf('Everything is Ok') != -1) {
                    status.downloadedLocation = path + '\\latest\\setup.exe';
                } else {
                    Logger.error('Extraction didnt finish with success');
                    success = false;
                }
            } catch (e) {
                success = false;
                Logger.error('Extraction failed: ' + e);
            }
        } else {
            //res.status(400);
            success = false;
        }
        createUpdateFile(status);
        if(success) {
            nvIO.emit('/download/v.0.1/postProcessingDone', status.taskId);
        }
        else {
            nvIO.emit('/download/v.0.1/postProcessingFailed', status.taskId);
        }
    };

    function createUpdateFile(status) {
        //access appdata and create the gfeupdate.json file with url.
        var payload = {};
        var downloadLoc = status.downloadedLocation;
        payload.url = status.downloadedLocation;
        payload.isMandatory = isMandatory;
        var path = downloadLoc.substring(0, downloadLoc.lastIndexOf('\\'));
        path = path.substring(0, path.lastIndexOf('\\'));
        fs.writeFileSync(nvAppDataPath + "\\gfeupdate.json", JSON.stringify(payload));
        fs.writeFileSync(path + "\\gfeupdate.json", JSON.stringify(payload));
    }

    function updateNvAppBetaUpdateFile() {
		// get the beta setting persistend in file
        var nvappOTAChannel = store.getValue('autoGFEbeta') == "1" ? 'beta' : 'Official';
		
		try {
			// Don't change the below object format, this is the format
            // which NvApp understand to read or set the beta opt-in state
			var payload = [{'componentName':'nvapp','registry':{'nvappOTAChannel':'Official'}}];
			payload[0].registry.nvappOTAChannel = nvappOTAChannel;
			Logger.info('Payload is', payload);
			// Location where existing gfe setting persist.
			var path = process.env.ProgramData + '\\NVIDIA Corporation\\Downloader\\config';
			// Create the file for NvApp.
			fs.writeFileSync(path + "\\nvapp.json", JSON.stringify(payload));
        }
        catch (err) {
			Logger.error(err);
		}
    }

    function showSelfUpdateNotification() {
        downloaderAPI.ShowNotification(function notificationResponse(error){
            if(error){
                Logger.error('failed to show notification with error: ', error);
            }
        });
    }

    function createUpdateFileAfterPostProcessing(status) {
        var postProcessingStatus = downloaderAPI.getPostProcessingStatus(status.taskId, status.downloadType);
        // post processing also needs to be completed, to consider the update to be ready
        if (((status.downloadType == DOWNLOAD_TYPE.DIFF_GFE_SELF_UPDATE_BETA)
            && (postProcessingStatus == POST_DOWNLOAD_PROCESSING_STATUS.PATCH_COMPLETED)
            || ((status.downloadType == DOWNLOAD_TYPE.GFE_SELF_UPDATE_BETA)
            && (postProcessingStatus == POST_DOWNLOAD_PROCESSING_STATUS.EXTRACTION_COMPLETED)))
            ||
            (((status.downloadType == DOWNLOAD_TYPE.NGXCORE)
            && (postProcessingStatus == POST_DOWNLOAD_PROCESSING_STATUS.EXTRACTION_COMPLETED)))
            ||
            ((status.downloadType == DOWNLOAD_TYPE.DIFF_GFE_SELF_UPDATE)
            && (postProcessingStatus == POST_DOWNLOAD_PROCESSING_STATUS.PATCH_COMPLETED)
            || ((status.downloadType == DOWNLOAD_TYPE.GFE_SELF_UPDATE)
            && (postProcessingStatus == POST_DOWNLOAD_PROCESSING_STATUS.EXTRACTION_COMPLETED)))) {
            //access appdata and create the gfeupdate.json file with url.
            var payload = {};
            var downloadLoc = downloaderAPI.getBackedupPackagePath(status.downloadType);
            payload.url = downloadLoc;
            payload.isMandatory = isMandatory;
            var path = downloadLoc.substring(0, downloadLoc.lastIndexOf('\\'));
            // we need to three folders up Postprocessing\<DownloadType>\task_folder
            path = path.substring(0, path.lastIndexOf('\\'));
            path = path.substring(0, path.lastIndexOf('\\'));
            path = path.substring(0, path.lastIndexOf('\\'));
            fs.writeFileSync(nvAppDataPath + "\\gfeupdate.json", JSON.stringify(payload));
            fs.writeFileSync(path + "\\gfeupdate.json", JSON.stringify(payload));
            if (status.downloadType == DOWNLOAD_TYPE.NGXCORE) {
                showSelfUpdateNotification();
            }
            else {
                function featureSupportResponse(error, featureSupported) {

                    if (error) {
                        Logger.error('failed to get feature support response', error);
                        return;
                    }

                    Logger.info('feature Support response:', featureSupported);

                    if ((FEATURES_SUPPORTED.NGX & featureSupported) == 1) {
                        showSelfUpdateNotification();
                    }
                }
                downloaderAPI.checkFeaturesSupported(featureSupportResponse);
            }
            nvIO.emit('/download/v.0.1/postProcessingDone', status.taskId);
        }
    }

    function clearPendingRequests(data){
        data = data || {};
        if(pending_rest_responses.length > 0){
             _.each(pending_rest_responses, function(res, key){
                if(data.status == DOWNLOAD_STATUS.COMPLETED && data.percentComplete == 100){
                    res.send(data.downloadedLocation); //HTTP end()
                } else{
                    res.status(404).send('');
                }
            });
        }
        pending_rest_responses = [];
    };

    function initiateGFEDownload(val) {
        return new Promise(function (resolve, reject) {
            try {
                var betaOption = store.getValue('autoGFEbeta');
                var downloadType = betaOption === "1" ? DOWNLOAD_TYPE.GFE_SELF_UPDATE_BETA : DOWNLOAD_TYPE.GFE_SELF_UPDATE;
                downloaderAPI.startDownload(NativeCallbackToPromise(resolve, reject), val.version, val.url, downloadType);
                downloadQueue.push({ url: val.url, ver: val.version });
            } catch (err) {
                Logger.error(err);
                reject(err);
            }
        });
    };

    function doPostProcessing(status) {
        return new Promise(function (resolve, reject) {
            try {
                nvIO.emit('/download/v.0.1/postProcessingStarted', status.taskId);
                downloaderAPI.doPostProcessing(NativeCallbackToPromise(resolve, reject), status.version, status.downloadUrl);
            } catch (err) {
                nvIO.emit('/download/v.0.1/postProcessingFailed', status.taskId);
                Logger.error(err);
                reject(err);
            }
        });
    }

    function checkforNGXUpdate() {
        try {
            Logger.info('Check for NGX Begins');
            function featureSupportResponse(error, featureSupported) {

                if (error) {
                    Logger.error('failed to get feature support response', error);
                    return;
                }

                Logger.info('feature Support response:', featureSupported);

                if (featureSupported == undefined) {
                    Logger.info('featureSupported is undefined, No features supported');
                }
                else {
                    if ((FEATURES_SUPPORTED.NGX & featureSupported) == 1) {
                        
                        let ngxCoreVersion = getNGXCoreVersion();
                        var val = {};
                        val.downloadType = DOWNLOAD_TYPE.NGXCORE;
                        val.version = ngxCoreVersion;
                        val.url = 'http://ota-downloads.nvidia.com/StandaloneNgx/NGX_' + ngxCoreVersion + '.exe';
                        var filen = val.url.match(/([^\/.]+)$|([^\/]+)(\.[^\/.]+)$/)[0];
                        initiateNGXDownload({ filename: filen, url: val.url, version: val.version, downloadType: val.downloadType })
                            .then(function (data) {
                                taskList.push(data.taskId);
                                clearPendingRequests(data); //Dont block the REST requests
                                if (data.status == DOWNLOAD_STATUS.COMPLETED) {
                                    createUpdateFile(data);
                                    killRequested();
                                }
                            })
                            .catch(function (d) {
                                Logger.error('initiateNGXDownload: ' + d);
                            });
                    }
                }
            }

            downloaderAPI.checkFeaturesSupported(featureSupportResponse);
        }
        catch (err) {
            Logger.error(err);
        }
    }

    function initiateNGXDownload(val) {
        return new Promise(function (resolve, reject) {
            try {
                downloaderAPI.startDownload(NativeCallbackToPromise(resolve, reject), val.version, val.url, val.downloadType);
                downloadQueue.push({ url: val.url, ver: val.version });
            }
            catch (err) {
                Logger.error(err);
                reject(err);
            }
        });
    }

    // create two separate functions for normal download and diff download, and call them based on diffDownloadToggleMask
    function initiateGFEDownloadWithDiffDownload(val){
        return new Promise(function(resolve, reject){
			try{
			    var betaOption = store.getValue('autoGFEbeta');
			    var downloadType = betaOption === "1" ? DOWNLOAD_TYPE.GFE_SELF_UPDATE_BETA : DOWNLOAD_TYPE.GFE_SELF_UPDATE;
			    var backedupGFEPackage = downloaderAPI.getBackedupPackagePath(downloadType);
			    var downloadUrl = val.url;
				fs.stat(backedupGFEPackage, function statCallback(err, stats) {
				    Logger.info(val.diffUrl);
				    Logger.info(stats);
				    Logger.info(err);
				    // check if the previous PFW of previous version exists.
				    if ((backedupGFEPackage != undefined)
                            && (stats != undefined)
                            && (!err)
                            && (stats.size > 0)
                            && (val.diffUrl)) {
				        if (!downloaderAPI.isPackageIgnored(val.version, val.diffUrl, downloadType)) {
				            // we should first download the archive of patches
				            downloadUrl = val.diffUrl;
				            downloadType = betaOption === "1" ? DOWNLOAD_TYPE.DIFF_GFE_SELF_UPDATE_BETA : DOWNLOAD_TYPE.DIFF_GFE_SELF_UPDATE;
				            Logger.info('Download from diffUrl: ' + downloadUrl);
				        } else {
				            Logger.info('Package is ignored download from fullUrl: ' + downloadUrl);
				        }
				    } else {
				        // when it is fresh install, backed up package path will be empty
				        // only in that case we're supposed to see this.
				        var log = 'No backup available, download from fullUrl: ' + downloadUrl + '\n';
				        log.concat('backedupGFEPackage: ' + backedupGFEPackage + '\n');
				        log.concat('stats: ' + JSON.stringify(stats) + '\n');
				        log.concat('err: ' + JSON.stringify(err) + '\n');
				        log.concat('val.diffUrl: ' + val.diffUrl + '\n');
				        Logger.info(log);
				    }
				    downloaderAPI.startDownload(NativeCallbackToPromise(resolve, reject), val.version, downloadUrl, downloadType);
				    downloadQueue.push({ url: downloadUrl, ver: val.version });
				});
			} catch (err){
				Logger.error(err);
				reject(err);
			}
		});
    };
	
	function fileExists(path) {
		try {
			const st = fs.statSync(path);
			return st.isFile();
		}
		catch (err) {
			return false;
		}
	}
	
	function readStatusFile() {
		let returnValue = null;
		if (fileExists(selfUpdateFile)) {
			try {
				returnValue = JSON.parse(fs.readFileSync(selfUpdateFile));
			} catch (err) {
				Logger.error('Could not read update status file');
			}
		}
		else {
			Logger.error('Update status file does not exist.....');
		}
		return returnValue;
	}

    function onGFEUpdateFound(data, isBetaRequested) {
        try {
            var retData = {};
            retData.isBetaRequested = isBetaRequested;
            data = JSON.parse(data);
        } catch (e) {
            Logger.error("Update Service error: " + data);
            killRequested();
            clearPendingRequests();
            retData.returnCode = CHECKFORUPDATE_STATUS.FAILED;
            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
            return;
        }
        if (data.url == undefined) {
            Logger.error('Invalid/Empty update Url');
            killRequested();
            clearPendingRequests();
            retData.returnCode = CHECKFORUPDATE_STATUS.FINISHED;
            retData.url = "";
            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
            return;
        }
        retData.returnCode = CHECKFORUPDATE_STATUS.FINISHED;
        retData.isBeta = data.isBeta;
        retData.url = data.url;
        retData.version = data.version;
        nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
        var url = data.url;
        var ver = data.version;
        var filen = url.match(/([^\/.]+)$|([^\/]+)(\.[^\/.]+)$/)[0];
        initiateGFEDownload({ filename: filen, url: url, version: ver })
            .then(function (data) {
                taskList.push(data.taskId);
                clearPendingRequests(data); //Dont block the REST requests
                if (data.status == DOWNLOAD_STATUS.COMPLETED) {
                    createUpdateFile(data);
                    killRequested();
                }
            })
            .catch(function (d) {
                Logger.error('initiateGFEDownload: ' + d);
            });

        /* GFE update response is not XML anymore */
    };

    function onGFEUpdateFoundWithDiffDownload(data, isBetaRequested){

        try {
            var retData = {};
            retData.isBetaRequested = isBetaRequested;
            data = JSON.parse(data);
        }catch(e){
            Logger.error("Update Service error: " + data);
            killRequested();
            clearPendingRequests();
            retData.returnCode = CHECKFORUPDATE_STATUS.FAILED;
            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
            return;
        }
        if(data.url == undefined) {
            Logger.error('Invalid/Empty update Url');
            killRequested();
            clearPendingRequests();
            retData.returnCode = CHECKFORUPDATE_STATUS.FINISHED;
            retData.url = "";
            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
            return;
        }
        retData.returnCode = CHECKFORUPDATE_STATUS.FINISHED;
        retData.isBeta = data.isBeta;
        retData.url = data.url;
        retData.version = data.version;
        retData.diffUrl = data.diffUrl;
        nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
        var url = data.url;
        var diffUrl = data.diffUrl;
        var ver = data.version;
        var filen = url.match(/([^\/.]+)$|([^\/]+)(\.[^\/.]+)$/)[0];
        initiateGFEDownloadWithDiffDownload({ filename: filen, url: url, diffUrl: diffUrl, version: ver })
            .then(function(data){
                taskList.push(data.taskId);
                clearPendingRequests(data); //Dont block the REST requests
            })
            .catch(function(d){
                Logger.error('initiateGFEDownload: ' + d);
            });

        /* GFE update response is not XML anymore */
    };
	
	function isOSWin64() {
		return process.arch === 'x64' || process.env.hasOwnProperty('PROCESSOR_ARCHITEW6432');
	}

    function getWSSelfUpdateUrl() {
        var betaOption = store.getValue('autoGFEbeta');
		var betaStr = "Official";
		if(betaOption === "") {
			betaOption = isBeta;
		}
		betaStr = betaOption === "1" ? "Beta" : "Official";
		store.updateValue('autoGFEbeta', betaOption);
		nvIO.emit('GFEbetaValue', store.getValue('autoGFEbeta'));

        if(WSChannel){
            betaStr = WSChannel;
        }
        var url = "";
        if (clientVersion) {
            //version query is under discussion, using 3.0.0.0
            var params = {};
            params.version = clientVersion;
            params.channel = betaStr;
            params.osVersion = OSVersion;
            params.is64bit = OSArch64Bit;
			params.isOs64bit = isOSWin64();
            params.userBuildPreference = buildPreference;
			params.gpuArch = gpuArch;
            //'{"version": "3.0.0.0", "channel": "' + betaStr +'"}';
            url = WSHost + WSUrlVersion + '/self-update/?' + encodeURIComponent(JSON.stringify(params));
        }
        return { updateUrl: url, isBetaRequested: betaOption === "1" ? true : false };
    };
	
	function updateStatusFile(cvData) {
		Logger.debug('Updating the status file...');
		const statusFileStruct = {
			url: cvData.url,
			isMandatory: cvData.isMandatory
		};
		if (fileExists(selfUpdateFile)) {
			try {
				fs.writeFileSync(selfUpdateFile, JSON.stringify(statusFileStruct));
			} catch (err) {
				Logger.error('Unable to update the config file:', err);
			}
		}
		else {
			Logger.error('update file does not exists');
		}
	}
    /*
	checkIfUpdateIsMandatory : Fetches the cloud variable IsMandatory from GXT server
	*/
    function checkIfUpdateIsMandatory() {
        let responseData = '';
        const options = {
            hostname: config.gxtarget.server,
            path: `/${config.gxtarget.cvEndpoint}/${config.gxtarget.version}?` + clientParams,
            method: 'GET',
            headers: {
                'accept': 'application/json'
            }
        };

        return new Promise((resolve, reject) => {
            const req = https.request(options, (res) => {
                res.setEncoding('utf8');
                res.on('data', (chunk) => {
                    Logger.debug('Config data received:', chunk);
                    responseData += chunk;
                });
                res.on('end', () => {
                    try {
                        Logger.debug('Config data received:', responseData);
                        const parsedData = JSON.parse(responseData);
                        isMandatory = getRemoteFlagResponse(parsedData, config.gxtarget.mandatoryUpdateCvName);
                        Logger.debug('Config CV data:', isMandatory);
						
						const selfUpdateStruct = readStatusFile();
						if (selfUpdateStruct && selfUpdateStruct.isMandatory != isMandatory) {
							selfUpdateStruct.isMandatory = isMandatory;
							updateStatusFile(selfUpdateStruct);
						}					
                        resolve(isMandatory);
                    } catch (err) {
                        Logger.error('Config end error:', err);
                        reject(err);
                    }
                })
            });

            req.on('error', (err) => {
                Logger.error('Config request error:', err);
                const selfUpdateStruct = readStatusFile();
                isMandatory = selfUpdateStruct && typeof selfUpdateStruct.isMandatory === 'boolean'
                    ? selfUpdateStruct.isMandatory
                    : false;
                resolve(isMandatory);
            });

            req.end();
        });
    }

    function checkForGFEUpdates(isRecursiveCall) {
        if (typeof isRecursiveCall === "undefined" || isRecursiveCall === null) {
            isRecursiveCall = false;
        }

        var auto_download = store.getValue('autoGFE');
        if(auto_download == "") auto_download = "1";
        if(auto_download !== "1"){
			clearPendingRequests();
			return; //return if check for updates is disabled
		}
        if(OSVersion === ""){
            clearPendingRequests();
           	return;
        }
        
        var retSelfUpdateUrl = getWSSelfUpdateUrl();
        var updateCheckUrl = retSelfUpdateUrl.updateUrl;
        Logger.info('checking for GFE Update... ' + updateCheckUrl);

        var retData = {};
        retData.returnCode = CHECKFORUPDATE_STATUS.STARTED;
        retData.isBetaRequested = retSelfUpdateUrl.isBetaRequested;
        nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
        var req = https.get(updateCheckUrl,
            (res) => {    
                    var data = "";
                    res.setEncoding('binary');
                    res.on('data', (chunk)=>{ data += chunk; });
                    res.on('end', () => {
                        var downloadType = retSelfUpdateUrl.isBetaRequested == true ? DOWNLOAD_TYPE.GFE_SELF_UPDATE_BETA : DOWNLOAD_TYPE.GFE_SELF_UPDATE;
                        var responseData = undefined;
                        try {
                            responseData = JSON.parse(data);
                            if(responseData.betaType) {
                                let key, version = responseData.version;
                                key = 'buildType.' + version.replace(/\./g, "-")
                                store.updateValue(key, responseData.betaType.toLowerCase());
                            }
                        }
                        catch(e) {
                            responseData = undefined;
                        }
                        if ((responseData != undefined)&& (responseData.url == undefined)) { 
                            retData.returnCode = CHECKFORUPDATE_STATUS.FINISHED;
                            retData.url = "";
                            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
                        }
                        else if (isDiffDownloadEnabled(downloadType)) {
                            onGFEUpdateFoundWithDiffDownload(data, retSelfUpdateUrl.isBetaRequested);
                        } else {
                            onGFEUpdateFound(data, retSelfUpdateUrl.isBetaRequested);
                        }
                    });
					Logger.info('Fetching the cloudVariable...');
					checkIfUpdateIsMandatory();
                    res.on('error', (e) => {
                                                Logger.error(e);
                                           });
                    Logger.info(' OTA response statusCode: ' + res.statusCode);
                    Logger.info(' OTA response headers: ' + JSON.stringify(res.headers));
                    fallBacktoOldServer(res.statusCode, isRecursiveCall);
                });
        req.on('error', function(err){
            Logger.error('Update Check Http Error ' + err);
            killRequested();
            clearPendingRequests();
            retData.returnCode = CHECKFORUPDATE_STATUS.FAILED;
            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
         });
        req.setTimeout(WSURLTimeout, function(){
            Logger.error('Update Service URL timedout');
            //exit if request has timedout
            //TODO: do a retry for 2-3 times before exiting
            killRequested();
            clearPendingRequests();
            retData.returnCode = CHECKFORUPDATE_STATUS.FAILED;
            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
        });
    };
	
	//In case new server is not available try with old server (should never happen)
    function fallBacktoOldServer(err, isRecursiveCall)
    {
        if (err === 503 && isRecursiveCall === false)
        {
            WSHost = 'https://services.gfe.nvidia.com/GFE/';
            checkForGFEUpdates(true);
        }
    }
    
    function checkForDriverUpdates(){
        //has the user disabled auto check for drivers?

    };

    function setupDownloadCompleteCallback() {
        downloaderAPI.setDownloadStatusCallback(statusUpdate);
    };

	function getArchiPromise(resolve,reject) {
        try {
            function doReply(err, response) {
                if(err){
                    Logger.error("failed to get hardware info with error: ", err);
                    return resolve(gpuArch);
                }
                if(response.GPU && response.GPU[0] && response.GPU[0].GPUArchitecture) {
                    gpuArch = response.GPU[0].GPUArchitecture; // parseInt(response.GPU[0].GPUArchitecture).toString(10);
					Logger.info('GpuArch',response);
                }
                return resolve(gpuArch);
            }
			Logger.info('GpuArch: ',gpuArch);
            if(gpuArch !== "") {
               return resolve(gpuArch);
            } else {
                var type;
                NvBackendAPI.getHardwareInformation(doReply, type);
            }
        }
        catch (e) {
            Logger.error("Failed to get hardware info with error", e);
            return resolve(gpuArch);
        }
    }
	
    function promiseChainCheckForUpdates(isRecursive) {
        Logger.info("promise chain check for updates");
		Promise.all([new Promise(getClientVersionPromise), new Promise(getGFEBetaPromise), new Promise(getUserBuildPreference), new Promise(getArchiPromise)]).then(function () {
            initDownloadURLAndConfig();
            checkForGFEUpdates(isRecursive);
        }).catch(function (err) {
            var retData = {};
            retData.returnCode = CHECKFORUPDATE_STATUS.FAILED;
            nvIO.emit('/download/v.0.1/checkforGFEUpdate', retData);
            Logger.error("Failed to perform update check " + err);
        });
    };

    function setupDownloaderEndpoints(app){
        app.get('/gfeupdate/autoGFEDownload/:key', function(req, res){
            res.end(store.getValue(req.params.key));
        });
        app.post('/gfeupdate/autoGFEDownload/:key', function (req, res) {
            if (req.is('text/*')) {
                req.setEncoding('utf8');
                req.text = '';
                req.on('data', function (chunk) { req.text += chunk });
                req.on('end', function () {
                    const originalValue = store.getValue(req.params.key);
                    
                    store.updateValue(req.params.key, req.text);
					nvIO.emit('GFEbetaValue', store.getValue('autoGFEbeta'));
                    setImmediate(function () {
                        Logger.info('Emitting ' + req.url);
                        nvIO.emit(req.url, req.text);
                        if ((req.params.key === "autoGFEbeta") && (req.text === "1"))
                        {
                            promiseChainCheckForUpdates();
                        }
                    });
                    if(req.params.key === "autoGFEbeta" && (originalValue != req.text)) {
                        updateNvAppBetaUpdateFile();
                        NvBackendAPI.notifyExperimentalFeaturesChange(req.text === "1");
                    }
                    res.end(req.text);
                });
            }
        });

        //GET new GFE version (explicit)
        app.get('/gfeupdate/autoGFENewVersion/', function(req, res){
            pending_rest_responses.push(res);
            exitCriteria = false;
            promiseChainCheckForUpdates();
        });
    };

    function killRequested(data){
        exitCriteria = true;
    };
    
    function getNGXCoreVersion(){
        var ngxCoreVersion = '';
        try {
            ngxCoreVersion = nvUtil.GetNGXCoreVersionSync();
        } catch (e) {
            Logger.error("Cannot read NGXCore version value from registry" + e);
            ngxCoreVersion = '1.0';//download base version
        }
        
        return ngxCoreVersion;
    }

    function getClientVersionPromise(resolve, reject) {

        if(clientVersion){
            return resolve();//client version already defined
        }

        try {
            clientVersion = nvUtil.GetGFEVersionSync();
            Logger.info("Read GFExperience version from registry: " + clientVersion);
            
        }
        catch (e) {
            Logger.error("Cannot read GFExperience version value from registry" + e);
            reject(e);
            return;
        }

        try {
            var arch = nvUtil.GetGFEArchSync();
            Logger.info("Read GFExperience architecture value from registry: " + arch);
            if (arch.toLowerCase() === 'x64') {
                OSArch64Bit = true;
            }
        }
        catch (e) {
            Logger.error("Cannot read GFExperience architecture value from registry" + e);
        }

        resolve();
    }

    function getGFEBetaPromise(resolve, reject) {
        
        if(isBeta) {
            resolve();
        }

        isBeta = "0";

        try {
            var gfe3beta = nvUtil.GetGFE3BetaFlagSync();
            Logger.info("Read GFE 3 BETA flag from registry: " + gfe3beta);
            //if current gfe build is beta, we don't carry forward perisistence, so don't bother GFE2
            if (gfe3beta == 1) {
                isBeta = "1";
            }
        }
        catch (e) {
            Logger.error("Cannot read GFE 3 BETA flag from registry" + e);
            readGFE2Betaflag();
            // Not a really hard error to DIE.
        }
        //remove GFE2Beta regkey unconditionally
        try {
            nvUtil.DeleteGFE2BetaFlagSync();
        }
        catch (e) {
            Logger.error("Cannot delete GFE 2 BETA flag from registry" + e);
        }
        resolve();
    }
    
    function getUserBuildPreference(resolve, reject) {
        Logger.info("inside promise chain check for updates");
        try {
            function doReply(err, response) {
                if(err){
                    Logger.error("failed to get build preference with error: ", err);
                    return resolve(buildPreference);
                }
                
                buildPreference = JSON.parse(response.userInfo).buildPreference;
                Logger.info("user build Preference", buildPreference);
                if(!buildPreference) {
                    buildPreference = store.getValue('buildType.' + clientVersion.replace(/\./g, "-"));
                    if(buildPreference.length === 0) {
                        buildPreference = undefined;
                    }
                }
                resolve(buildPreference);
            }
            NvAccountAPI.UserToken(doReply, false);
        }
        catch (e) {
            Logger.error("Failed to get user build preference with error", e);
            return resolve(buildPreference);
        }
    }

    function readGFE2Betaflag()
    {
        try {
            var gfe2beta = nvUtil.GetGFE2BetaFlagSync();
            Logger.info("Read GFE 2 BETA flag from registry: " + gfe2beta);
            if (gfe2beta == 1) {
                isBeta = "1";
            }
        }
        catch (e) {
            Logger.error("Cannot read GFE 2 BETA flag from registry" + e);
            // Not a really hard error to DIE.
        }
    }
    
    function getRemoteFlagResponse(gxtResponse, cvName) {
        if (!gxtResponse.cloudVariables || gxtResponse.cloudVariables.length === 0) {
            throw 'Missing CV List'
        }
        const data = gxtResponse.cloudVariables.find((val) => val.name === cvName);
        if (!data || !data.value) {
            throw 'CV not found'
        }
        return data.value.IsMandatoryUpdate;
    }

    function initDownloadURLAndConfig() {
        var os = require('os');

        var rel = os.release().split('.');
        OSVersion = rel[0] + "." + (rel[1] || "");
        try{
            if(OSArch64Bit){
                NvidiaWwwPath = "../../Program Files/NVIDIA Corporation/" + NvidiaWwwPath;
                NvidiaPath = "../../Program Files/NVIDIA Corporation/" + NvidiaPath;
            }
            var config = require("./"+GFEWSConfigFile);
        }catch(e){
            Logger.error(e);
            return;
        }
        if(config.gfservices){
            WSHost = config.gfservices.server;
            WSUrlVersion = config.gfservices.version;
            if(config.gfservices.selfupdate){
                WSChannel = config.gfservices.selfupdate.channel || WSChannel;
                clientVersion = config.gfservices.selfupdate.verOverride || clientVersion;
            }
        }
    };

    module.exports = {
        
        initialize: function (app, io, logger, util, NvCommonTasks, accountAPI, backendAPI, nvSelfUpdateStatusFilePath) {
            clientParams = new URLSearchParams({
                cvName: config.gxtarget.mandatoryUpdateCvName,
                deviceId: 'undefined',
                userId: 'undefined',
                idpId: 'undefined',
                clientId: config.gxtarget.clientId,
                clientVer: clientVersion,
                clientVariant: 'Release',
                deviceOS: config.gxtarget.deviceOS,
                deviceType: 'Desktop',
                deviceMake: 'undefined',
                deviceModel: 'undefined',
                deviceOSVersion: 'undefined',
                clientType: 'Native',
                browserType: 'Chrome',
                clientParams: {}
            }).toString()
			
			if (nvSelfUpdateStatusFilePath) {
				selfUpdateFile = nvSelfUpdateStatusFilePath + '\\gfeupdate.json';
			}
      
            return new Promise(
                function (resolve, reject) {
                    setImmediate(function init() {
                        nvIO = io;
                        Logger = logger;
                        nvUtil = util;
                        NvAccountAPI = accountAPI;
                        NvBackendAPI = backendAPI;

                        if (NvCommonTasks.setIntervalCallback) {
                            NvCommonTasks.setIntervalCallback(promiseChainCheckForUpdates);
                        };

                        setupDownloaderEndpoints(app);

                        // Check for updates after startup.
                        promiseChainCheckForUpdates();
                        // update the beta setting for nvapp after startup.
                        updateNvAppBetaUpdateFile();

                        setupDownloadCompleteCallback();
                        resolve();
                    });
                });
        },

        stop: function () {
            _.each(downloadQueue, function (elem) {
                downloaderAPI.stopDownload(function () { }, elem.ver);
            });
        },

        version: function () {
            return NvAutoGFEDownloadVersion;
        },

        setAppDataPath: function (path) {
            nvAppDataPath = path;
        },

        canNodeExitNow: function () {
            return exitCriteria;
        }
    }
}());
