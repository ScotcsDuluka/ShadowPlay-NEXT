'use strict'
// defaults.js — the backend's default responses.
//
// Source of truth = REAL backend response captures (docs/osc/real-floor/,
// snapshot in lib/floor-defaults.json — proven live on the Intel floor,
// commits 01b6a02 / 206edb9) + the DulukaFloor.js pattern constants
// (slider bounds, hotkey table, generic hardware shape — proven 2026-09-19).

const floor = require('./floor-defaults.json');

// plain string arrays — the page does indexOf(resName) on the UNWRAPPED
// list; object arrays were the root cause of stuck initInProgress (a263b4d)
const RESOLUTIONS = ['In-game', '2160p 4K', '1440p HD', '1080p HD',
    '720p HD', '480p', '360p', '240p'];
const FRAMERATES = [60, 30];

// bitrate slider bounds (SettingsCustomizeController reads bitrateBpsMin/Max;
// GetCustomize wraps them as bitrate{current,min,max,default})
const BITRATE_RECORD = { min: 10000000, max: 130000000, default: 50000000 };
const BITRATE_BROADCAST = { min: 1000000, max: 40000000, default: 3500000 };

// per-feature settings defaults (GetCustomize / <Feature>/Settings shapes)
const SETTINGS_DEFAULTS = {
    record: { quality: 'Custom', resolution: '1440p HD', framerate: 60, bitrateBps: 50000000 },
    instantreplay: { quality: 'Custom', resolution: '1440p HD', framerate: 60, bitrateBps: 50000000, replayLengthSeconds: 15 },
    broadcast: { quality: 'Good', resolution: '720p HD', framerate: 30, bitrateBps: 3500000, provider: 'YTL' }
};

// store-section key per customize name (DulukaFloor convention)
const SETTINGS_KEY = {
    record: 'recordSettings',
    instantreplay: 'instantReplaySettings',
    broadcast: 'broadcastSettings'
};

// Hotkey preview bindings (mirror OscControllerServer.HotkeyPreviewJson —
// the page renders the label itself via shortcutToStr; a response WITHOUT
// keys renders every tile as "Disabled")
const HOTKEY_DEFAULTS = {
    overlaytoggle: [18, 90], openshare: [18, 90],
    screenshot: [18, 112], nvcameraui: [18, 113],
    modsui: [18, 114], modstoggle: [16, 18, 114],
    modspreset1: [18, 116], modspreset2: [18, 117], modspreset3: [18, 118],
    modspresetcycle: [18, 115],
    broadcasttoggle: [18, 119], broadcastpausetoggle: [16, 18, 119],
    dvrtoggle: [16, 18, 121], recordtoggle: [18, 120], recordsave: [18, 121],
    cameratoggle: [18, 67], mictoggle: [18, 77],
    fps: [18, 80], ptt: [86], commentstoggle: [18, 88],
    overlayaswitch: [18, 65], overlaybswitch: [18, 66], overlaycswitch: [16, 18, 65],
    pmocoverlay: [18, 82], pmocoverlaycycle: [16, 18, 82],
    pmocresetaveragemetrics: [16, 18, 80], pmocloggingtoggle: [16, 18, 76]
};

// Boot-chain-safe generic hardware shape (all values STRINGS — the page
// reads DeviceId/VendorId via gfwsl and GPU[0].IsPrimary via findWhere).
// The boot-time WMI probe (lib/hardwareProbe.js) replaces this per machine.
const GENERIC_HARDWARE_FLOOR = {
    GPU: [{
        LongGPUName: 'Standard Display Adapter', ActualVRAMSize: '1073741824',
        GPURAMType: 'Unknown', VBIOSVersion: '', IsQuadro: '0',
        DeviceId: '0', VendorId: '32902', SubSystemId: '0', SubVendorId: '32902',
        SystemType: '1', BrandType: '2', PhysicalGPUHandle: '0',
        GPUArchitecture: '', GPUArchRevision: '', GPUArchVersion: '',
        GPUArchImplementation: '', IsPrimary: '1'
    }],
    CPUName: 'Standard CPU', PhysicalMemoryCapacity: '8589934592',
    TotalPhysicalMemory: '8589934592', CurrentResolution: '1920x1080',
    OSName: 'Microsoft Windows 10', MoboType: '', BIOSVersion: '',
    JarvisDeviceId: '', TelemetryDeviceId: '', UserDefaultUILanguage: 'en-US',
    ProcessorArchitecture: '9', OSVersion: '10.0.26100', OSBuildNumber: '26100',
    PCName: 'PC',
    DriverVersion: '', IsDCHDriverInstalled: '0', DriverType: 'Standard',
    SLISupported: '0', HasActiveSLITopology: '0', ActiveTopologyGPUCount: '0',
    IsOptimus: '0'
};

// PiplConfig floor — the REAL shape the page boots through. jarvis.server
// is overridden at request time to point at the Duluka Server (:5115).
const PIPL_FLOOR = (floor.endpoints['/PiplConfig/v.1.0/data'] || {}).body || {
    daysToExpire: 1,
    isConnectEnabled: true,
    configData: {
        jarvis: { server: 'https://accounts.nvgs.nvidia.com' },
        gfwsl: { server: 'https://gfwsl.geforce.com/' },
        aem: { server: 'https://www.nvidia.com/' },
        vrs: { server: 'https://www.nvidia.com' },
        jsEvents: { server: 'https://events.gfe.nvidia.com' },
        nvTelemetry: {
            eventsServer: 'https://events.gfe.nvidia.com/v1.0/events/json',
            feedbackServer: 'https://telemetry.gfe.nvidia.com/gfc/v2.0/head',
            feedbackAttachmentServer: 'https://telemetry.gfe.nvidia.com/gfc/v2.0/attachment'
        },
        redirect: { server: 'https://www.nvidia.com/content/drivers/redirect.asp?language=' }
    }
};

module.exports = {
    floor: floor,
    RESOLUTIONS: RESOLUTIONS,
    FRAMERATES: FRAMERATES,
    BITRATE_RECORD: BITRATE_RECORD,
    BITRATE_BROADCAST: BITRATE_BROADCAST,
    SETTINGS_DEFAULTS: SETTINGS_DEFAULTS,
    SETTINGS_KEY: SETTINGS_KEY,
    HOTKEY_DEFAULTS: HOTKEY_DEFAULTS,
    GENERIC_HARDWARE_FLOOR: GENERIC_HARDWARE_FLOOR,
    PIPL_FLOOR: PIPL_FLOOR
};
