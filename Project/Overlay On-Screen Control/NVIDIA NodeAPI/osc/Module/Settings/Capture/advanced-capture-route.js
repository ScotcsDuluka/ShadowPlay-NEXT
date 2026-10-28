/* Native NVIDIA OSC Advanced Capture settings route.
 * Loaded in osc/index.html HEAD before AngularJS bootstrap. UI choices that
 * have no proven backend remain design-only and never POST settings.
 */
(function (window) {
  'use strict';
  if (!window.angular || window.__nvAdvancedCaptureRouteInstalled) return;
  window.__nvAdvancedCaptureRouteInstalled = true;

  var encodeNames = {
    NVENC_H264: 'NVIDIA NVENC H.264',
    NVENC_HEVC: 'NVIDIA NVENC HEVC',
    NVENC_AV1: 'NVIDIA NVENC AV1',
    QuickSync_H264: 'Intel Quick Sync H.264',
    QuickSync_HEVC: 'Intel Quick Sync HEVC',
    AMF_H264: 'AMD AMF H.264',
    AMF_HEVC: 'AMD AMF HEVC',
    LibX264: 'Software x264',
    LibX265: 'Software x265'
  };
  var aliases = {
    h264_nvenc:'NVENC_H264', hevc_nvenc:'NVENC_HEVC',
    av1_nvenc:'NVENC_AV1', h264_qsv:'QuickSync_H264',
    hevc_qsv:'QuickSync_HEVC', h264_amf:'AMF_H264',
    hevc_amf:'AMF_HEVC', libx264:'LibX264', libx265:'LibX265'
  };
  var planDefinitions = [
    { key:'desktop', title:'Desktop Capture API', labelKey:'l10n.oscCaptureDesktopApi', choices:[
      {id:'dxgi', name:'DXGI Desktop Duplication (existing)'},
      {id:'wgc', name:'Windows Graphics Capture (not wired)', disabled:true},
      {id:'ffmpeg', name:'FFmpeg capture source (not wired)', disabled:true}
    ]},
    { key:'exclusive', title:'Exclusive Capture API', labelKey:'l10n.oscCaptureExclusiveApi', choices:[
      {id:'dxgi', name:'DXGI (Exclusive unverified)'},
      {id:'wgc', name:'WGC monitor (not verified)', disabled:true},
      {id:'game', name:'Game/Present Source (not implemented)', disabled:true}
    ]},
    { key:'video', title:'Video Encoder (design)', labelKey:'l10n.oscCaptureVideoEncoder', choices:[
      {id:'nvenc', name:'NVIDIA NVENC (existing)'},
      {id:'qsv', name:'Intel Native QSV (not wired)', disabled:true},
      {id:'amf', name:'AMD AMF (not verified)', disabled:true}
    ]},
    { key:'systemAudio', title:'System Audio', labelKey:'l10n.oscCaptureSystemAudio', choices:[
      {id:'off', name:'Off'},
      {id:'wasapi', name:'Duluka WASAPI (component exists)'},
      {id:'ffmpeg', name:'FFmpeg Audio (not wired)', disabled:true}
    ]},
    { key:'microphone', title:'Microphone', labelKey:'l10n.oscCaptureMicrophone', choices:[
      {id:'off', name:'Off'},
      {id:'wasapi', name:'Duluka WASAPI (component exists)'},
      {id:'ffmpeg', name:'FFmpeg Audio (not wired)', disabled:true}
    ]},
    { key:'recovery', title:'Source Recovery', labelKey:'l10n.oscCaptureRecovery', disabled:true, choices:[
      {id:'planned', name:'Recovery / API Fallback (planned)', disabled:true}
    ]}
  ];

  var template = [
    '<div class="osc-content nv-advanced-capture-route" layout="row" ng-cloak>',
      '<div class="osc-bread-crumb"><nv-osc-tile title="Advanced Capture" icon-font="icon-settings"></nv-osc-tile></div>',
      '<div class="osc-central-div" layout="column" flex>',
        '<div class="preferences-root-panel">',
          '<div class="preferences-root-label"><p translate="l10n.oscCaptureAdvancedTitle">Advanced Capture</p></div>',
          '<p class="preferences-instructional-text" translate="l10n.oscCaptureAdvancedInfo">Configure capture routing and review encoder capabilities. Unsupported pipeline choices remain disabled.</p>',
          '<section class="nv-advanced-capture-group nv-advanced-capture-plan">',
          '<div class="nv-advanced-capture-heading" translate="l10n.oscCapturePipelineTitle">Pipeline design · preview only</div>',
          '<div class="nv-advanced-capture-grid" role="group" aria-label="Capture pipeline design">',
            '<div class="nv-advanced-capture-field" ng-repeat="item in captureAdvanced.definitions track by item.key">',
              '<span class="nv-advanced-capture-field-label" translate="{{item.labelKey}}">{{item.title}}</span>',
              '<md-select class="osc-select resolution-fps-select" ng-model="captureAdvanced.plan[item.key]" ng-disabled="item.disabled" aria-label="{{item.title}}" hover-focus>',
                '<md-option ng-repeat="choice in item.choices track by choice.id" ng-value="choice.id" ng-disabled="choice.disabled" hover-focus>{{choice.name}}</md-option>',
              '</md-select>',
            '</div>',
          '</div>',
          '<p class="preferences-instructional-text nv-advanced-capture-note" translate="l10n.oscCaptureDraftOnly">Pipeline selections are previews and are not saved or applied to the recording engine.</p>',
          '</section>',
          '<section class="nv-advanced-capture-group nv-advanced-capture-live">',
          '<div class="nv-advanced-capture-heading" translate="l10n.oscCaptureEncoderTitle">Live encoder settings</div>',
          '<div class="nv-advanced-capture-grid nv-advanced-capture-encoder-grid">',
            '<div class="nv-advanced-capture-field">',
              '<span class="nv-advanced-capture-field-label" translate="l10n.oscCaptureCodecLabel">Codec encoder</span>',
              '<md-select class="osc-select resolution-fps-select" ng-model="captureAdvanced.encoder" ng-change="captureAdvanced.changed()" ng-disabled="!captureAdvanced.ready || captureAdvanced.busy" aria-label="Codec encoder" hover-focus>',
                '<md-option ng-repeat="option in captureAdvanced.codecs track by option.id" ng-value="option.id" ng-disabled="option.disabled" hover-focus>{{option.name}}</md-option>',
              '</md-select>',
            '</div>',
            '<div class="nv-advanced-capture-field">',
              '<span class="nv-advanced-capture-field-label" translate="l10n.oscCapturePresetLabel">Encoder preset</span>',
              '<md-select class="osc-select resolution-fps-select" ng-model="captureAdvanced.preset" ng-change="captureAdvanced.changed()" ng-disabled="!captureAdvanced.ready || captureAdvanced.busy" aria-label="Encoder preset" hover-focus>',
                '<md-option ng-repeat="option in captureAdvanced.presets track by option.id" ng-value="option.id" hover-focus>{{option.name}}</md-option>',
              '</md-select>',
            '</div>',
          '</div>',
          '<div class="nv-advanced-capture-preview">',
            '<div class="nv-advanced-capture-heading" translate="l10n.oscCaptureBuildTitle">Recording configuration preview</div>',
            '<p class="nv-advanced-capture-result" ng-bind="captureAdvanced.summary"></p>',
            '<p class="preferences-instructional-text" translate="l10n.oscCaptureBuildNote">This describes requested configuration, not proof of an active encoder or successful MP4 recording.</p>',
          '</div>',
          '</section>',
          '<p role="status" class="nv-advanced-capture-status" ng-if="captureAdvanced.message">{{captureAdvanced.message}}</p>',
          '<p role="alert" class="nv-advanced-capture-error" ng-if="captureAdvanced.error">{{captureAdvanced.error}}</p>',
        '</div>',
      '</div>',
      '<div class="osc-side-buttons" layout="column">',
        '<button type="button" class="osc-button osc-button-green" ng-click="captureAdvanced.save()" ng-disabled="!captureAdvanced.ready || !captureAdvanced.dirty || captureAdvanced.busy" translate="l10n.save" hover-focus>Save</button>',
        '<button type="button" class="osc-button osc-button-gray" ng-click="captureAdvanced.load()" ng-disabled="captureAdvanced.busy" translate="l10n.oscUi_reload" hover-focus>Reload</button>',
        '<button type="button" class="osc-button osc-button-gray" ng-click="captureAdvanced.back()" translate="l10n.back" hover-focus>Back</button>',
      '</div>',
    '</div>'
  ].join('');

  window.angular.module('main')
    .config(['$stateProvider', function ($stateProvider) {
      $stateProvider.state('main.osc-capture-advanced', {
        url: '/preferences/video-capture/advanced',
        template: template,
        controller: 'OscAdvancedCaptureController',
        controllerAs: 'captureAdvanced'
      });
    }])
    .controller('OscAdvancedCaptureController',
      ['$http', '$state', '$scope', function ($http, $state, $scope) {
        var vm = this, disposed = false, saved = null, allowed = [];
        vm.busy = false;
        vm.ready = false;
        vm.dirty = false;
        vm.message = '';
        vm.error = '';
        vm.summary = 'Loading verified Capture configuration…';
        vm.definitions = planDefinitions;
        vm.plan = {
          desktop:'dxgi', exclusive:'dxgi', video:'nvenc',
          systemAudio:'off', microphone:'off', recovery:'planned'
        };
        vm.encoder = 'NVENC_H264';
        vm.preset = 4;
        vm.presets = [];
        vm.codecs = [];
        $scope.$on('$destroy', function () { disposed = true; });

        function validSettings(data) {
          return !!data && ['Duluka','FFmpeg'].indexOf(data.engineMode) >= 0 &&
            typeof data.apiCapture === 'string' &&
            Object.prototype.hasOwnProperty.call(encodeNames, data.encoder) &&
            Number.isInteger(data.encoderPreset) &&
            data.encoderPreset >= 1 && data.encoderPreset <= 7 &&
            typeof data.captureCursor === 'boolean' &&
            Number.isFinite(data.fps) && data.fps > 0 &&
            Number.isFinite(data.bitrateKbps) && data.bitrateKbps > 0 &&
            typeof data.useNativeResolution === 'boolean' &&
            Number.isFinite(data.width) && data.width > 0 &&
            Number.isFinite(data.height) && data.height > 0;
        }
        function readCatalog(catalog, settings) {
          if (!catalog || catalog.schemaVersion !== 1 ||
              catalog.readOnly !== true || !Array.isArray(catalog.providers) ||
              catalog.policyValid !== true) throw new Error('Capture route catalog unavailable');
          var provider = catalog.providers.filter(function (item) {
            return item && item.id === settings.engineMode &&
              item.routeWired === true && Array.isArray(item.encoders);
          })[0];
          if (!provider) throw new Error('Selected engine has no verified route');
          allowed = provider.encoders.map(function (id) { return aliases[id] || id; })
            .filter(function (id) { return Object.prototype.hasOwnProperty.call(encodeNames, id); })
            .filter(function (id, index, all) { return all.indexOf(id) === index; });
          // Mirror the main Video Settings GPU-negative-evidence gate.
          // A codec's presence in FFmpeg binary is NOT evidence it works.
          var binary = catalog.workerRuntime && catalog.workerRuntime.ffmpegBinary;
          var proof = binary && binary.nvencSyntheticValidation;
          if (settings.engineMode === 'FFmpeg' && proof &&
              proof.status === 'complete' && Number.isInteger(proof.gpuCount) &&
              proof.gpuCount > 0 && proof.gpuCount <= 8 &&
              Array.isArray(proof.results)) {
            allowed = allowed.filter(function (id) {
              var key = Object.keys(aliases).filter(function (name) {
                return aliases[name] === id && /_nvenc$/.test(name);
              })[0];
              if (!key) return true;
              var rows = proof.results.filter(function (row) {
                return row && row.encoder === key;
              });
              if (rows.length !== proof.gpuCount) return true;
              var seen = {};
              var valid = rows.every(function (row) {
                if (!Number.isInteger(row.gpuIndex) || row.gpuIndex < 0 ||
                    row.gpuIndex >= proof.gpuCount ||
                    typeof row.syntheticEncodePassed !== 'boolean' ||
                    Object.prototype.hasOwnProperty.call(seen, row.gpuIndex)) return false;
                seen[row.gpuIndex] = true;
                return true;
              });
              return !valid || rows.some(function (row) {
                return row.syntheticEncodePassed === true;
              });
            });
          }
          vm.codecs = allowed.map(function (id) { return { id:id, name:encodeNames[id], disabled:false }; });
          if (allowed.indexOf(settings.encoder) < 0) {
            vm.codecs.push({id:settings.encoder,
              name:encodeNames[settings.encoder]+' (saved, unavailable)', disabled:true});
          }
          if (!allowed.length) throw new Error('No encoder route is available');
        }
        function refreshPresetOptions() {
          var map = /^AMF_/.test(vm.encoder)
            ? [{id:2,name:'quality'}, {id:4,name:'balanced'}, {id:6,name:'speed'}]
            : /^QuickSync_/.test(vm.encoder)
              ? ['veryslow','slower','slow','medium','fast','faster','veryfast']
              : /^LibX/.test(vm.encoder)
                ? ['slow','medium','fast','faster','veryfast','superfast','ultrafast']
                : ['P1','P2','P3','P4','P5','P6','P7'];
          vm.presets = map.map(function (item, i) {
            return typeof item === 'string' ? {id:i+1,name:item} : item;
          });
        }
        function updateSummary() {
          if (!saved) return;
          vm.summary = 'Engine: '+saved.engineMode+
            '  ·  Capture API: '+saved.apiCapture+
            '  ·  Encoder: '+(encodeNames[vm.encoder] || 'Unknown')+
            '  ·  Preset: '+(vm.presets.filter(function (p) { return p.id === vm.preset; })[0] || {}).name+
            '  ·  '+saved.fps+' FPS'+
            '  ·  '+saved.bitrateKbps+' kbps';
        }
        vm.changed = function () {
          refreshPresetOptions();
          if (!vm.presets.some(function (item) { return item.id === vm.preset; })) {
            vm.preset = vm.presets[0].id;
          }
          vm.dirty = !!saved && (vm.encoder !== saved.encoder ||
            vm.preset !== saved.encoderPreset);
          vm.message = vm.dirty ? 'Unsaved encoder selection. Engine rebuild may be required.' : '';
          vm.error = '';
          updateSummary();
        };
        vm.load = function () {
          if (vm.busy) return;
          vm.busy=true;vm.ready=false;vm.error='';vm.message='Loading Capture settings…';
          return $http.get('/DulukaCapture/v.1.0/settings', {cache:false})
            .then(function (result) {
              if (!validSettings(result.data)) throw new Error('Invalid Capture settings');
              return $http.get('/DulukaCapture/v.1.0/engines',{cache:false})
                .then(function (catalog) {
                  if (disposed) return;
                  readCatalog(catalog.data,result.data);
                  saved = angular.copy(result.data);
                  vm.encoder = saved.encoder;
                  vm.preset = saved.encoderPreset;
                  refreshPresetOptions();
                  vm.ready = allowed.length > 0;
                  vm.dirty = false;
                  vm.message = allowed.indexOf(vm.encoder) >= 0 ?
                    'Configuration loaded (not live recording proof).' :
                    'Saved encoder unavailable. Select a supported encoder before saving.';
                  updateSummary();
                });
            }).catch(function (err) {
              if (disposed) return;
              vm.error='Could not load Capture configuration: '+
                ((err && err.message) || 'backend unavailable');
            }).finally(function () {
              if (!disposed) vm.busy=false;
            });
        };
        vm.save = function () {
          if (!saved || !vm.ready || !vm.dirty || vm.busy ||
              allowed.indexOf(vm.encoder) < 0 ||
              !vm.presets.some(function (p) {return p.id === vm.preset;})) return;
          var desired=angular.copy(saved);
          desired.quality='Custom';desired.encoder=vm.encoder;desired.encoderPreset=vm.preset;
          vm.busy=true;vm.error='';vm.message='Saving verified Capture settings…';
          return $http.post('/DulukaCapture/v.1.0/settings', desired)
            .then(function (response) {
              if (!validSettings(response.data) ||
                  response.data.encoder !== desired.encoder ||
                  response.data.encoderPreset !== desired.encoderPreset) {
                throw new Error('The Capture service did not confirm the selected encoder');
              }
              if (disposed) return;
              saved=angular.copy(response.data);
              vm.dirty=false;
              vm.message='Encoder setting saved. An engine rebuild may be required.';
              updateSummary();
            }).catch(function (err) {
              if (disposed) return;
              vm.error='Encoder setting not saved: '+((err && err.message) || 'unknown error');
              vm.dirty=true;
            }).finally(function () { if (!disposed) vm.busy=false; });
        };
        vm.back = function () { return $state.go('main.preferences.video'); };
        vm.load();
      }]);
})(window);
