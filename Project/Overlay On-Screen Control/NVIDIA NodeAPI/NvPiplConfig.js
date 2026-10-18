/* Copyright (c) 2023, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict';

const https = require('https');
const fs = require('fs');

const config = require('./config.json');

let clientVersion = 'undefined';
let clientParams = '';
let configCacheFile;
let configRefreshInterval;

let logger;
// callbacks to be called each time there is a config update.
let nvAccountCallbackFunc;
let nvBackendCallbackFunc;

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
    logger.error(errorString);
    res.end(errorString);
}

function setNvAccountCallbackInternal(callback) {
    nvAccountCallbackFunc = callback;
}

function setNvBackendCallbackInternal(callback) {
    nvBackendCallbackFunc = callback;
}

function fileExists(path) {
    try {
        const st = fs.statSync(path);
        return st.isFile();
    }
    catch (err) {
        return false;
    }
}

function extractCvDataFromResponse(gxtResponse, cvName) {
    if (!gxtResponse.cloudVariables || gxtResponse.cloudVariables.length === 0) {
        throw 'Missing CV List'
    }
    const data = gxtResponse.cloudVariables.find((val) => val.name === cvName);
    if (!data || !data.value) {
        throw 'CV not found'
    }
    return data.value;
}

function getCachedConfigData() {
    let returnValue = null;
    if (fileExists(configCacheFile)) {
        try {
            returnValue = JSON.parse(fs.readFileSync(configCacheFile, 'utf8'));
        } catch (err) {
            logger.error('Could not read config cache');
        }
    }
    return returnValue;
}

function isCacheValid(piplFileStruct) {
    if (!piplFileStruct || !piplFileStruct.data || !piplFileStruct.expiryTime) {
        return false;
    }
    return piplFileStruct.expiryTime > new Date().getTime();
}

function setCachedConfigData(cvData) {
    try {
        const daysToBeAdded = cvData.daysToExpire || 1;
        const expiryDate = new Date();
        const finalExpiryTime = expiryDate.setDate(expiryDate.getDate() + daysToBeAdded);
        const piplFileStruct = {
            data: cvData,
            expiryTime: finalExpiryTime
        };
        fs.writeFileSync(configCacheFile, JSON.stringify(piplFileStruct));
    } catch (err) {
        logger.error('Unable to write pipl config to file:', err);
    }
}

function getCvData() {
    let responseData = '';
    const options = {
        hostname: config.gxtarget.server,
        path: `/${config.gxtarget.cvEndpoint}/${config.gxtarget.version}?` + clientParams,
        method: 'GET',
        headers: {
            'accept': 'application/json'
        },
        timeout: config.gxtarget.defaultTimeout
    };

    return new Promise((resolve, reject) => {
        const req = https.request(options, (res) => {
            res.setEncoding('utf8');
            res.on('data', (chunk) => {
                responseData += chunk;
            });
            res.on('end', () => {
                try {
                    const parsedData = JSON.parse(responseData);
                    const cvData = extractCvDataFromResponse(parsedData, config.gxtarget.piplConfigCvName);
                    resolve(cvData);
                } catch (err) {
                    logger.error('PiplConfig end error:', err);
                    reject(err);
                }
            })
        });

        req.on('error', (err) => {
            logger.error('PiplConfig request error:', err);
            reject(err);
        });

        req.end();
    });
}

module.exports = function (app, io, _logger, gfeVersion, nvNodeProgramDataPath) {
    logger = _logger;
    if (gfeVersion) {
        clientVersion = gfeVersion;
    }
    if (nvNodeProgramDataPath) {
        configCacheFile = nvNodeProgramDataPath + '\\piplConfig.json';
    }

    clientParams = new URLSearchParams({
        cvName: config.gxtarget.piplConfigCvName,
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
    }).toString();

    function propogateConfigUpdate(cvData, previousCvCache) {
        try {
            if (JSON.stringify(previousCvCache) !== JSON.stringify(cvData)) {
                setImmediate(() => io.emit('/PiplConfig/v.1.0/update', cvData));
                nvAccountCallbackFunc(cvData);
                nvBackendCallbackFunc(cvData);
                logger.debug('Updated PiplConfig propogated successfully');
            }
        } catch (err) {
            logger.err('Error propogating config:', err);
        }
    }

    function getConfigInternal() {
        let previousCvCache;
        const piplFileStruct = getCachedConfigData();
        if (piplFileStruct && piplFileStruct.data) {
            previousCvCache = piplFileStruct.data;
        }
        if (isCacheValid(piplFileStruct)) {
            return Promise.resolve(piplFileStruct.data);
        } else {
            return getCvData().then(cvData => {
                setCachedConfigData(cvData);
                propogateConfigUpdate(cvData, previousCvCache);
                return cvData;
            })
        }
    }

    app.get('/PiplConfig/v.1.0/data', (req, res) => {
        logger.info('Get PiplConfig');
        getConfigInternal()
            .then(piplConfig => {
                res.writeHead(200);
                res.end(JSON.stringify(piplConfig));
            })
            .catch(err => {
                logger.error('PiplConfig error:', err);
                const staleCache = getCachedConfigData();
                if (staleCache && staleCache.data) {
                    logger.info('Stale cache found, using it for reply');
                    res.writeHead(200);
                    res.end(JSON.stringify(staleCache.data));
                } else {
                    replyWithError(res, err);
                }
            });
    });

    function initializeInternal() {
        clearInterval(configRefreshInterval);
        // 5 minute interval
        configRefreshInterval = setInterval(
            () => getConfigInternal().catch(err => logger.error('PiplConfig interval fetch error:', err)),
            1000 * 60 * 5
        );
        logger.info('NvPiplConfig initialized');
        return getConfigInternal().then(configData => propogateConfigUpdate(configData, null)).catch(err => null);
    }

    function cleanupInternal() {
        clearInterval(configRefreshInterval);
        logger.info('Cleaning up PiplConfig');
    }

    return {
        initialize: initializeInternal,
        setNvBackendCallback: setNvBackendCallbackInternal,
        setNvAccountCallback: setNvAccountCallbackInternal,
        getConfig: getConfigInternal,
        cleanup: cleanupInternal
    };
};
