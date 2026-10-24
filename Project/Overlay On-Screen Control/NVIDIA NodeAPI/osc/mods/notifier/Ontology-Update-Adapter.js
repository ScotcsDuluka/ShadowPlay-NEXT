/* nv-ontology catalog update notifications.
 * Public version is a hint, never a command to download or execute code.
 * Local catalog status comes from the validated OSC GameWatcher adapter.
 */
(function (window) {
    'use strict';

    var VERSION_URL = 'https://nv-ontology.pages.dev/api/v1/catalog/version';
    var STORAGE_KEY = 'nv-ontology-update-state-v1';
    var POLL_MS = 5 * 60 * 1000;
    var REQUEST_TIMEOUT_MS = 10000;
    var UPDATE_ICON_GLYPH = '\uE91E'; // nvgcshare uniE91E

    if (!window.angular || !window.Notifier) return;

    angular.module('main').run(['eventAggregator', '$log', '$filter', function (eventAggregator, $log, $filter) {
        var state = readState();
        var remoteVersion = '';
        var localVersion = '';
        var pendingVersion = '';
        var checking = false;

        function validVersion(value) {
            return typeof value === 'string' &&
                /^\d+(?:\.\d+){1,3}$/.test(value) && value.length <= 64;
        }

        function newerThan(a, b) {
            if (!validVersion(a) || !validVersion(b)) return false;
            var left = a.split('.');
            var right = b.split('.');
            var size = Math.max(left.length, right.length);
            for (var i = 0; i < size; i++) {
                var x = Number(left[i] || 0);
                var y = Number(right[i] || 0);
                if (x !== y) return x > y;
            }
            return false;
        }

        function readState() {
            try {
                var saved = JSON.parse(window.localStorage.getItem(STORAGE_KEY) || '{}');
                return {
                    lastSeenVersion: validVersion(saved.lastSeenVersion) ? saved.lastSeenVersion : '',
                    lastNotifiedVersion: validVersion(saved.lastNotifiedVersion) ? saved.lastNotifiedVersion : ''
                };
            } catch (error) {
                return { lastSeenVersion: '', lastNotifiedVersion: '' };
            }
        }

        function saveState() {
            try {
                window.localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
            } catch (error) {
                $log.warn('[ontology-update] unable to persist update notification state', error);
            }
        }

        function translateOrEnglish(key, englishFallback, args) {
            try {
                var value = $filter('translate')(key, args || {});
                if (typeof value === 'string' && value.trim() && value !== key)
                    return value;
            } catch (error) {
                $log.warn('[ontology-update] translation unavailable for ' + key, error);
            }
            return englishFallback;
        }

        function tryNotify() {
            if (!remoteVersion || state.lastNotifiedVersion === remoteVersion) return;
            var localOutdated = newerThan(remoteVersion, localVersion);
            if (!pendingVersion && !localOutdated) return;

            var group = 'nv-ontology-update:' + remoteVersion;
            var detail = 'v' + remoteVersion;
            var title = translateOrEnglish(
                'l10n.notificationOntologyCatalogUpdate',
                'nv-ontology catalog update available');
            var subtext = translateOrEnglish(
                'l10n.notificationOntologyCatalogDetails',
                'Update to ' + detail, { arg1: detail });

            window.Notifier.show(title, {
                key: 'NV_ONTOLOGY_UPDATED',
                group: group,
                icon: UPDATE_ICON_GLYPH,
                positive: true,
                subtext: subtext,
                priority: true,
                width: 420
            });
            if (window.Notifier.hasActiveGroupPrefix(group)) {
                state.lastNotifiedVersion = remoteVersion;
                pendingVersion = '';
                saveState();
                $log.info('[ontology-update] notified about catalog ' + remoteVersion);
            } else {
                // All notifier slots can be protected; retry on the next poll.
                pendingVersion = remoteVersion;
                $log.info('[ontology-update] no toast slot available; will retry');
            }
        }

        function applyRemote(payload) {
            if (!payload || !validVersion(payload.catalogVersion) ||
                typeof payload.gameCount !== 'number' ||
                !isFinite(payload.gameCount) || payload.gameCount < 0 ||
                Math.floor(payload.gameCount) !== payload.gameCount) return;

            remoteVersion = payload.catalogVersion;
            if (newerThan(remoteVersion, state.lastSeenVersion)) {
                pendingVersion = remoteVersion;
                state.lastSeenVersion = remoteVersion;
                saveState();
            } else if (!state.lastSeenVersion) {
                // First observation is a baseline, not a fabricated update.
                state.lastSeenVersion = remoteVersion;
                saveState();
            }

            // If the local GameWatcher is older than the public catalog,
            // announce the available newer version even on first observation.
            tryNotify();
        }

        function checkUpdates() {
            if (checking) return;
            checking = true;
            var controller = typeof AbortController === 'function' ? new AbortController() : null;
            var timeout = controller ? window.setTimeout(function () {
                controller.abort();
            }, REQUEST_TIMEOUT_MS) : null;
            var options = { cache: 'no-store' };
            if (controller) options.signal = controller.signal;

            fetch(VERSION_URL, options).then(function (response) {
                if (!response.ok) throw new Error('HTTP ' + response.status);
                return response.json();
            }).then(applyRemote).catch(function (error) {
                $log.warn('[ontology-update] catalog version check failed', error);
            }).then(function () {
                if (timeout !== null) window.clearTimeout(timeout);
                checking = false;
            });
        }

        eventAggregator.on('nv-ontology-local-catalog-version', function (version) {
            if (!validVersion(version)) return;
            localVersion = version;
            tryNotify();
        });
        window.setTimeout(checkUpdates, 3000);
        window.setInterval(checkUpdates, POLL_MS);
    }]);
})(window);
