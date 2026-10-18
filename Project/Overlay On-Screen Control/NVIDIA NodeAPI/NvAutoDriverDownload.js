/* Copyright (c) 2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

module.exports = function(NvCommonTasks, NvBackendAPI, downloaderAPI, logger) {

	var NvAutoDriverDownloadVersion = '1.0.0';

	var DOWNLOAD_STATUS  = {
		UNDEFINED: -1,
		DOWNLOADING: 0,
		PAUSED: 1,
		COMPLETED: 2,
		RETRYING: 3,
		PAUSED_FOR_FAILED: 4,
		STOPPED_FOR_FAILED: 5,
		DOWNLOAD_ERROR: 6
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
	var DRIVER_TYPE = {
	    GRD: 0,
	    CRD: 1
	}

	function startDriverDownload(version, url, driverType) {
	    if (downloaderAPI.startDownload) {
	        var downloadType = (driverType == DRIVER_TYPE.GRD) ? DOWNLOAD_TYPE.AUTO_DRIVER_DOWNLOAD : DOWNLOAD_TYPE.AUTO_CRD_DOWNLOAD;
	        downloaderAPI.startDownload(version, url, downloadType).then(function (startDownloadResponse) {
				if(startDownloadResponse){
					if(startDownloadResponse.status === DOWNLOAD_STATUS.DOWNLOADING){
						logger.info('auto download triggered for driver version:', version, 'with url:',  url);
					} else {
						logger.error('Invalid staus while triggering auto driver download for version:', version, 'with url:', url, 'status returned:', startDownloadResponse.status);
					}
				}
			}, function(err){
				logger.error('failed to trigger auto download for version:', version, 'with url:', url);
			});
		}
	}

	function autoDriverDownloadCheckCallback(){

		function getDriverUpdatesSuccess(data){
			logger.info('get driver updates success data:', data);
			if( Object.prototype.toString.call( data.updates ) === '[object Array]' ) {
				data.updates.forEach(function(driver){
					if(driver.isBeta){
						return;
					}

					if(downloaderAPI.getStatus){
						downloaderAPI.getStatus(driver.version, driver.downloadURL).then(function(statusResponse){
							logger.info('status for driver version:', driver.version, 'status:', statusResponse);
							if(statusResponse){
								if(statusResponse.status === DOWNLOAD_STATUS.UNDEFINED || statusResponse.status === DOWNLOAD_STATUS.PAUSED) {
									if(statusResponse.failureCount < 3) {
										logger.info('triggering auto driver download for version:', driver.version);
										startDriverDownload(driver.version, driver.downloadURL, driver.driverType);
									} else {
										logger.info('skipping auto driver download for version:', driver.version, 'and url:', driver.downloadURL, 'failureCount:', statusResponse.failureCount);
									}
								}
							}
						}, function(err){
							logger.error('Status does not exist for driver:', driver.version);
							startDriverDownload(driver.version, driver.downloadURL, driver.driverType);
						});
					}
				});
			}
		}

		function automaticDriverDownloadEnabledSuccess(data){
			logger.info('data received from audriver download enabled api:', data);
			if(data != null && data != undefined){
				if(data && NvBackendAPI.getDriverUpdates){
					NvBackendAPI.getDriverUpdates().then(getDriverUpdatesSuccess, function(err){
						logger.error('Error while checking for updates');
					});
				}
			}
		}

		logger.info('auto driver download check begins');
		if(NvBackendAPI.getAutomaticDriverDownloadEnabled) {
			NvBackendAPI.getAutomaticDriverDownloadEnabled().then(automaticDriverDownloadEnabledSuccess, function(err){
				logger.error('Error while checking for automatic driver enabled');
			});
		}
	}

	if(NvCommonTasks.setIntervalCallback){
		NvCommonTasks.setIntervalCallback(autoDriverDownloadCheckCallback, true);
		NvBackendAPI.setAutoDriverDownloadCallback(autoDriverDownloadCheckCallback);
	}

	return {
		version: function () {
			return NvAutoDriverDownloadVersion;
		}
	};
}