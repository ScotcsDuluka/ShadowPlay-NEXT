'use strict';
angular.module('main.config', [])
    .constant('OSC_CONFIG', {
        "userAgent": "NVIDIAOSCClient",
        "windowName": "shareclient",
        "hangouts": false,
        "nvCamera": true,
        "anselLite": true,
        "nvCameraExperimental": false,
        "mods": true,
        "osd": true,
        "perfmonOCTool": true,
        "autoUploadMs": 0,
        "jarvisEnabled": false,
        "jarvisLinkedContent": false,
        "jsEvents": {
            "server": "",
            "version": "v1.0",
            "schemaVersion": "2.7",
            "maxRetries": 2,
            "msBetweenRetries": 1000,
            "defaultTimeout": 30000,
            "msBetweenSendRequest": 5000,
            "maxEventsPerRequest": 128,
            "oscClientId": "11615747663535760"
        },
        "jarvis": {
            "server": "",
            "version": "1",
            "deviceId": "oscclient",
            "clientId": "144326972728672375",
            "gfeClientId": "135333107684344109",
            "clientDescription": "OSC {VERSION}",
            "redirectUrl": "https://rds-assets.nvidia.com/main/redirect/share-redirect.html#",
            "redirectUrlJarvisOauth": "https://rds-assets.nvidia.com/main/redirect/share-redirect.html",
            "defaultTimeout": 10000,
            "defaultRetries": 2,
            "defaultTimeBetweenRetries": 500,
            "blockKeys": {
                "upload": "linkedContent"
            }
        },
        "connect": {
            "redirectUri": "https://rds-assets.nvidia.com/main/redirect/share-redirect.html",
            "twitch": {
                "clientId": "cxcbwgocuez8axmeqe0cmz29ivvxjt2",
                "config": {
                    "title": "l10n.twitch",
                    "icon": "icon-logo_glyph_twitch",
                    "tooltip": "l10n.twitch",
                    "hint": "l10n.hintTwitch",
                    "qualityPrefix": "Gamecast",
                    "portalIdentifier": "Twitch",
                    "privacyOptions": [{
                        "level": 0,
                        "id": "Public",
                        "name": "l10n.public",
                        "value": "Public",
                        "default": true
                    }],
                    "maxTitleChars": 140,
                    "bitrateMax": 9,
                    "bitrateMax2K": 9,
                    "supported_settings": {
                        "30": ["360p", "480p", "720p HD", "1080p HD"],
                        "60": ["360p", "480p", "720p HD", "1080p HD"]
                    },
                    "destinationOptions": [{
                        "param": "privacy",
                        "name": "l10n.channel"
                    }]
                }
            },
            "imgur": {
                "clientId": "af3ff87d603599e",
                "config": {
                    "type": 2,
                    "title": "l10n.imgur",
                    "icon": "icon-logo_glyph_imgur",
                    "tooltip": "l10n.tooltipUploadScreenshotsToImgur",
                    "hint": "l10n.hintImgur",
                    "privacyOptions": [{
                        "level": 0,
                        "id": "Private",
                        "name": "l10n.private",
                        "value": "private"
                    }],
                    "destinationOptions": [{
                        "param": "privacy",
                        "name": "l10n.images"
                    }],
                    "gifOptions": {
                        "maxFileSizeMB": 200,
                        "maxDuration": 15,
                        "fpsLevel": "high",
                        "memeHeight": 720
                    }
                }
            },
            "google": {
                "clientId": "954449761280-u6t6u90f9okkk4buhesve4gfn4lrc5u4.apps.googleusercontent.com",
                "useOauthProxy": true,
                "embeddedRestricted": true,
                "redirectUriOverride": "http://localhost:{{portNumber}}",
                "config": {
                    "youtube": {
                        "title": "l10n.youTube",
                        "icon": "icon-logo_glyph_youtube",
                        "tooltip": "l10n.tooltipUploadVideosToYouTube",
                        "hint": "l10n.hintYoutube",
                        "qualityPrefix": "GamecastYtl",
                        "portalIdentifier": "YTL",
                        "privacyOptions": [{
                            "level": 0,
                            "id": "Public",
                            "name": "l10n.public",
                            "value": "public",
                            "default": true
                        }, {
                            "level": 1,
                            "id": "Unlisted",
                            "name": "l10n.unlisted",
                            "value": "unlisted"
                        }, {
                            "level": 2,
                            "id": "Private",
                            "name": "l10n.private",
                            "value": "private"
                        }],
                        "maxTitleChars": 100,
                        "bitrateMax": 9,
                        "bitrateMax2K": 18,
                        "supported_settings": {
                            "30": ["360p", "480p", "720p HD", "1080p HD", "1440p HD"],
                            "60": ["720p HD", "1080p HD", "1440p HD"]
                        },
                        "destinationOptions": [{
                            "param": "privacy",
                            "name": "l10n.channel"
                        }]
                    },
                    "photos": {
                        "icon": "icon-logo_glyph_google",
                        "tooltip": "l10n.tooltipUploadScreenshotsToGooglePhotos",
                        "hint": "l10n.hintGooglePhotos",
                        "privacyOptions": [{
                            "level": 0,
                            "id": "Private",
                            "name": "l10n.private",
                            "value": "private"
                        }],
                        "destinationOptions": [{
                            "param": "privacy",
                            "name": "l10n.photos"
                        }],
                        "gifOptions": {
                            "maxFileSizeMB": 50,
                            "maxDuration": 15,
                            "fpsLevel": "high",
                            "memeHeight": 540
                        }
                    }
                }
            },
            "facebook": {
                "clientId": "1679326302390196",
                "useOauthProxy": true,
                "config": {
                    "title": "l10n.facebook",
                    "icon": "icon-logo_glyph_facebook",
                    "tooltip": "l10n.facebook",
                    "hint": "l10n.hintFacebook",
                    "qualityPrefix": "GamecastFbl",
                    "portalIdentifier": "FbLive",
                    "broadcastTimeout": true,
                    "max_duration": 240,
                    "privacyOptions": [{
                        "level": 0,
                        "id": "Public",
                        "name": "l10n.public",
                        "value": "EVERYONE"
                    }, {
                        "level": 1,
                        "id": "Friends",
                        "name": "l10n.friends",
                        "value": "ALL_FRIENDS",
                        "default": true
                    }, {
                        "level": 2,
                        "id": "OnlyMe",
                        "name": "l10n.onlyMe",
                        "value": "SELF"
                    }],
                    "bitrateMax": 4,
                    "bitrateMax2K": 4,
                    "supported_settings": {
                        "30": ["360p", "480p", "720p HD"]
                    },
                    "destinationOptions": [{
                        "param": "privacy",
                        "name": "l10n.timeline"
                    }, {
                        "param": "page",
                        "name": "l10n.managedPage"
                    }, {
                        "param": "group",
                        "name": "l10n.groupMember"
                    }],
                    "commentsAndReactionsInterval": 1000,
                    "maxNumberOfComments": 25,
                    "gifOptions": {
                        "shouldUploadGifAsVideo": true,
                        "maxDuration": 15,
                        "maxFileSizeMB": 0,
                        "maxHeight": 720,
                        "memeHeight": 720,
                        "fpsLevel": "high"
                    }
                }
            },
            "weibo": {
                "clientId": "3774042265",
                "useOauthProxy": true,
                "config": {
                    "title": "l10n.weibo",
                    "icon": "icon-logo_glyph_weibo",
                    "tooltip": "l10n.tooltipUploadScreenshotsToWeibo",
                    "hint": "l10n.hintWeibo",
                    "privacyOptions": [{
                        "level": 0,
                        "id": "Everyone",
                        "name": "l10n.public",
                        "value": 0,
                        "scope": 22
                    }, {
                        "level": 1,
                        "id": "OnlyMe",
                        "name": "l10n.onlyMe",
                        "value": 1,
                        "scope": 4
                    }],
                    "destinationOptions": [{
                        "param": "privacy",
                        "name": "l10n.myMicroBlog"
                    }],
                    "maxTitleChars": 140,
                    "maxImageSize": 5,
                    "gifOptions": {
                        "maxFileSizeMB": 5,
                        "maxDuration": 10,
                        "fpsLevel": "medium",
                        "memeHeight": 272
                    }
                }
            },
            "swgf": {
                "clientId": "163900107807260888",
                "baseUrl": "https://api-prod.nvidia.com",
                "config": {
                    "title": "l10n.shotWithGeForce",
                    "icon": "icon-logo_glyph_shotwithgeforce",
                    "tooltip": "l10n.tooltipUploadScreenshotsToShotWithGeForce",
                    "hint": "",
                    "privacyOptions": [{
                        "level": 0,
                        "id": "Public",
                        "name": "l10n.public",
                        "value": "public",
                        "default": true
                    }],
                    "destinationOptions": [{
                        "param": "privacy",
                        "name": "l10n.images"
                    }]
                }
            }
        },
        "gfwsl": {
            "server": "",
            "defaultTimeout": 10000
        },
        "redirect": {
            "server": ""
        },
        "pipl": {
            "server": "PiplConfig",
            "version": "v.1.0",
            "defaultTimeout": 30000
        }
    })
    .constant('OSC_BUILD_INFO', {
        "oscPackageVersion": "3.28.0.412",
        "branch": "rel_03_28",
        "branchType": "rel",
        "oscClientVersion": "0.0.1",
        "gitHash": "ffe15e48d9",
        "buildType": "prod"
    });