(function (window) {
    'use strict';

    var GAME_WATCHER_EVENT = '/Duluka/v1/GameWatcherStatus';
    var OPEN_SHARE_ACTIVE_GAMES_STORAGE_KEY = 'nv-open-share-notified-game-instances-v5';
    var EXE_ICON_FALLBACK = '\uE94F';
    var NVIDIA_ICON_FALLBACK = '/mods/notifier/NVIDIA.ico';
    var OPEN_SHARE_PRESENTATION_TIMEOUT_MS = 1000;

    if (!window.angular || !window.Notifier) {
        console.error('[notifier] OSC or notifier runtime did not load');
        return;
    }

    angular.module('main').run([
        '$filter',
        '$log',
        'eventAggregator',
        'NOTIFICATION_EVENT',
        'NOTIFICATION_DATA',
        'socketService',
        function ($filter, $log, eventAggregator, notificationEvent, notificationData, socketService) {
            var observedGameInstances = loadObservedGameInstances();
            var pendingGameNotifications = Object.create(null);

            var obsNotifications = {
                RECORD_STARTED: true,
                RECORD_STOPPED: true,
                RECORD_PAUSED: true,
                RECORD_RESUMED: true,
                INSTANT_REPLAY_STARTED: true,
                INSTANT_REPLAY_STOPPED: true,
                INSTANT_REPLAY_SAVED: true,
                BROADCAST_STARTED: true,
                BROADCAST_STOPPED: true
            };
            var obsNotificationDefinitions = {
                RECORD_PAUSED: {
                    id: 'RECORD_PAUSED',
                    message: 'l10n.notificationManualRecordPaused',
                    icon: 'icon-record'
                },
                RECORD_RESUMED: {
                    id: 'RECORD_RESUMED',
                    message: 'l10n.notificationManualRecordResumed',
                    icon: 'icon-record icon-highlighted'
                }
            };

            function findNotification(selection) {
                if (obsNotificationDefinitions[selection]) return obsNotificationDefinitions[selection];
                for (var i = 0; i < notificationData.length; i++) {
                    if (notificationData[i].id === selection) return notificationData[i];
                }
                return null;
            }

            function localizeArgument(value) {
                return typeof value === 'string' && value.indexOf('l10n.') === 0
                    ? $filter('translate')(value)
                    : value;
            }

            function groupFor(selection) {
                if (/^(RECORD_|RECORDING_)/.test(selection)) return 'recording';
                if (/^(INSTANT_REPLAY_|REPLAY_)/.test(selection)) return 'replay';
                if (/^BROADCAST_/.test(selection)) return 'broadcast';
                if (/^SCREENSHOT_|^PHOTOGRAPHIC_SCREENSHOT_/.test(selection)) return 'screenshot';
                return selection.toLowerCase();
            }

            function normalizePath(path) {
                return String(path || '').replace(/\//g, '\\').toLowerCase();
            }

            function openShareToastGroup(game) {
                /* PID/matchedUtc distinguish runs for deduplication, but the
                   visible card belongs to the catalog gameId (not each process
                   or EXE path). Multiple detections of one game share one UI. */
                return 'gpu-game-openshare:' + game.gameId.toLowerCase();
            }

            function executableName(path) {
                var parts = String(path || '').split(/[\\/]/);
                return parts[parts.length - 1] || path || 'Game.exe';
            }

            function gpuCardWidth(message, subtext) {
                var canvas = document.createElement('canvas');
                var context = canvas.getContext('2d');
                if (!context) return 380;
                context.font = '14px "Segoe UI"';
                var subtextWidth = context.measureText(String(subtext || '')).width;
                context.font = '16px "Segoe UI"';
                var messageWidth = context.measureText(String(message || '')).width;
                var desiredWidth = Math.ceil(Math.max(subtextWidth, messageWidth) + 124);
                var viewportLimit = Math.max(280, window.innerWidth - 40);
                return Math.max(Math.min(380, viewportLimit),
                    Math.min(desiredWidth, viewportLimit));
            }

            function loadObservedGameInstances() {
                try {
                    var saved = JSON.parse(
                        window.localStorage.getItem(OPEN_SHARE_ACTIVE_GAMES_STORAGE_KEY) || '[]');
                    var observed = Object.create(null);
                    if (Array.isArray(saved)) {
                        saved.slice(-128).forEach(function (key) {
                            if (typeof key === 'string' && key.length > 0 && key.length <= 256)
                                observed[key] = true;
                        });
                    }
                    return observed;
                } catch (error) {
                    $log.warn('[notifier] saved OpenShare game state unavailable', error);
                    return Object.create(null);
                }
            }

            function saveObservedGameInstances(instances) {
                try {
                    window.localStorage.setItem(
                        OPEN_SHARE_ACTIVE_GAMES_STORAGE_KEY,
                        JSON.stringify(Object.keys(instances).slice(-128)));
                } catch (error) {
                    $log.warn('[notifier] could not save OpenShare game state', error);
                }
            }

            function isValidGameWatcherEvent(event) {
                if (!event || event.schemaVersion !== 1 ||
                    typeof event.eventId !== 'string' || !event.eventId.trim() ||
                    typeof event.publishedUtc !== 'string')
                    return false;

                var published = Date.parse(event.publishedUtc);
                var age = Date.now() - published;
                if (!isFinite(published) || age > 2 * 60 * 1000 || age < -2 * 60 * 1000)
                    return false;

                var status = event.status;
                if (!status || status.enabled !== true || status.running !== true ||
                    status.catalogReady !== true ||
                    status.catalogEmpty !== false ||
                    typeof status.catalogVersion !== 'string' || !status.catalogVersion ||
                    !Array.isArray(status.matchedGames) ||
                    status.matchedGames.length > 128 ||
                    status.matchedGameCount !== status.matchedGames.length)
                    return false;

                return status.matchedGames.every(function (game) {
                    var matched = game && typeof game.matchedUtc === 'string'
                        ? Date.parse(game.matchedUtc)
                        : NaN;
                    return game && typeof game === 'object' &&
                        typeof game.processId === 'number' && isFinite(game.processId) &&
                        Math.floor(game.processId) === game.processId && game.processId > 0 &&
                        typeof game.gameId === 'string' && game.gameId.trim().length > 0 &&
                        typeof game.displayName === 'string' && game.displayName.trim().length > 0 &&
                        typeof game.executablePath === 'string' && game.executablePath.length >= 3 &&
                        isFinite(matched) &&
                        typeof game.catalogVersion === 'string' &&
                        game.catalogVersion === status.catalogVersion;
                });
            }

            function getExecutableIcon(exePath) {
                var pathKey = normalizePath(exePath);
                return fetch('/Duluka/v1/gpu/manager').then(function (response) {
                    if (!response.ok)
                        throw new Error('GPU manager returned HTTP ' + response.status);
                    return response.json();
                }).then(function (state) {
                    var apps = (Array.isArray(state.profiles) ? state.profiles : [])
                        .concat(Array.isArray(state.windowsApps) ? state.windowsApps : []);
                    var app = apps.find(function (item) {
                        return item && normalizePath(item.exePath) === pathKey;
                    });
                    return app && typeof app.iconDataUri === 'string' && app.iconDataUri
                        ? app.iconDataUri
                        : '';
                }).catch(function (error) {
                    $log.warn('[notifier] could not load GPU manager icon', error);
                    return '';
                }).then(function (iconPath) {
                    if (iconPath) return iconPath;
                    return fetch('/Duluka/v1/gpu/running-games').then(function (response) {
                        if (!response.ok)
                            throw new Error('Running-games endpoint returned HTTP ' + response.status);
                        return response.json();
                    }).then(function (state) {
                        var games = Array.isArray(state.games) ? state.games : [];
                        var game = games.find(function (item) {
                            return item && normalizePath(item.exePath) === pathKey;
                        });
                        return game && typeof game.iconDataUri === 'string'
                            ? game.iconDataUri
                            : '';
                    });
                });
            }

            function isUsableIcon(iconPath) {
                return new Promise(function (resolve) {
                    if (typeof iconPath !== 'string' || !iconPath) {
                        resolve(false);
                        return;
                    }
                    var image = new Image();
                    image.onload = function () {
                        resolve(image.naturalWidth > 0 && image.naturalHeight > 0);
                    };
                    image.onerror = function () {
                        resolve(false);
                    };
                    image.src = iconPath;
                });
            }

            function getOpenShareIcon(iconPath) {
                return isUsableIcon(iconPath).then(function (usable) {
                    if (usable) return iconPath;
                    $log.info('[notifier] game EXE icon unavailable; using NVIDIA icon');
                    return isUsableIcon(NVIDIA_ICON_FALLBACK).then(function (fallbackUsable) {
                        return fallbackUsable ? NVIDIA_ICON_FALLBACK : '';
                    });
                });
            }

            function showOpenShareGameCard(game, instanceKey, iconPath, shortcut, updaterUi, onClosed, toastGroup) {
                var exeName = typeof game.displayName === 'string' && game.displayName.trim()
                    ? game.displayName.trim()
                    : executableName(game.executablePath).replace(/\.exe$/i, '');
                var subtext = $filter('translate')('l10n.notificationOpenShare', {
                    arg1: shortcut || 'Alt+Z'
                });
                if (subtext === 'l10n.notificationOpenShare')
                    subtext = 'Press ' + (shortcut || 'Alt+Z') +
                        ' to use in-game overlay';
                return window.Notifier.show(exeName, {
                    key: 'gpu-game-openshare:' + instanceKey,
                    iconPath: iconPath || '',
                    fallbackIcon: EXE_ICON_FALLBACK,
                    group: toastGroup || ('gpu-game-openshare:' + instanceKey),
                    width: gpuCardWidth(exeName, subtext),
                    subtext: subtext,
                    priority: true,
                    updaterUi: !!updaterUi,
                    onClosed: onClosed
                });
            }

            function onGameWatcherStatus(event) {
                if (!isValidGameWatcherEvent(event)) return;
                eventAggregator.trigger('nv-ontology-local-catalog-version', event.status.catalogVersion);

                var currentGameInstances = Object.create(null);
                event.status.matchedGames.forEach(function (game) {
                    var instanceKey = game.gameId.toLowerCase() + ':' +
                        game.processId + ':' + game.matchedUtc;
                    var toastGroup = openShareToastGroup(game);
                    currentGameInstances[instanceKey] = true;
                    /* The same status snapshot is not a new event: don't
                       restart the flip every GameWatcher polling interval.
                       A new run of the same game does use Updater UI. */
                    if (observedGameInstances[instanceKey] ||
                        pendingGameNotifications[toastGroup]) return;
                    pendingGameNotifications[toastGroup] = true;

                    function onClosed() {
                        eventAggregator.trigger('nv-openshare-game-card-closed', {
                            game: game,
                            instanceKey: instanceKey,
                            iconPath: iconPath
                        });
                    }

                    eventAggregator.trigger('nv-openshare-game-card-started', {
                        game: game,
                        instanceKey: instanceKey,
                        iconPath: ''
                    });
                    var iconPath = '';
                    var cardSubmitted = false;
                    var presentationTimer = window.setTimeout(function () {
                        submitOpenShareCard(NVIDIA_ICON_FALLBACK, 'Alt+Z');
                    }, OPEN_SHARE_PRESENTATION_TIMEOUT_MS);

                    function submitOpenShareCard(resolvedIconPath, shortcut) {
                        if (cardSubmitted) return;
                        cardSubmitted = true;
                        window.clearTimeout(presentationTimer);
                        iconPath = resolvedIconPath || '';
                        var cardData = {
                            game: game,
                            instanceKey: instanceKey,
                            iconPath: iconPath
                        };
                        try {
                            var alreadyVisible = window.Notifier.hasActiveGroupPrefix(
                                toastGroup);
                            var card = showOpenShareGameCard(game, instanceKey, iconPath,
                                shortcut, alreadyVisible, onClosed, toastGroup);
                            var isVisible = !!card || window.Notifier.hasActiveGroupPrefix(
                                toastGroup);
                            if (isVisible) {
                                observedGameInstances[instanceKey] = true;
                                saveObservedGameInstances(observedGameInstances);
                                $log.info('[notifier] OpenShare card shown for ' + game.displayName);
                            } else {
                                eventAggregator.trigger('nv-openshare-game-card-skipped', cardData);
                                $log.warn('[notifier] OpenShare card could not claim a notifier slot for ' +
                                    game.displayName);
                            }
                        } catch (error) {
                            eventAggregator.trigger('nv-openshare-game-card-skipped', cardData);
                            $log.warn('[notifier] OpenShare card failed', error);
                        }
                        delete pendingGameNotifications[toastGroup];
                    }

                    var iconPromise = getExecutableIcon(game.executablePath).catch(function (error) {
                        $log.warn('[notifier] could not load game EXE icon', error);
                        return '';
                    }).then(function (iconPath) {
                        return getOpenShareIcon(iconPath);
                    });
                    var shortcutPromise = window.Notifier.getOpenShareShortcut().catch(function (error) {
                        $log.warn('[notifier] could not load OpenShare shortcut; using Alt+Z', error);
                        return 'Alt+Z';
                    });
                    Promise.all([iconPromise, shortcutPromise]).then(function (presentation) {
                        submitOpenShareCard(presentation[0], presentation[1]);
                    }, function (error) {
                        $log.warn('[notifier] could not update OpenShare card', error);
                        submitOpenShareCard('', 'Alt+Z');
                    });
                });
                Object.keys(observedGameInstances).forEach(function (instanceKey) {
                    if (!currentGameInstances[instanceKey])
                        delete observedGameInstances[instanceKey];
                });
                saveObservedGameInstances(observedGameInstances);
            }

            // Manual replay uses the same card, icon resolution and shortcut
            // as game entry, without changing automatic-entry deduplication.
            window.__dulukaReplayGameEntry = function (game) {
                return Promise.all([
                    getExecutableIcon(game.executablePath).then(getOpenShareIcon).catch(function () { return NVIDIA_ICON_FALLBACK; }),
                    window.Notifier.getOpenShareShortcut().catch(function () { return 'Alt+Z'; })
                ]).then(function (presentation) {
                    return showOpenShareGameCard(game, 'manual-focus', presentation[0], presentation[1], false);
                });
            };

            function showNotification(event) {
                if (!event || typeof event.selection !== 'string') {
                    $log.warn('[notifier] Ignored malformed OSC notification', event);
                    return;
                }
                var item = findNotification(event.selection);
                if (!item) {
                    $log.warn('[notifier] No OSC notification definition for ' + event.selection);
                    return;
                }

                var arg1 = localizeArgument(event.arg1);
                var arg2 = localizeArgument(event.arg2);
                var args = { arg1: arg1, arg2: arg2 };
                var messageKey = event.selection === 'INSTANT_REPLAY_SAVED' && event.durationUnavailable === true
                    ? 'l10n.notificationInstantReplaySavedNoDuration'
                    : item.message;
                var message = $filter('translate')(messageKey, args);
                var subtext = item.messageSubtext
                    ? $filter('translate')(item.messageSubtext, args)
                    : '';
                window.Notifier.show(message, {
                    key: event.selection,
                    iconClass: item.icon || '',
                    iconPath: item.img || '',
                    positive: /icon-highlighted/.test(item.icon || ''),
                    group: groupFor(event.selection),
                    subtext: subtext
                });
            }

            function registerObsSocket() {
                socketService.register('/Duluka/v1/OBSNotification', 'nv-obs-notification');
                socketService.register(GAME_WATCHER_EVENT, 'nv-game-watcher');
                $log.info('[notifier] OBS toast socket subscribed');
            }

            var handledObsEvents = [];
            function showObsNotification(event) {
                if (!event || !obsNotifications[event.selection]) {
                    $log.warn('[notifier] Ignored malformed OBS notification', event);
                    return;
                }
                if (typeof event.eventId === 'string') {
                    if (handledObsEvents.indexOf(event.eventId) !== -1) return;
                    handledObsEvents.push(event.eventId);
                    if (handledObsEvents.length > 32) handledObsEvents.shift();
                }
                showNotification(event);
                $log.info('[notifier] OBS toast displayed: ' + event.selection);
            }

            fetch('/Notifier/v.1.0/settings')
                .then(function (response) {
                    if (!response.ok) throw new Error('Unable to load notifier settings (' + response.status + ')');
                    return response.json();
                })
                .then(function (settings) {
                    window.Notifier.setSlotCount(settings.slotCount);
                    $log.info('[notifier] configured slots: ' + settings.slotCount);
                })
                .catch(function (error) {
                    $log.error('[notifier] settings could not be loaded; using default slot count', error);
                    window.Notifier.setSlotCount(2);
                });
            window.Notifier.startHeartbeat();
            eventAggregator.on(notificationEvent, showNotification);
            registerObsSocket();
            eventAggregator.on('connect', registerObsSocket);
            eventAggregator.on('nv-obs-notification', showObsNotification);
            eventAggregator.on('nv-game-watcher', onGameWatcherStatus);
            window.NvNotifierBridge = {
                show: showNotification,
                status: function () { return window.Notifier.status(); }
            };
            $log.info('[notifier] New stacked notifier connected to OSC events');
        }
    ]);
})(window);
