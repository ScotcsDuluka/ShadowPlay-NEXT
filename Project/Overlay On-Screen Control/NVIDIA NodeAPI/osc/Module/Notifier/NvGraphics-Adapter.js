/* NvGraphics GPU preference and applied-GPU notifications. */
(function (window) {
    'use strict';

    if (!window.angular || !window.Notifier) {
        console.error('[gpu-notifier] OSC or notifier runtime did not load');
        return;
    }

    var PREF_EVENT = '/Duluka/v1/GpuPreferenceApplied';
    var GPU_ALERTS_STORAGE_KEY = 'nv-gpu-notifications-enabled';
    var NVGRAPHICS_RUNNING_PROFILES_URL = '/Duluka/v1/gpu/running-profile-paths';
    var NVGRAPHICS_PROFILE_POLL_MS = 1000;
    var GPU_CARD_OPEN_SHARE_GRACE_MS = 3500;
    var GPU_ASSIGNMENT_RECHECK_MS = 3000;
    var EXE_ICON_FALLBACK = '\uE94F';

    angular.module('main').run([
        '$filter', '$log', 'socketService', 'eventAggregator',
        function ($filter, $log, socketService, eventAggregator) {
            var handledEventIds = [];
            var runningNvGraphicsProfiles = Object.create(null);
            var openShareCardsByPath = Object.create(null);
            var gpuCardChecksInFlight = Object.create(null);
            var nvGraphicsProfilePollInFlight = false;
            var managerStateRequest = null;
            var managerStateRequestUtc = 0;

            function getManagerState() {
                if (managerStateRequest && Date.now() - managerStateRequestUtc < 2000)
                    return managerStateRequest;

                managerStateRequestUtc = Date.now();
                managerStateRequest = fetch('/Duluka/v1/gpu/manager').then(function (response) {
                    if (!response.ok)
                        throw new Error('GPU manager returned HTTP ' + response.status);
                    return response.json();
                }).catch(function (error) {
                    managerStateRequest = null;
                    managerStateRequestUtc = 0;
                    throw error;
                });
                return managerStateRequest;
            }

            function gpuAlertsEnabled() {
                try { return window.localStorage.getItem(GPU_ALERTS_STORAGE_KEY) !== 'false'; }
                catch (error) {
                    $log.warn('[gpu-notifier] notification preference unavailable', error);
                    return true;
                }
            }

            function dedupe(event) {
                if (handledEventIds.indexOf(event.eventId) !== -1) return true;
                handledEventIds.push(event.eventId);
                if (handledEventIds.length > 32) handledEventIds.shift();
                return false;
            }

            function isValidPreferenceEvent(event) {
                if (!event || event.schemaVersion !== 1) return false;
                if (typeof event.eventId !== 'string' ||
                    event.eventId.trim().length === 0 || event.eventId.length > 64) return false;
                if (typeof event.exePath !== 'string' ||
                    event.exePath.trim().length < 3 || event.exePath.length > 512) return false;
                var hasDriveFilePath = /^[A-Za-z]:[\\/](?:[^\\/]+[\\/])*[^\\/]+$/.test(event.exePath);
                var hasUncFilePath = /^(?:\\\\|\/\/)[^\\/]+[\\/][^\\/]+(?:[\\/][^\\/]+)+$/.test(event.exePath);
                if (!hasDriveFilePath && !hasUncFilePath)
                    return false;
                if (typeof event.adapterId !== 'string' || event.adapterId.length > 128) return false;
                if (typeof event.isSystemDefault !== 'boolean') return false;
                if (!event.isSystemDefault && event.adapterId.trim().length === 0) return false;
                if (event.adapterName != null &&
                    (typeof event.adapterName !== 'string' || event.adapterName.length > 256)) return false;
                if (event.displayName != null &&
                    (typeof event.displayName !== 'string' || event.displayName.length > 128)) return false;
                if (typeof event.appliedUtc !== 'string') return false;
                var applied = Date.parse(event.appliedUtc);
                if (!isFinite(applied)) return false;
                var age = Date.now() - applied;
                return age <= 2 * 60 * 1000 && age >= -2 * 60 * 1000;
            }

            function normalizePath(path) {
                return String(path || '').replace(/\//g, '\\').toLowerCase();
            }

            function executableName(path) {
                var parts = String(path || '').split(/[\\/]/);
                return parts[parts.length - 1] || path || 'Game.exe';
            }

            function displayGpuName(name) {
                return String(name || '').replace(/\s*\[GPU\s*\d+\]\s*/gi, ' ').trim();
            }

            function normalizeAdapterId(adapterId) {
                return String(adapterId || '')
                    .replace(/(?:VEN_|DEV_|SUBSYS_)/gi, '')
                    .replace(/[^a-z0-9]/gi, '')
                    .toUpperCase();
            }

            function getExecutableGpuAssignment(exePath) {
                var pathKey = normalizePath(exePath);
                return getManagerState().then(function (state) {
                    var profiles = Array.isArray(state.profiles) ? state.profiles : [];
                    var app = profiles.find(function (item) {
                        return item && normalizePath(item.exePath) === pathKey;
                    });
                    var adapterId = app && app.lastAppliedAdapterId;
                    if (typeof adapterId !== 'string' || !adapterId.trim())
                        return { applied: false, gpuName: '' };

                    var adapters = Array.isArray(state.adapters) ? state.adapters : [];
                    var adapterKey = normalizeAdapterId(adapterId);
                    var adapter = adapters.find(function (item) {
                        return item && normalizeAdapterId(item.adapterId) === adapterKey;
                    });
                    if (!adapter)
                        return { applied: true, gpuName: '' };
                    return {
                        applied: true,
                        gpuName: displayGpuName(adapter.name)
                    };
                });
            }

            function getExecutableIcon(exePath) {
                var pathKey = normalizePath(exePath);
                return getManagerState().then(function (state) {
                    var apps = (Array.isArray(state.profiles) ? state.profiles : [])
                        .concat(Array.isArray(state.windowsApps) ? state.windowsApps : []);
                    var app = apps.find(function (item) {
                        return item && normalizePath(item.exePath) === pathKey;
                    });
                    return app && typeof app.iconDataUri === 'string' && app.iconDataUri
                        ? app.iconDataUri
                        : '';
                }).catch(function (error) {
                    $log.warn('[gpu-notifier] could not load GPU manager icons', error);
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

            function onOpenShareGameCardStarted(event) {
                if (!event || !event.game ||
                    typeof event.game.executablePath !== 'string' ||
                    typeof event.instanceKey !== 'string' ||
                    typeof event.game.processId !== 'number')
                    return;
                openShareCardsByPath[normalizePath(event.game.executablePath)] = event.instanceKey;
            }

            function onOpenShareGameCardFinished(event) {
                if (!event || !event.game ||
                    typeof event.game.executablePath !== 'string' ||
                    typeof event.instanceKey !== 'string')
                    return;
                var pathKey = normalizePath(event.game.executablePath);
                if (openShareCardsByPath[pathKey] === event.instanceKey)
                    delete openShareCardsByPath[pathKey];
                if (runningNvGraphicsProfiles[pathKey])
                    runningNvGraphicsProfiles[pathKey].lastAssignmentCheckUtc = 0;
                showPendingGpuCards();
            }

            function pollNvGraphicsRunningProfiles() {
                if (nvGraphicsProfilePollInFlight) return;
                nvGraphicsProfilePollInFlight = true;
                fetch(NVGRAPHICS_RUNNING_PROFILES_URL).then(function (response) {
                    if (!response.ok)
                        throw new Error('NvGraphics running profiles returned HTTP ' + response.status);
                    return response.json();
                }).then(function (state) {
                    if (!state || !Array.isArray(state.paths))
                        throw new Error('NvGraphics running profiles response is invalid');

                    var currentProfiles = Object.create(null);
                    state.paths.forEach(function (path) {
                        if (typeof path !== 'string' || path.length < 3) return;
                        var pathKey = normalizePath(path);
                        currentProfiles[pathKey] = runningNvGraphicsProfiles[pathKey] || {
                            executablePath: path,
                            firstSeenUtc: Date.now(),
                            lastAssignmentCheckUtc: 0,
                            gpuCardShown: false
                        };
                    });

                    runningNvGraphicsProfiles = currentProfiles;
                    Object.keys(openShareCardsByPath).forEach(function (pathKey) {
                        if (!currentProfiles[pathKey]) {
                            delete openShareCardsByPath[pathKey];
                        }
                    });
                    showPendingGpuCards();
                }).catch(function (error) {
                    $log.warn('[gpu-notifier] could not read NvGraphics running games', error);
                }).then(function () {
                    nvGraphicsProfilePollInFlight = false;
                });
            }

            function showPendingGpuCards() {
                if (!gpuAlertsEnabled()) return;
                Object.keys(runningNvGraphicsProfiles).forEach(function (pathKey) {
                    var runningGame = runningNvGraphicsProfiles[pathKey];
                    var now = Date.now();
                    if (!runningGame || runningGame.gpuCardShown ||
                        openShareCardsByPath[pathKey] ||
                        now - runningGame.firstSeenUtc < GPU_CARD_OPEN_SHARE_GRACE_MS ||
                        now - runningGame.lastAssignmentCheckUtc < GPU_ASSIGNMENT_RECHECK_MS ||
                        gpuCardChecksInFlight[pathKey]) return;
                    runningGame.lastAssignmentCheckUtc = now;
                    gpuCardChecksInFlight[pathKey] = true;
                    getExecutableGpuAssignment(runningGame.executablePath).then(function (assignment) {
                        if (runningNvGraphicsProfiles[pathKey] !== runningGame ||
                            !runningNvGraphicsProfiles[pathKey] ||
                            openShareCardsByPath[pathKey] ||
                            !gpuAlertsEnabled() ||
                            !assignment.applied || !assignment.gpuName)
                            return;
                        return getExecutableIcon(runningGame.executablePath).then(function (iconPath) {
                            if (runningNvGraphicsProfiles[pathKey] !== runningGame ||
                                !runningNvGraphicsProfiles[pathKey] ||
                                openShareCardsByPath[pathKey] ||
                                !gpuAlertsEnabled())
                                return;
                            if (!iconPath)
                                $log.warn('[gpu-notifier] EXE icon unavailable for ' +
                                    runningGame.executablePath);
                            var game = { executablePath: runningGame.executablePath };
                            var instanceKey = 'nvgraphics:' + pathKey + ':' + runningGame.firstSeenUtc;
                            var card = showGameRunningOnGpuCard(game, instanceKey, iconPath,
                                assignment.gpuName, false);
                            var isVisible = !!card || window.Notifier.hasActiveGroupPrefix(
                                'gpu-game-running:' + instanceKey);
                            if (!isVisible) {
                                $log.warn('[gpu-notifier] no notifier slot available for ' +
                                    executableName(runningGame.executablePath));
                                return;
                            }
                            runningGame.gpuCardShown = true;
                            $log.info('[gpu-notifier] applied-GPU card shown for ' +
                                executableName(runningGame.executablePath));
                        });
                    }).catch(function (error) {
                        $log.warn('[gpu-notifier] could not prepare the running-GPU card', error);
                    }).then(function () {
                        delete gpuCardChecksInFlight[pathKey];
                    });
                });
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

            function showGameRunningOnGpuCard(game, instanceKey, iconPath, gpuName, updaterUi, onClosed) {
                var exeName = executableName(game.executablePath);
                var message = $filter('translate')('l10n.gpuAppRunningOnGpu', {
                    arg1: exeName
                });
                if (message === 'l10n.gpuAppRunningOnGpu')
                    message = exeName + ' is running on GPU';
                return window.Notifier.show(message, {
                    key: 'gpu-game-running:' + instanceKey,
                    iconPath: iconPath || '',
                    fallbackIcon: EXE_ICON_FALLBACK,
                    group: 'gpu-game-running:' + instanceKey,
                    width: gpuCardWidth(message, gpuName),
                    subtext: gpuName,
                    priority: true,
                    updaterUi: !!updaterUi,
                    onClosed: onClosed
                });
            }

            function showPreferenceToast(event, exeName, iconPath) {
                var adapterName = displayGpuName(event.adapterName || event.adapterId || '');
                var message = event.isSystemDefault
                    ? $filter('translate')('l10n.gpuPreferenceDefault', { arg1: exeName })
                    : $filter('translate')('l10n.gpuPreferenceApplied', {
                        arg1: exeName,
                        arg2: 'GPU'
                    });
                if (message === 'l10n.gpuPreferenceDefault')
                    message = 'Set ' + exeName + ' to use system default';
                if (message === 'l10n.gpuPreferenceApplied')
                    message = 'Set ' + exeName + ' to use GPU';
                var subtext = event.isSystemDefault
                    ? $filter('translate')('l10n.gpuSystemDefaultUnavailable')
                    : adapterName;
                if (event.isSystemDefault && subtext === 'l10n.gpuSystemDefaultUnavailable')
                    subtext = 'GPU name for System default is unavailable';
                window.Notifier.show(message, {
                    key: 'gpu-pref:' + normalizePath(event.exePath || exeName),
                    iconPath: iconPath || '',
                    fallbackIcon: EXE_ICON_FALLBACK,
                    group: 'gpu-pref:' + normalizePath(event.exePath || exeName),
                    subtext: subtext,
                    width: gpuCardWidth(message, subtext),
                    priority: true,
                    updaterUi: true
                });
                $log.info('[gpu-notifier] preference updated: ' + exeName + ' → ' +
                    (event.isSystemDefault ? 'System default' : event.adapterName || event.adapterId));
            }

            function onPreference(event) {
                if (!isValidPreferenceEvent(event)) {
                    $log.warn('[gpu-notifier] rejected malformed or stale preference event');
                    return;
                }
                if (!gpuAlertsEnabled()) return;
                if (dedupe(event)) return;
                var exeName = executableName(event.exePath);
                getExecutableIcon(event.exePath).then(function (iconPath) {
                    if (!iconPath)
                        $log.warn('[gpu-notifier] EXE icon unavailable for ' + event.exePath);
                    showPreferenceToast(event, exeName, iconPath);
                }).catch(function (error) {
                    $log.warn('[gpu-notifier] could not load EXE icon for GPU preference', error);
                    showPreferenceToast(event, exeName, '');
                });
            }

            function registerSocket() {
                socketService.register(PREF_EVENT, 'nv-gpu-pref');
            }

            eventAggregator.on('nv-gpu-pref', onPreference);
            eventAggregator.on('nv-openshare-game-card-started', onOpenShareGameCardStarted);
            eventAggregator.on('nv-openshare-game-card-closed', onOpenShareGameCardFinished);
            eventAggregator.on('nv-openshare-game-card-skipped', onOpenShareGameCardFinished);
            eventAggregator.on('connect', registerSocket);
            registerSocket();
            pollNvGraphicsRunningProfiles();
            window.setInterval(pollNvGraphicsRunningProfiles, NVGRAPHICS_PROFILE_POLL_MS);
        }
    ]);
})(window);
