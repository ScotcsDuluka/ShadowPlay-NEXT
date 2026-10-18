'use strict'
// hardwareProbe.js — per-machine hardware floor.
//
// The REAL backend answers GET /HardwareInformation/v.0.2 with this
// machine's WMI data (proven live on the 1080 Ti backend 2026-09-19 —
// every value a STRING, GPUArchitecture "" on non-NVIDIA GPUs). The
// installer used to generate hardware-floor.json once; this backend now
// does the same job itself at boot:
//   1. data/hardware-floor.json exists (cached probe)  → use it
//   2. win32                                          → WMI probe → cache
//   3. otherwise                                       → generic shape
// HARDWARE_REPROBE=1 forces a fresh probe (config reprobeHardware too).

const { execFile } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

// One PowerShell round-trip gathers everything the floor needs. Fields
// mirror install-host.ps1's generator exactly (proven shapes).
const PS_SCRIPT = [
    "$ErrorActionPreference='SilentlyContinue'",
    "$gpu = Get-CimInstance Win32_VideoController | Where-Object { $_.Name -notmatch 'Parsec|Virtual' } | Select-Object -First 1",
    "$nvgpu = Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' } | Select-Object -First 1",
    "if ($nvgpu) { $gpu = $nvgpu }",   // NVIDIA GPU wins when present (full experience machine)
    "$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name",
    "$bios = (Get-CimInstance Win32_BIOS | Select-Object -First 1).SMBIOSBIOSVersion",
    "$cs = Get-CimInstance Win32_ComputerSystem | Select-Object -First 1",
    "$mem = [string]$cs.TotalPhysicalMemory",
    "$ram = [string]$gpu.AdapterRAM",
    "$res = '{0}x{1}@{2}' -f $gpu.CurrentHorizontalResolution, $gpu.CurrentVerticalResolution, $gpu.CurrentRefreshRate",
    "if ($res -notmatch '\\dx\\d') { $res = '1920x1080@60' }",
    "$did=''; $vid=''; $sub=''; $subv=''",
    "if ($gpu.PNPDeviceID -match 'DEV_([0-9A-F]{4})') { $did=$Matches[1] }",
    "if ($gpu.PNPDeviceID -match 'VEN_([0-9A-F]{4})') { $vid=$Matches[1] }",
    "if ($gpu.PNPDeviceID -match 'SUBSYS_([0-9A-F]{4})([0-9A-F]{4})') { $sub=$Matches[1]; $subv=$Matches[2] }",
    "$tele = ($vid + $did).ToLower() + ('0' * 56)",
    "[pscustomobject]@{",
    "  MoboType='Desktop'; BIOSVersion=[string]$bios",
    "  PhysicalMemoryCapacity=$mem; JarvisDeviceId=($vid+$did)",
    "  TelemetryDeviceId=$tele; UserDefaultUILanguage='1033'",
    "  ProcessorArchitecture='AMD64'; OSVersion='10.0'",
    "  OSBuildNumber=[string][System.Environment]::OSVersion.Version.Build",
    "  TotalPhysicalMemory=$mem; CurrentResolution=$res",
    "  PCName=$env:COMPUTERNAME; CPUName=[string]$cpu",
    "  DriverVersion=[string]$gpu.DriverVersion; IsDCHDriverInstalled='1'",
    "  DriverType='0'; SLISupported='0'; HasActiveSLITopology='0'",
    "  ActiveTopologyGPUCount='0'; IsOptimus='0'",
    "  GPU=@([pscustomobject]@{",
    "    LongGPUName=[string]$gpu.Name; ActualVRAMSize=$ram; GPURAMType=''",
    "    VBIOSVersion=''; IsQuadro='0'; DeviceId=$did; VendorId=$vid",
    "    SubSystemId=$sub; SubVendorId=$subv; SystemType='DESKTOP'",
    "    BrandType='1'; PhysicalGPUHandle=''; GPUArchitecture=''",
    "    GPUArchRevision=''; GPUArchVersion=''; GPUArchImplementation=''",
    "    IsPrimary='1'",
    "  })",
    "} | ConvertTo-Json -Depth 4"
].join('\n');

function probeWindows(logger, cb) {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', PS_SCRIPT],
        { timeout: 25000, windowsHide: true, maxBuffer: 1 << 20 },
        function (err, stdout) {
            if (err) { cb(err); return; }
            try {
                const hw = JSON.parse(stdout);
                if (!hw || !hw.GPU || !hw.GPU.length) throw new Error('probe returned no GPU');
                // every value a string (page contract)
                for (const k of Object.keys(hw)) {
                    if (k !== 'GPU' && typeof hw[k] !== 'string') hw[k] = String(hw[k]);
                }
                cb(null, hw);
            } catch (perr) { cb(perr); }
        });
}

// ensure the floor is ready before (or shortly after) serving requests.
// Never blocks boot: fallback is synchronous, probe result swaps in async.
function ensureHardwareFloor(cfg, logger, defaults) {
    const cachePath = path.join(cfg.dataDir, 'hardware-floor.json');
    const state = { floor: defaults.GENERIC_HARDWARE_FLOOR, source: 'generic' };

    // 1. cached probe
    try {
        const parsed = JSON.parse(fs.readFileSync(cachePath, 'utf8'));
        if (parsed && parsed.GPU) {
            state.floor = parsed; state.source = 'cache';
            logger.info('hardwareFloor: using cached probe (' + cachePath + ')');
        }
    } catch (err) { /* no cache yet */ }

    // 2. fresh probe (win32 only) — async swap-in
    function reprobe() {
        if (process.platform !== 'win32') {
            logger.info('hardwareFloor: non-win32 host — generic shape stays');
            return;
        }
        probeWindows(logger, function (err, hw) {
            if (err) {
                logger.warn('hardwareFloor: WMI probe failed (' + err.message + ') — ' +
                    (state.source === 'cache' ? 'cache kept' : 'generic shape stays'));
                return;
            }
            state.floor = hw; state.source = 'probe';
            try {
                fs.mkdirSync(path.dirname(cachePath), { recursive: true });
                const tmp = cachePath + '.' + process.pid + '.tmp';
                fs.writeFileSync(tmp, JSON.stringify(hw, null, 4));
                fs.renameSync(tmp, cachePath);
                logger.info('hardwareFloor: WMI probe OK — ' + hw.PCName + ' / ' +
                    (hw.GPU[0] && hw.GPU[0].LongGPUName) + ' / ' + hw.CurrentResolution);
            } catch (werr) {
                logger.warn('hardwareFloor: cache write failed: ' + werr.message);
            }
        });
    }

    if (cfg.reprobeHardware || state.source === 'generic') reprobe();
    else {
        // cache present — refresh in background anyway (resolution/monitor
        // changes), low priority: only swap when probe succeeds
        if (process.platform === 'win32') reprobe();
    }

    return {
        get: function () { return state.floor; },
        source: function () { return state.source; },
        cachePath: function () { return cachePath; }
    };
}

module.exports = { ensureHardwareFloor, probeWindows };
