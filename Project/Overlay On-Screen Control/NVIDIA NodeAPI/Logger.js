/* Copyright (c) 2016, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

module.exports = function (nvUtil, path) {
    if (nvUtil === undefined) {
        throw 'You need to provide NvUtil';
    }
    if (path === undefined) {
        throw 'You need to provide log path';
    }

    let hasLogger = false;
    try {
        nvUtil.CreateLogger(path);
        hasLogger = true;
    }
    catch (e) {
        console.log(e + ', logging into console only');
    }

    var util = require('util');
    return {
        error: function Error() {
            if (hasLogger) {
                nvUtil.LogError(util.format.apply(util, arguments));
            }
            console.error(util.format.apply(util, arguments));
        },
        debug: function Debug() {
            if (hasLogger) {
                nvUtil.LogDebug(util.format.apply(util, arguments));
            }
            console.log(util.format.apply(util, arguments));

        },
        info: function Info() {
            if (hasLogger) {
                nvUtil.LogInfo(util.format.apply(util, arguments));
            }
            console.info(util.format.apply(util, arguments));
        },
        infoSync: function InfoSync() {
            if (hasLogger) {
                nvUtil.LogInfoSync(util.format.apply(util, arguments));
            }
            console.info(util.format.apply(util, arguments));
        },
        destroyLogger: function DestroyLogger() {
            if (hasLogger) {
                nvUtil.DestroyLogger();
            }
        }
    };
}
