/* Copyright (c) 2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

//keeping this as seperate file since same timer will be required for selfupdate
module.exports = function() {
	
	var NvCommonTasksVersion = '1.0.0';
	var intervalCallbacks = [],
		autoIntervalTime = 24 * 60 * 60 * 1000;//24 hour
		
	setInterval(function(){
		intervalCallbacks.forEach(function(callback){
			callback();
		});
	}, autoIntervalTime);
	
	
	function setIntervalCallback(callBackFunction, callOnceImmediately){
		if(callBackFunction){
			intervalCallbacks.push(callBackFunction);
		}
		
		if(callOnceImmediately){
			callBackFunction();
		}
	}
	
	function removeIntervalCallback(callBackFunction){
		var functionIndex = intervalCallbacks.indexOf(callBackFunction);
		if(functionIndex !== -1){
			intervalCallbacks.splice(functionIndex, 1);
		}
	}
	
	return {
		version: function () {
			return NvCommonTasksVersion;
		},
        setIntervalCallback: setIntervalCallback,
		removeIntervalCallback: removeIntervalCallback
    };
}