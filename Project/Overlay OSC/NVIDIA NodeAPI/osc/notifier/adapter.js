(function (window) {
    'use strict';

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
            window.NvNotifierBridge = {
                show: showNotification,
                status: function () { return window.Notifier.status(); }
            };
            $log.info('[notifier] New stacked notifier connected to OSC events');
        }
    ]);
})(window);
