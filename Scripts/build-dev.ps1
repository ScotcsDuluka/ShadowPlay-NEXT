# Build only the active Project tree; the retired Close Project tree is never
# a source or a fallback.
param(
    [string]$Dest = '',
    [switch]$Clean,
    [switch]$NoBuild,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent $PSScriptRoot
$ProjectRoot = Join-Path $Repo 'Project'
$BuildRoot = if ([string]::IsNullOrWhiteSpace($Dest)) {
    Join-Path $Repo 'Build\NVIDIA ShadowPlay'
} else {
    [IO.Path]::GetFullPath($Dest)
}
$ConfigRoot = Join-Path $Repo 'Build\Build-Config'
$LayoutFile = Join-Path $ConfigRoot 'dev-layout.json'
$Configuration = 'Release'
$TargetFramework = 'net10.0-windows10.0.26100.0'
$CefSdk = $env:CEF_ROOT
$pending = New-Object System.Collections.Generic.List[string]
$overlayConfig = Join-Path $BuildRoot 'NVIDIA Config\config.json'
$legacyOverlayConfig = Join-Path $BuildRoot 'Config\config.json'
$preservedOverlayConfig = $null

if (Test-Path -LiteralPath $overlayConfig) {
    $preservedOverlayConfig = Get-Content -LiteralPath $overlayConfig -Raw
} elseif (Test-Path -LiteralPath $legacyOverlayConfig) {
    $preservedOverlayConfig = Get-Content -LiteralPath $legacyOverlayConfig -Raw
}

if (-not (Test-Path -LiteralPath $LayoutFile)) { throw "Layout config missing: $LayoutFile" }
if ([string]::IsNullOrWhiteSpace($CefSdk) -or
    -not (Test-Path -LiteralPath (Join-Path $CefSdk 'Release\libcef.lib'))) {
    throw 'Set CEF_ROOT to the CEF 73 SDK directory before building the active Project tree.'
}

$MSBuild = $null
if (-not $NoBuild) {
    $vswhereCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $vswhereCandidates += Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $vswhereCandidates += Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    $vswhere = $vswhereCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $vswhere) { throw 'vswhere.exe not found; install Visual Studio Build Tools with the C++ workload.' }
    $vcInst = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vcInst) { throw 'The Visual Studio C++ workload is required to build the active CEF owners.' }

    $MSBuild = $env:MSBUILD_EXE
    if ($MSBuild -and -not (Test-Path -LiteralPath $MSBuild)) { throw "MSBUILD_EXE not found: $MSBuild" }
    if (-not $MSBuild) {
        $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
        if ($command) { $MSBuild = $command.Source }
    }
    if (-not $MSBuild) {
        $candidate = Join-Path $vcInst 'MSBuild\Current\Bin\MSBuild.exe'
        if (Test-Path -LiteralPath $candidate) { $MSBuild = $candidate }
    }
    if (-not $MSBuild) { throw 'MSBuild.exe not found.' }
}

$Layout = Get-Content -LiteralPath $LayoutFile -Raw | ConvertFrom-Json

function Ensure-Dir([string]$Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Copy-FileSafe([string]$SourceFile, [string]$DestinationFile) {
    Ensure-Dir (Split-Path -Parent $DestinationFile)
    try {
        Copy-Item -LiteralPath $SourceFile -Destination $DestinationFile -Force
    } catch {
        if ((Test-Path -LiteralPath $DestinationFile) -and
            (Get-FileHash -LiteralPath $SourceFile -Algorithm SHA256).Hash -eq
            (Get-FileHash -LiteralPath $DestinationFile -Algorithm SHA256).Hash) {
            Write-Host "Unchanged staged file is in use; keeping it: $DestinationFile" -ForegroundColor DarkGray
        } else {
            throw
        }
    }
}

function Copy-Tree([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Required Project tree missing: $Source" }
    Ensure-Dir $Destination
    robocopy $Source $Destination /E /R:1 /W:1 /NJH /NJS /NP /NDL /NFL /NS /NC | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "robocopy failed: $Source -> $Destination (exit $LASTEXITCODE)" }
}

function Copy-RuntimeFiles([string]$Source, [string]$Destination, [string[]]$ExcludeNames = @()) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Required Project build output missing: $Source" }
    Ensure-Dir $Destination
    Get-ChildItem -LiteralPath $Source -Recurse -File -Force |
        Where-Object {
            $_.Extension -in '.exe','.dll','.json' -and
            $_.FullName -notmatch '\\(obj|Debug)\\' -and
            $ExcludeNames -notcontains $_.Name
        } |
        ForEach-Object {
            $sourceFile = $_.FullName
            $relative = $sourceFile.Substring($Source.Length).TrimStart('\')
            $target = Join-Path $Destination $relative
            Copy-FileSafe $sourceFile $target
        }
}

function Invoke-ProjectBuild([string]$ProjectFile, [string]$Platform) {
    if (-not (Test-Path -LiteralPath $ProjectFile)) { throw "Active Project owner missing: $ProjectFile" }
    $extension = [IO.Path]::GetExtension($ProjectFile)
    if ($extension -in '.csproj','.vbproj') {
        & $MSBuild $ProjectFile /t:Restore "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:CEF_ROOT=$CefSdk" /m:1 /nr:false /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "RESTORE FAILED: $ProjectFile (exit $LASTEXITCODE)" }
    }
    & $MSBuild $ProjectFile /t:Build "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:CEF_ROOT=$CefSdk" /m:1 /nr:false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "BUILD FAILED: $ProjectFile (exit $LASTEXITCODE)" }
}

if ($Clean -and (Test-Path -LiteralPath $BuildRoot)) {
    Remove-Item -LiteralPath $BuildRoot -Recurse -Force
}

$activeProjects = @(
    @{ Path = 'Overlay On-Screen Control\NVIDIA OSC\NVIDIA OSC.vcxproj'; Platform = 'x64' },
    @{ Path = 'NVIDIA Adapter\OSC Adapter\Duluka Hook\NVIDIA OSC Adapter.vcxproj'; Platform = 'x64' },
    @{ Path = 'NVIDIA Adapter\OSC Adapter\Duluka Hook\NVIDIA OSC Adapter.vcxproj'; Platform = 'Win32' },
    @{ Path = 'NVIDIA Adapter\Ansel Adapter\ReShade Ansel\NVIDIA Ansel Adapter.vcxproj'; Platform = 'x64' },
    @{ Path = 'NVIDIA Adapter\OSC Adapter\NVIDIA OSC Adapter Host.csproj'; Platform = 'x64' },
    @{ Path = 'NVIDIA Adapter\OSC Adapter\NVIDIA OSC Adapter Host.csproj'; Platform = 'x86' },
    @{ Path = 'Overlay On-Screen Control\NVIDIA NodeAPI\NVIDIA Web Helper\NVIDIA Web Helper.vbproj'; Platform = 'x64' },
    @{ Path = 'NVIDIA Container\NvContainer.csproj'; Platform = 'AnyCPU' },
    @{ Path = 'NVIDIA Capture\NVIDIA Capture.vbproj'; Platform = 'AnyCPU' },
    @{ Path = 'NVIDIA Graphics\NvGraphics.App\NvGraphics.csproj'; Platform = 'AnyCPU' },
    @{ Path = 'NVIDIA Container\NVIDIA ShadowPlay Helper\nvsphelper.csproj'; Platform = 'AnyCPU' },
    @{ Path = 'Launcher.Cef\Launcher.vcxproj'; Platform = 'x64' },
    @{ Path = 'Launcher.Cef\Launcher Bootstrap.vcxproj'; Platform = 'x64' }
)
Write-Host '=== ACTIVE PROJECT BUILD ===' -ForegroundColor Cyan
Write-Host "Output  : $BuildRoot"
Write-Host "CEF SDK : $CefSdk"
if ($NoBuild) {
    Write-Host 'Build   : skipped; staging existing Project outputs'
} else {
    foreach ($project in $activeProjects) {
        Invoke-ProjectBuild (Join-Path $ProjectRoot $project.Path) $project.Platform
    }
}

$cefOutput = Join-Path $BuildRoot 'NVIDIA Launcher\Cef'
$legacyLauncherOutput = Join-Path $BuildRoot 'NvOverlay\Cef'
if ((Test-Path -LiteralPath $legacyLauncherOutput) -and
    -not (Test-Path -LiteralPath $cefOutput)) {
    Ensure-Dir (Split-Path -Parent $cefOutput)
    Move-Item -LiteralPath $legacyLauncherOutput -Destination $cefOutput
}

foreach ($relative in $Layout.rootDirectories) {
    Ensure-Dir (Join-Path $BuildRoot ($relative -replace '/', '\'))
}
Ensure-Dir (Join-Path $BuildRoot 'Logs')

# Stage the NodeAPI runtime as a paired owner, without copying its developer
# Logs folder or the nested host project's source/build tree.
$nodeApiSource = Join-Path $ProjectRoot 'Overlay On-Screen Control\NVIDIA NodeAPI'
$nodeApiOutput = Join-Path $BuildRoot 'Overlay On-Screen Control\NVIDIA NodeAPI'
$nodeHostOutput = Join-Path $nodeApiSource ("NVIDIA Web Helper\bin\x64\Release\{0}" -f $TargetFramework)
Copy-RuntimeFiles $nodeHostOutput $nodeApiOutput
Get-ChildItem -LiteralPath $nodeApiSource -File -Force |
    Where-Object {
        $_.Extension -in '.js','.json','.node','.dll','.exe','.dat' -and
        $_.Name -notlike 'NVIDIA Web Helper.*'
    } |
    ForEach-Object {
        Copy-FileSafe $_.FullName (Join-Path $nodeApiOutput $_.Name)
    }
foreach ($directory in @('node_modules', 'osc', 'shims')) {
    Copy-Tree (Join-Path $nodeApiSource $directory) (Join-Path $nodeApiOutput $directory)
}

# Service configuration is rooted beside NvContainer; mutable user settings
# are preserved when already present.
$nvConfigOutput = Join-Path $BuildRoot 'NVIDIA Config'
Ensure-Dir $nvConfigOutput
Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'NVIDIA Config') -File -Filter '*.json' |
    ForEach-Object {
        $destination = Join-Path $nvConfigOutput $_.Name
        if ($_.Name -ieq 'nvcontainer.json') {
            if (Test-Path -LiteralPath $destination) {
                # Existing runtime may still be using a legacy pairing key.
                # Source templates must never overwrite its active configuration.
                Write-Output '[config] preserving existing nvcontainer.json (pairing migration is separate)'
                return
            }
            $protectedPairing = Join-Path $env:LOCALAPPDATA 'ShadowPlay NEXT\Secrets\pairing-current-user.dpapi'
            if (-not (Test-Path -LiteralPath $protectedPairing)) {
                throw 'New NvContainer deployment requires pre-enrolled per-user protected pairing secret'
            }
        }
        Copy-FileSafe $_.FullName $destination
    }
$captureFlag = Join-Path $ProjectRoot 'NVIDIA Config\capture-disabled.flag'
if (Test-Path -LiteralPath $captureFlag) {
    Copy-FileSafe $captureFlag (Join-Path $nvConfigOutput 'capture-disabled.flag')
}
# Never copy the checked-in NvNode init *example* onto a runtime file.
# The real init is written by NvContainer from the resolved pairing identity.
$nvNodeSrc = Join-Path $ProjectRoot 'NVIDIA Config\nvnode'
$nvNodeDst = Join-Path $nvConfigOutput 'nvnode'
Ensure-Dir $nvNodeDst
robocopy $nvNodeSrc $nvNodeDst /E /XF nvnode-init-duluka.json /R:1 /W:1 /NJH /NJS /NP /NDL /NFL /NS /NC | Out-Null
if ($LASTEXITCODE -gt 7) { throw 'NvNode config copy failed' }

if ($preservedOverlayConfig) {
    [System.IO.File]::WriteAllText(
        $overlayConfig,
        $preservedOverlayConfig,
        [System.Text.UTF8Encoding]::new($false))
}
if (-not (Test-Path -LiteralPath $overlayConfig)) {
    [System.IO.File]::WriteAllText(
        $overlayConfig,
        '{ "Overlay": { "UseOverlayEnabled": true, "EngineOverlayMode": true } }',
        [System.Text.UTF8Encoding]::new($false))
}
if (Test-Path -LiteralPath $legacyOverlayConfig) {
    Remove-Item -LiteralPath $legacyOverlayConfig -Force
}

# Stage the controller and its hotkey helper from their active Project owners.
$containerOutput = Join-Path $BuildRoot 'NVIDIA Container'
$containerSource = Join-Path $ProjectRoot ("NVIDIA Container\bin\Release\{0}" -f $TargetFramework)
$nativeCoreFiles = @('NvContainer.exe','NvContainer.dll','NvContainer.deps.json','NvContainer.runtimeconfig.json')
# Install the supervisor as a verified four-file set, not partial EXE/DLL
# overwrites. When the running supervisor owns these files, stage automatically
# and report a pending activation; no game or OSC process is force-stopped.
Copy-RuntimeFiles $containerSource $containerOutput $nativeCoreFiles
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Install-NvContainerBuild.ps1') -SourceDirectory $containerSource -BuildDirectory $containerOutput
if ($LASTEXITCODE -eq 2) {
    $pending.Add('NvContainer native update staged; install after its normal shutdown.')
} elseif ($LASTEXITCODE -ne 0) {
    throw 'NvContainer verified installation failed.'
}
Copy-RuntimeFiles (Join-Path $ProjectRoot ("NVIDIA Container\NVIDIA ShadowPlay Helper\bin\Release\{0}" -f $TargetFramework)) $containerOutput
$captureOutput = Join-Path $BuildRoot 'NVIDIA Capture'
Copy-RuntimeFiles (Join-Path $ProjectRoot ("NVIDIA Capture\bin\Release\{0}" -f $TargetFramework)) $captureOutput
$ffmpegSource = Join-Path $ProjectRoot 'FFmpeg'
$ffmpegOutput = Join-Path $captureOutput 'FFmpeg'
Ensure-Dir $ffmpegOutput
foreach ($ffmpegName in @('ffmpeg.exe', 'ffprobe.exe')) {
    Copy-FileSafe (Join-Path $ffmpegSource $ffmpegName) (Join-Path $ffmpegOutput $ffmpegName)
}
Get-ChildItem -LiteralPath $ffmpegSource -File -Filter '*.dll' | ForEach-Object {
    Copy-FileSafe $_.FullName (Join-Path $ffmpegOutput $_.Name)
}
$graphicsBuildOutput = Join-Path $Repo 'Build\NVIDIA ShadowPlay\NVIDIA Graphics'
$graphicsOutput = Join-Path $BuildRoot 'NVIDIA Graphics'
if ([IO.Path]::GetFullPath($graphicsBuildOutput) -ne [IO.Path]::GetFullPath($graphicsOutput)) {
    Copy-RuntimeFiles $graphicsBuildOutput $graphicsOutput
}
$containerGraphicsExe = Join-Path $containerOutput 'NvGraphics.exe'
if (Test-Path -LiteralPath $containerGraphicsExe) {
    Remove-Item -LiteralPath $containerGraphicsExe -Force
}

# The x64 host routes x86 games to the matching host/DLL in the x86 subfolder.
# Keep each host and native module together.
$adapterOutput = Join-Path $BuildRoot 'NVIDIA Adapter'
$adapterSource = Join-Path $ProjectRoot 'NVIDIA Adapter\OSC Adapter'
$hookEngineSource = Join-Path $adapterSource 'Duluka Hook'
$adapterHostOutput = Join-Path $adapterSource (
    "bin\x64\Release\{0}\win-x64" -f $TargetFramework)
$adapterNativeDll = Join-Path $hookEngineSource 'bin\x64\Release\NVIDIA OSC Adapter Hook.dll'
Copy-RuntimeFiles $adapterHostOutput $adapterOutput
$adapterX86Output = Join-Path $adapterOutput 'x86'
$adapterX86HostOutput = Join-Path $adapterSource (
    "bin\x86\Release\{0}\win-x86" -f $TargetFramework)
Copy-RuntimeFiles $adapterX86HostOutput $adapterX86Output
Copy-FileSafe (Join-Path $hookEngineSource 'bin\Win32\Release\NVIDIA OSC Adapter Hook.dll') (Join-Path $adapterX86Output 'NVIDIA OSC Adapter Hook.dll')
Copy-FileSafe $adapterNativeDll (Join-Path $adapterOutput (Split-Path -Leaf $adapterNativeDll))
Copy-FileSafe (Join-Path $ProjectRoot 'NVIDIA Adapter\Ansel Adapter\ReShade Ansel\bin\x64\Release\NVIDIA Ansel Adapter.dll') (Join-Path $adapterOutput 'NVIDIA Ansel Adapter.dll')

# Launcher host/UI slot; CEF runtime is shared from the OSC slot below.
$launcherOutput = Join-Path $ProjectRoot 'Launcher.Cef\bin\x64\Release'
Copy-FileSafe (Join-Path $launcherOutput 'Launcher.exe') (Join-Path $BuildRoot 'Launcher.exe')
Copy-FileSafe (Join-Path $launcherOutput 'Launcher.dll') (Join-Path $cefOutput 'Launcher.dll')
Copy-Tree (Join-Path $ProjectRoot 'Launcher.Cef\ui') (Join-Path $cefOutput 'Resources\launcher')

# OSC owns the single shared CEF runtime directory used by both hosts.
$oscOutput = Join-Path $BuildRoot 'Overlay On-Screen Control\NVIDIA OSC'
Copy-RuntimeFiles (Join-Path $ProjectRoot 'Overlay On-Screen Control\NVIDIA OSC\bin\x64\Release') $oscOutput
$cefRuntimeFiles = @(
    @{ Source = 'Release\libcef.dll'; Destination = 'libcef.dll' },
    @{ Source = 'Release\chrome_elf.dll'; Destination = 'chrome_elf.dll' },
    @{ Source = 'Release\d3dcompiler_47.dll'; Destination = 'd3dcompiler_47.dll' },
    @{ Source = 'Release\d3dcompiler_43.dll'; Destination = 'd3dcompiler_43.dll' },
    @{ Source = 'Release\libEGL.dll'; Destination = 'libEGL.dll' },
    @{ Source = 'Release\libGLESv2.dll'; Destination = 'libGLESv2.dll' },
    @{ Source = 'Release\natives_blob.bin'; Destination = 'natives_blob.bin' },
    @{ Source = 'Release\snapshot_blob.bin'; Destination = 'snapshot_blob.bin' },
    @{ Source = 'Release\v8_context_snapshot.bin'; Destination = 'v8_context_snapshot.bin' },
    @{ Source = 'Resources\cef.pak'; Destination = 'cef.pak' },
    @{ Source = 'Resources\cef_100_percent.pak'; Destination = 'cef_100_percent.pak' },
    @{ Source = 'Resources\cef_200_percent.pak'; Destination = 'cef_200_percent.pak' },
    @{ Source = 'Resources\cef_extensions.pak'; Destination = 'cef_extensions.pak' },
    @{ Source = 'Resources\devtools_resources.pak'; Destination = 'devtools_resources.pak' },
    @{ Source = 'Resources\icudtl.dat'; Destination = 'icudtl.dat' }
)
foreach ($file in $cefRuntimeFiles) {
    $source = Join-Path $CefSdk $file.Source
    if (-not (Test-Path -LiteralPath $source)) { throw "CEF runtime dependency missing: $source" }
    Copy-FileSafe $source (Join-Path $oscOutput $file.Destination)
}
Copy-Tree (Join-Path $CefSdk 'Resources\locales') (Join-Path $oscOutput 'locales')
Copy-Tree (Join-Path $CefSdk 'Release\swiftshader') (Join-Path $oscOutput 'swiftshader')

# Remove only stale CEF runtime copies from the Launcher slot. Keep
# Launcher UI, profile data, and legacy NVIDIA Share assets untouched.
$legacyCefRuntimeItems = @(
    'libcef.dll', 'chrome_elf.dll', 'd3dcompiler_47.dll',
    'd3dcompiler_43.dll', 'libEGL.dll', 'libGLESv2.dll',
    'natives_blob.bin', 'snapshot_blob.bin', 'v8_context_snapshot.bin',
    'cef.pak', 'cef_100_percent.pak', 'cef_200_percent.pak',
    'cef_extensions.pak', 'devtools_resources.pak', 'icudtl.dat',
    'locales', 'swiftshader'
)
foreach ($item in $legacyCefRuntimeItems) {
    $legacyPath = Join-Path $cefOutput $item
    if (Test-Path -LiteralPath $legacyPath) {
        try {
            Remove-Item -LiteralPath $legacyPath -Recurse -Force -ErrorAction Stop
        } catch {
            $pending.Add("stale Launcher CEF runtime still in use: $legacyPath")
            Write-Host "PENDING: stale Launcher CEF runtime still in use: $legacyPath" -ForegroundColor Yellow
        }
    }
}

$manifest = [ordered]@{
    buildUtc = [DateTime]::UtcNow.ToString('o')
    configuration = $Configuration
    sourceRoot = 'Project'
    projects = @($activeProjects | ForEach-Object { $_.Path })
    output = $BuildRoot
    layout = 'Build/Build-Config/dev-layout.json'
    retiredSourceExcluded = 'Project/Close Project'
    pendingArtifacts = @($pending)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ConfigRoot 'dev-build.json') -Encoding utf8

if ($pending.Count -gt 0) {
    Write-Host "DEV BUILD STAGED WITH PENDING ARTIFACTS: $($pending.Count)" -ForegroundColor Yellow
    $pending | ForEach-Object { Write-Host "  PENDING: $_" -ForegroundColor Yellow }
    if ($Strict) { throw 'Strict build requested and pending artifacts remain.' }
} else {
    Write-Host 'ACTIVE PROJECT BUILD TREE STAGED.' -ForegroundColor Green
}
