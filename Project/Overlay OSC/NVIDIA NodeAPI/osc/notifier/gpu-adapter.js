/* GPU preference notifications for apps in the saved profile list. */
(function (window) {
    'use strict';

    if (!window.angular || !window.Notifier) {
        console.error('[gpu-notifier] OSC or notifier runtime did not load');
        return;
    }

    var PREF_EVENT = '/Duluka/v1/GpuPreferenceApplied';
    var GPU_ALERTS_STORAGE_KEY = 'nv-gpu-notifications-enabled';
    var MANAGER_URL = '/Duluka/v1/gpu/manager';
    var RUNNING_URL = '/Duluka/v1/gpu/running-profile-paths';
    var RESTART_URL = '/Duluka/v1/gpu/restart';
    var EXE_ICON_FALLBACK = '\uE94F';

    angular.module('main').run([
        '$filter', '$log', 'socketService', 'eventAggregator',
        function ($filter, $log, socketService, eventAggregator) {
            var handledEventIds = [];
            var runningPaths = Object.create(null);
            var restartDialog = null;
            var initialized = false;
            var polling = false;

            function gpuAlertsEnabled() {
                try { return window.localStorage.getItem(GPU_ALERTS_STORAGE_KEY) !== 'false'; }
                catch (error) {
                    $log.warn('[gpu-notifier] notification preference unavailable', error);
                    return true;
                }
            }

            function dedupe(event) {
                if (typeof event.eventId !== 'string' || event.eventId.length === 0) return false;
                if (handledEventIds.indexOf(event.eventId) !== -1) return true;
                handledEventIds.push(event.eventId);
                if (handledEventIds.length > 32) handledEventIds.shift();
                return false;
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

            function windowsAdapterId(adapterId) {
                return String(adapterId || '').split('&').map(function (part) {
                    return part.replace(/^(VEN_|DEV_|SUBSYS_)/i, '');
                }).join('&').toLowerCase();
            }

            function requestJson(url) {
                return fetch(url).then(function (response) {
                    if (!response.ok) throw new Error('HTTP ' + response.status + ' from ' + url);
                    return response.json();
                });
            }

            function postJson(url, body) {
                return fetch(url, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body)
                }).then(function (response) {
                    return response.json().catch(function () { return {}; }).then(function (result) {
                        if (!response.ok)
                            throw new Error(result.error || 'HTTP ' + response.status + ' from ' + url);
                        return result;
                    });
                });
            }

            function translate(key, fallback, args) {
                var value = $filter('translate')(key, args);
                return value === key ? fallback : value;
            }

            function closeRestartDialog() {
                if (!restartDialog) return;
                restartDialog.remove();
                restartDialog = null;
                if (document.body.getAttribute('data-nv-route') === 'confirmation') {
                    if (restartDialogPreviousRoute === null)
                        document.body.removeAttribute('data-nv-route');
                    else
                        document.body.setAttribute('data-nv-route', restartDialogPreviousRoute);
                }
            }

            var restartDialogPreviousRoute = null;

            function showRestartConfirmation(exePath, exeName) {
                closeRestartDialog();
                restartDialogPreviousRoute = document.body.getAttribute('data-nv-route');
                document.body.setAttribute('data-nv-route', 'confirmation');

                var backdrop = document.createElement('div');
                backdrop.className = 'nv-gpu-restart-backdrop';
                backdrop.setAttribute('role', 'presentation');
                var card = document.createElement('section');
                card.className = 'nv-gpu-restart-card';
                card.setAttribute('role', 'dialog');
                card.setAttribute('aria-modal', 'true');
                card.setAttribute('aria-labelledby', 'nv-gpu-restart-title');
                var copy = document.createElement('div');
                copy.className = 'nv-gpu-restart-copy';
                var title = document.createElement('div');
                title.id = 'nv-gpu-restart-title';
                title.className = 'nv-gpu-restart-title';
                title.textContent = translate('l10n.gpuRestartTitle',
                    'Restart ' + exeName + ' to apply this graphics change?', { arg1: exeName });
                var detail = document.createElement('div');
                detail.className = 'nv-gpu-restart-detail';
                detail.textContent = translate('l10n.gpuRestartDetail',
                    'Save your progress first. The game will relaunch from its executable with its original arguments, not through its original launcher.');
                var status = document.createElement('div');
                status.className = 'nv-gpu-restart-status';
                var actions = document.createElement('div');
                actions.className = 'nv-gpu-restart-actions';
                var restartNow = document.createElement('button');
                restartNow.type = 'button';
                restartNow.className = 'nv-gpu-restart-now';
                restartNow.textContent = translate('l10n.gpuRestartNow', 'Restart now');
                var restartLater = document.createElement('button');
                restartLater.type = 'button';
                restartLater.className = 'nv-gpu-restart-later';
                restartLater.textContent = translate('l10n.gpuRestartLater', 'Restart later');

                restartLater.addEventListener('click', closeRestartDialog);
                restartNow.addEventListener('click', function () {
                    restartNow.disabled = true;
                    restartLater.disabled = true;
                    status.textContent = translate('l10n.gpuRestartInProgress',
                        'Closing and restarting the game…');
                    postJson(RESTART_URL, { confirmed: true, exePath: exePath }).then(function () {
                        closeRestartDialog();
                        window.Notifier.show(
                            translate('l10n.gpuRestartStarted', exeName + ' is restarting', {
                                arg1: exeName
                            }), {
                                key: 'gpu-restarted:' + normalizePath(exePath),
                                group: 'gpu-restarted:' + normalizePath(exePath),
                                priority: true,
                                updaterUi: true
                            });
                    }).catch(function (error) {
                        status.textContent = error.message;
                        restartNow.disabled = false;
                        restartLater.disabled = false;
                    });
                });

                copy.appendChild(title);
                copy.appendChild(detail);
                copy.appendChild(status);
                actions.appendChild(restartNow);
                actions.appendChild(restartLater);
                card.appendChild(copy);
                card.appendChild(actions);
                backdrop.appendChild(card);
                document.body.appendChild(backdrop);
                restartDialog = backdrop;
                restartNow.focus();
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

            function showAppOpened(exePath) {
                if (!gpuAlertsEnabled()) return Promise.resolve();
                return requestJson(MANAGER_URL).then(function (state) {
                    var pathKey = normalizePath(exePath);
                    var profile = (state.profiles || []).find(function (item) {
                        return normalizePath(item.exePath) === pathKey;
                    });
                    if (!profile) return; /* Only apps explicitly added to Game profiles. */

                    var windowsApp = (state.windowsApps || []).find(function (item) {
                        return normalizePath(item.exePath) === pathKey;
                    });
                    var configuredId = windowsApp && windowsApp.lastAppliedAdapterId;
                    var iconPath = profile.iconDataUri || '';
                    var exeName = executableName(exePath);
                    var key = normalizePath(exePath);
                    var gpuToast = null;

                    if (configuredId) {
                        var adapter = (state.adapters || []).find(function (item) {
                            return windowsAdapterId(item.adapterId) === windowsAdapterId(configuredId);
                        });
                        var gpuName = displayGpuName(adapter && adapter.name || configuredId);
                        var message = translate('l10n.gpuAppRunningOnGpu',
                            exeName + ' is running on GPU', { arg1: exeName });
                        gpuToast = {
                            message: message,
                            options: {
                                key: 'gpu-windows-preference:' + key,
                                iconPath: iconPath,
                                fallbackIcon: EXE_ICON_FALLBACK,
                                group: 'gpu-windows-preference:' + key,
                                subtext: gpuName,
                                width: gpuCardWidth(message, gpuName),
                                priority: true,
                                updaterUi: true
                            }
                        };
                    }

                    window.Notifier.show(
                        $filter('translate')('l10n.notificationOpenShare', { arg1: 'Alt + Z' }),
                        {
                            key: 'gpu-app-open:' + key,
                            iconPath: iconPath,
                            fallbackIcon: EXE_ICON_FALLBACK,
                            group: 'gpu-app-open:' + key,
                            priority: true,
                            updaterUi: true,
                            onClosed: gpuToast ? function () {
                                window.setTimeout(function () {
                                    window.Notifier.show(gpuToast.message, gpuToast.options);
                                }, 800);
                            } : null
                        });
                    if (gpuToast)
                        $log.info('[gpu-notifier] app opened with Windows GPU preference: ' +
                            exeName + ' → ' + gpuToast.options.subtext);
                }).catch(function (error) {
                    $log.warn('[gpu-notifier] could not load app or Windows GPU preference', error);
                });
            }

            function showPreferenceToast(event, exeName, iconPath, systemDefaultGpuName) {
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
                    ? systemDefaultGpuName ||
                        translate('l10n.gpuSystemDefaultUnavailable',
                            'GPU name for System default is unavailable')
                    : adapterName;
                if (event.isSystemDefault && !systemDefaultGpuName)
                    $log.warn('[gpu-notifier] Windows did not report a system-default display GPU');
                window.Notifier.show(message, {
                    key: 'gpu-pref:' + normalizePath(event.exePath || exeName),
                    iconPath: iconPath,
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
                if (!event || typeof event.exePath !== 'string' ||
                    dedupe(event)) return;
                var exeName = executableName(event.exePath);
                requestJson(MANAGER_URL).then(function (state) {
                    var pathKey = normalizePath(event.exePath);
                    var app = (state.profiles || []).concat(state.windowsApps || []).find(function (item) {
                        return normalizePath(item.exePath) === pathKey && item.iconDataUri;
                    });
                    var systemDefaultAdapter = (state.adapters || []).find(function (adapter) {
                        return adapter.isSystemDefaultDisplayAdapter;
                    });
                    if (!app)
                        $log.warn('[gpu-notifier] EXE icon unavailable for ' + event.exePath);
                    var iconPath = app && app.iconDataUri || '';
                    var systemDefaultName = displayGpuName(
                        systemDefaultAdapter && systemDefaultAdapter.name || '');
                    requestJson(RUNNING_URL).then(function (running) {
                        var isRunning = (Array.isArray(running.paths) ? running.paths : []).some(
                            function (path) { return normalizePath(path) === pathKey; });
                        if (isRunning)
                            showRestartConfirmation(event.exePath, exeName);
                        else if (gpuAlertsEnabled())
                            showPreferenceToast(event, exeName, iconPath, systemDefaultName);
                    }).catch(function (error) {
                        $log.warn('[gpu-notifier] could not check game before showing restart prompt', error);
                        if (gpuAlertsEnabled())
                            showPreferenceToast(event, exeName, iconPath, systemDefaultName);
                    });
                }).catch(function (error) {
                    $log.warn('[gpu-notifier] could not load EXE icon for GPU preference', error);
                    if (gpuAlertsEnabled())
                        showPreferenceToast(event, exeName, '', '');
                });
            }

            function pollRunningProfiles() {
                if (polling) return;
                polling = true;
                requestJson(RUNNING_URL).then(function (state) {
                    var next = Object.create(null);
                    (Array.isArray(state.paths) ? state.paths : []).forEach(function (path) {
                        next[normalizePath(path)] = path;
                    });

                    if (initialized) {
                        Object.keys(next).forEach(function (pathKey) {
                            if (!runningPaths[pathKey]) showAppOpened(next[pathKey]);
                        });
                    }
                    runningPaths = next;
                    initialized = true;
                }).catch(function (error) {
                    $log.warn('[gpu-notifier] could not check running profiles', error);
                }).then(function () {
                    polling = false;
                });
            }

            function registerSocket() {
                socketService.register(PREF_EVENT, 'nv-gpu-pref');
            }

            eventAggregator.on('nv-gpu-pref', onPreference);
            eventAggregator.on('connect', registerSocket);
            registerSocket();
            pollRunningProfiles();
            window.setInterval(pollRunningProfiles, 1500);
        }
    ]);
})(window);
