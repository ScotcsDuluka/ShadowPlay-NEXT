// Copyright (c) 2016, NVIDIA CORPORATION.  All rights reserved.
//
// NVIDIA CORPORATION and its licensors retain all intellectual property
// and proprietary rights in and to this software, related documentation
// and any modifications thereto.  Any use, reproduction, disclosure or
// distribution of this software and related documentation without an express
// license agreement from NVIDIA CORPORATION is strictly prohibited.

'use strict'

module.exports = function () {
    //! Function that receives body of POST/PUT request and calls callback for that data.
    //! @param req      Request object provided by Express.
    //! @param callback Callback that is triggered on successfully downloaded data.
    function _getRequestDataAndDo(req, callback) {
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

    //! Function that receives JSON body of POST/PUT request and calls callback for the parsed data.
    //! @param req      Request object provided by Express.
    //! @param res      Response object provided by Express.
    //! @param callback Callback that is triggered on successfully parsed data.
    function _getJSONRequestDataAndDo(req, res, callback) {
        function parseAndCallback(content) {
            var parsed = {};
            try {
                parsed = JSON.parse(content);
            }
            catch (e) {
                console.log("Exception while parsing json data for url: " + req.url);
                res.writeHead(400, { 'Content-Type': 'text/html;charset=utf-8' });
                res.end(e.name + ': ' + e.message);
                return;
            }
            callback(parsed);
        }
        _getRequestDataAndDo(req, parseAndCallback);
    }

    return {
        getJSONRequestDataAndDo: _getJSONRequestDataAndDo,
        getRequestDataAndDo: _getRequestDataAndDo
    };
};