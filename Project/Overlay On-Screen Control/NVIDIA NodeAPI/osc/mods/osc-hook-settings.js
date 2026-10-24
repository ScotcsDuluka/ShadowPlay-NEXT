(function (window) {
  'use strict';
  if (!window.angular || window.__dulukaOscHookSettingsInstalled) return;
  window.__dulukaOscHookSettingsInstalled = true;

  // Keep the stock NVIDIA Preferences panel, typography and side buttons.
  // These are LIVE Host observations, not unimplemented Hook toggles.
  var template = [
    '<div class="osc-content" layout="row" ng-cloak>',
    '<div class="osc-bread-crumb"><nv-osc-tile title="Hook Settings" icon-font="icon-settings"></nv-osc-tile></div>',
    '<div class="osc-central-div" layout="column" style="max-height:76vh;overflow-y:auto;overflow-x:hidden"><div class="preferences-root-panel">',
    '<div class="preferences-root-label"><p translate="l10n.oscHookTitle">Hook Settings</p></div>',
    '<p class="preferences-instructional-text" translate="l10n.oscHookInfo">Live Host transport diagnostics and Hook integration status.</p>',
    '<p ng-if="hookSettings.busy" role="status" translate="l10n.oscHookLoading">Checking Host...</p>',
    '<p ng-if="hookSettings.error" role="alert" translate="l10n.oscHookError">Could not read Host status.</p>',
    '<div ng-if="hookSettings.loaded" aria-live="polite">',
    '<p><strong translate="l10n.oscHookHostMode">Host rendering:</strong> {{hookSettings.status.hostMode}}</p>',
    '<p><strong translate="l10n.oscHookConsumer">Game adapter:</strong> <span ng-if="!hookSettings.status.consumerObserved" translate="l10n.oscHookNoGame">No connected game adapter detected</span><span ng-if="hookSettings.status.consumerObserved">{{hookSettings.transportName()}}</span></p>',
    '<p><strong translate="l10n.oscHookFocus">Game focused:</strong> <span ng-if="hookSettings.status.gameFocused" translate="l10n.oscHookYes">Yes</span><span ng-if="!hookSettings.status.gameFocused" translate="l10n.oscHookNo">No</span></p>',
    '<p><strong translate="l10n.oscHookOverlay">Overlay open:</strong> <span ng-if="hookSettings.status.overlayOpen" translate="l10n.oscHookYes">Yes</span><span ng-if="!hookSettings.status.overlayOpen" translate="l10n.oscHookNo">No</span></p>',
    '<p><strong translate="l10n.oscHookSharedGpu">Host GPU_SHARED requested:</strong> <span ng-if="hookSettings.status.gpuSharedRequested" translate="l10n.oscHookYes">Yes</span><span ng-if="!hookSettings.status.gpuSharedRequested" translate="l10n.oscHookNo">No</span></p>',
    '<p><strong translate="l10n.oscHookGpuReady">Host GPU device ready:</strong> <span ng-if="hookSettings.status.gpuDeviceReady" translate="l10n.oscHookYes">Yes</span><span ng-if="!hookSettings.status.gpuDeviceReady" translate="l10n.oscHookNo">No</span></p>',
    '<p class="preferences-instructional-text" translate="l10n.oscHookCaveat">An observed game adapter does not confirm that the in-game Hook is drawing. DX9Ex GPU preview requires an explicit game-side Pump; CPU fallback remains available.</p>',
    '</div>',
    '<div class="preferences-root-label"><p translate="l10n.oscHookTransportTitle">Hook transport per graphics API</p></div>',
    '<p class="preferences-instructional-text" translate="l10n.oscHookTransportInfo">Auto uses the safest available route. Unsupported modes are disabled to avoid false settings.</p>',
    '<p ng-if="hookSettings.transportBusy" role="status" translate="l10n.oscHookLoading">Loading...</p>',
    '<p ng-if="hookSettings.transportError" role="alert" translate="l10n.oscHookTransportError">Unable to save transport settings.</p>',
    '<p ng-if="!hookSettings.transportLoaded && !hookSettings.transportBusy" class="preferences-instructional-text" translate="l10n.oscHookTransportUnavailable">This Host does not expose transport selection yet.</p>',
    '<div ng-repeat="api in hookSettings.apis track by api.id">',
    '<md-input-container><label>{{api.display}}</label>',
    '<md-select class="osc-select resolution-fps-select" ng-model="api.mode" ng-change="hookSettings.saveMode(api)" ng-disabled="!api.implemented || hookSettings.transportBusy" aria-label="{{api.display}} Hook transport" hover-focus>',
    '<md-option ng-repeat="mode in hookSettings.transportChoices track by mode.key" ng-value="mode.key" ng-disabled="!hookSettings.modeAllowed(api, mode.key)" md-ink-ripple="false" hover-focus><span translate="{{mode.label}}">{{mode.name}}</span></md-option>',
    '</md-select></md-input-container>',
    '<p class="preferences-instructional-text" ng-if="api.implemented" role="status">{{hookSettings.routeLabel(api)}} · {{hookSettings.selectionLabel(api)}}</p>',
    '<p class="preferences-instructional-text" ng-if="!api.implemented" translate="l10n.oscHookApiUnavailable">Detection only. Native overlay renderer is not implemented.</p>',
    '<p class="preferences-instructional-text" ng-if="api.id === \'d3d9\'" translate="l10n.oscHookD3d9Note">DX9 uses the CPU Shared Memory bridge.</p>',
    '<p class="preferences-instructional-text" ng-if="api.id === \'d3d9ex\'" translate="l10n.oscHookD3d9ExNote">DX9Ex GPU is experimental and requires an in-game Pump; CPU is the verified default.</p>',
    '<p class="preferences-instructional-text" ng-if="api.id === \'d3d11\'" translate="l10n.oscHookD3d11Note">D3D11 uses GPU_SHARED with a cross-GPU CPU mirror when needed; CPU-only is not implemented.</p>',
    '</div>',
    '<p ng-if="hookSettings.transportSaved" role="status" translate="l10n.oscHookTransportSaved">Transport preference saved.</p>',
    '</div></div><div class="osc-side-buttons" layout="column">',
    '<button class="osc-button osc-button-green" ng-click="hookSettings.refresh()" ng-disabled="hookSettings.busy" translate="l10n.oscHookRefresh" hover-focus>Refresh status</button>',
    '<button class="osc-button osc-button-gray" ng-click="hookSettings.renderer()" translate="l10n.oscHookRenderer" hover-focus>Renderer Mode</button>',
    '<button class="osc-button osc-button-gray" ng-click="hookSettings.back()" translate="l10n.back" hover-focus>Back</button>',
    '</div></div>'
  ].join('');

  window.angular.module('main')
    .config(['$stateProvider', function ($stateProvider) {
      $stateProvider.state('main.osc-hook', {
        url:'/preferences/overlay-hook',
        template:template,
        controller:'OscHookSettingsController',
        controllerAs:'hookSettings'
      });
    }])
    .controller('OscHookSettingsController', ['$q', '$state', '$scope',
      function ($q, $state, $scope) {
        var vm=this, disposed=false;
        vm.loaded=false; vm.busy=false; vm.error='';
        vm.status={hostMode:'unknown',consumerObserved:false,consumerApi:0,
          gameFocused:false,overlayOpen:false,gpuSharedRequested:false,
          gpuDeviceReady:false};
        vm.apis=[];
        vm.transportLoaded=false;
        vm.transportBusy=false;
        vm.transportSaved=false;
        vm.transportError='';
        vm.transportChoices=[
          {key:'auto',label:'l10n.oscRendererAuto',name:'Auto'},
          {key:'gpu',label:'l10n.oscRendererGpu',name:'GPU'},
          {key:'cpu',label:'l10n.oscRendererCpu',name:'CPU'}
        ];
        var apiNames={d3d9:'Direct3D 9',d3d9ex:'Direct3D 9Ex',
          d3d10:'Direct3D 10',d3d11:'Direct3D 11',
          d3d12:'Direct3D 12',vulkan:'Vulkan',opengl:'OpenGL'};
        $scope.$on('$destroy',function(){disposed=true;});
        function valid(data) {
          return data && typeof data.hostMode==='string' &&
            data.hostMode.length>0 && data.hostMode.length<40 &&
            typeof data.consumerObserved==='boolean' &&
            [0,9,11].indexOf(data.consumerApi)>=0 &&
            ['gameFocused','overlayOpen','gpuSharedRequested','gpuDeviceReady']
              .every(function(k){return typeof data[k]==='boolean';});
        }
        vm.transportName=function(){
          if(!vm.status.consumerObserved)return 'Not connected';
          if(vm.status.consumerApi===9)return 'Direct3D 9 / 9Ex';
          if(vm.status.consumerApi===11)return 'Direct3D 11';
          return 'Unknown';
        };
        vm.refresh=function(){
          if(disposed||vm.busy)return;
          vm.busy=true;vm.error='';
          return $q(function(resolve,reject){
            if(typeof window.cefQuery!=='function'){
              reject(new Error('Native Host unavailable'));return;
            }
            try{
              window.cefQuery({
                request:JSON.stringify({command:'QUERY_OSC_GET_HOOK_STATUS'}),
                onSuccess:function(raw){
                  try{
                    var data=JSON.parse(raw);
                    if(!valid(data)||data.error){reject(new Error('Invalid Host response'));return;}
                    resolve(data);
                  }catch(error){reject(error);}
                },
                onFailure:function(code,message){reject(new Error(message||String(code)));}
              });
            }catch(error){reject(error);}
          }).then(function(data){
            if(disposed)return;
            vm.status=data;vm.loaded=true;
          },function(){
            if(!disposed)vm.error='l10n.oscHookError';
          }).finally(function(){
            if(!disposed)vm.busy=false;
          });
        };
        vm.modeAllowed=function(api,mode){
          return mode==='auto' || (api.implemented &&
            (mode==='gpu' ? api.gpuAvailable : mode==='cpu' && api.cpuAvailable));
        };
        vm.routeLabel=function(api){
          if(!api.implemented)return 'Unavailable';
          if(api.id==='d3d9')return 'Route: CPU Shared Memory';
          if(api.id==='d3d9ex')return 'Route: CPU fallback; GPU preview not verified';
          if(api.id==='d3d11')return 'Route: GPU_SHARED; CPU mirror if needed';
          return 'Native renderer not available';
        };
        vm.selectionLabel=function(api){
          return 'Selected: '+String(api.mode || 'auto').toUpperCase()+
            ' (saved preference, not proof of an active in-game route)';
        };
        function transportQuery(request){
          return $q(function(resolve,reject){
            if(typeof window.cefQuery!=='function'){
              reject(new Error('Native Host unavailable'));return;
            }
            try{
              window.cefQuery({
                request:JSON.stringify(request),
                onSuccess:function(raw){
                  try{
                    var data=JSON.parse(raw);
                    if(!data || data.error || !Array.isArray(data.apis) ||
                        data.apis.length!==7 ||
                        data.apis.some(function(api){
                          return !api || !Object.prototype.hasOwnProperty.call(apiNames,api.id) ||
                            ['auto','gpu','cpu'].indexOf(api.mode)<0 ||
                            typeof api.implemented!=='boolean' ||
                            typeof api.gpuAvailable!=='boolean' ||
                            typeof api.cpuAvailable!=='boolean' ||
                            typeof api.normalRoute!=='string';
                        })) {reject(new Error('Invalid Hook transport response'));return;}
                    resolve(data);
                  }catch(error){reject(error);}
                },
                onFailure:function(code,message){reject(new Error(message||String(code)));}
              });
            }catch(error){reject(error);}
          });
        }
        function acceptModes(data){
          vm.apis=data.apis.map(function(api){
            return {id:api.id, display:apiNames[api.id], mode:api.mode,
              persisted:api.mode, implemented:api.implemented,
              gpuAvailable:api.gpuAvailable, cpuAvailable:api.cpuAvailable,
              normalRoute:api.normalRoute};
          });
          vm.transportLoaded=true;
        }
        vm.loadModes=function(){
          if(disposed||vm.transportBusy)return;
          vm.transportBusy=true;vm.transportError='';
          return transportQuery({command:'QUERY_OSC_GET_HOOK_TRANSPORT'})
            .then(function(data){if(!disposed)acceptModes(data);},
              function(){if(!disposed)vm.transportError='l10n.oscHookTransportUnavailable';})
            .finally(function(){if(!disposed)vm.transportBusy=false;});
        };
        vm.saveMode=function(api){
          if(disposed||vm.transportBusy||!vm.transportLoaded)return;
          if(!api.implemented||!vm.modeAllowed(api,api.mode)){
            api.mode=api.persisted;
            vm.transportError='l10n.oscHookTransportError';return;
          }
          if(api.mode===api.persisted)return;
          vm.transportBusy=true;vm.transportError='';vm.transportSaved=false;
          return transportQuery({command:'QUERY_OSC_SET_HOOK_TRANSPORT',
            api:api.id,mode:api.mode})
            .then(function(data){
              if(!disposed){acceptModes(data);vm.transportSaved=true;}
            },function(){
              if(!disposed){
                api.mode=api.persisted;
                vm.transportError='l10n.oscHookTransportError';
              }
            }).finally(function(){if(!disposed)vm.transportBusy=false;});
        };
        vm.renderer=function(){return $state.go('main.osc-renderer');};
        vm.back=function(){return $state.go('main.main-menu');};
        vm.refresh();
        vm.loadModes();
      }
    ]);
})(window);
