#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Dest = '',
    [switch]$NoBuild,
    [string]$QsvNativePackageDirectory = '',
    [switch]$RequireNativeQsv
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$repo = Split-Path -Parent $PSScriptRoot
$capture = Join-Path $repo 'Project\NVIDIA Capture'
$project = Join-Path $capture 'NVIDIA Capture.vbproj'
$native = Join-Path $capture 'EngineCapture\Duluka Capture\Encoding\Intel\Native'
$buildRoot = if ($Dest) { [IO.Path]::GetFullPath($Dest) } else { Join-Path $repo 'build_test\NVIDIA Capture Unified_Test' }
$output = Join-Path $buildRoot 'NVIDIA Capture'
$manifestPath = Join-Path $buildRoot 'capture-build-manifest.json'
$package = if ($QsvNativePackageDirectory) { [IO.Path]::GetFullPath($QsvNativePackageDirectory) } else { Join-Path $native 'bin\package' }
$required = ([bool]$RequireNativeQsv).ToString().ToLowerInvariant()
& (Join-Path $native 'Test-NativeQsvPackage.ps1') -PackageDirectory $package -Required $required -ProbeAbi
$nativePresent = Test-Path -LiteralPath (Join-Path $package 'qsv-native-package.json')

function Get-SourceDigest {
    $files = @(Get-ChildItem -LiteralPath $capture -Recurse -File | Where-Object {
        $_.Extension -in '.vb','.cs','.vbproj','.csproj','.ps1','.cpp','.txt' -and
        $_.FullName -notmatch '\\(bin|obj|packages|package|Tests|[^\\]*\.Tests)\\'
    })
    $files += @(Get-Item -LiteralPath (Join-Path $repo 'Common\AppLayout.vb'),(Join-Path $repo 'Common\AppLayoutStartup.vb'),(Join-Path $repo 'Common\FFmpegLocator.vb'),$PSCommandPath,(Join-Path $PSScriptRoot 'build-dev.ps1'))
    $files += @(Get-ChildItem -LiteralPath $repo -File | Where-Object { $_.Name -like 'Directory.Build.*' })
    $files += @(Get-ChildItem -LiteralPath (Join-Path $repo 'Project\FFmpeg') -File | Where-Object { $_.Name -in 'ffmpeg.exe','ffprobe.exe' -or $_.Extension -eq '.dll' })
    # Stable's text files can be checked out as LF or CRLF on different Windows
    # machines. Hash canonical text bytes so equivalent source revisions get the
    # same input fingerprint; keep actual exe/dll payload bytes authoritative.
    $lines = @($files | Sort-Object FullName | ForEach-Object {
        $source = $_
        if ($source.Extension -in '.exe', '.dll') {
            $digest = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
        } else {
            $content = [IO.File]::ReadAllText($source.FullName)
            $canonical = $content.Replace("`r`n", "`n").Replace("`r", "`n")
            $fileSha = [Security.Cryptography.SHA256]::Create()
            try { $digest = ([BitConverter]::ToString($fileSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
            finally { $fileSha.Dispose() }
        }
        $source.FullName.Substring($repo.Length) + ':' + $digest
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))).Replace('-','') }
    finally { $sha.Dispose() }
}
function Get-StagedFiles {
    return @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($output.Length).TrimStart('\'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; bytes=$_.Length}
    })
}
$sourceDigest = Get-SourceDigest
if ($NoBuild) {
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'capture_no_build_requires_verified_manifest: first run without -NoBuild' }
    $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($previous.sourceSha256 -ne $sourceDigest) { throw 'capture_no_build_stale_source: build current ProjectReference sources first' }
    $expected = @($previous.files | ForEach-Object { $_.path + ':' + $_.sha256 })
    $actual = @(Get-StagedFiles | ForEach-Object { $_.path + ':' + $_.sha256 })
    if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'capture_no_build_staged_hash_mismatch' }
    if ([bool]$previous.nativeQsvPackaged -ne [bool]$nativePresent) { throw 'capture_no_build_native_package_changed' }
    if ($nativePresent -and $previous.nativePackageManifestSha256 -ne (Get-FileHash -LiteralPath (Join-Path $package 'qsv-native-package.json') -Algorithm SHA256).Hash) {
        throw 'capture_no_build_native_package_changed'
    }
    Write-Output ('CAPTURE_UNIFIED_VERIFIED existing=true output=' + $output)
    return
}
# An isolated artifacts root prevents stale bin/Release DLLs and stale optional
# native content from surviving a previous build with different dependencies.
$buildId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff',[Globalization.CultureInfo]::InvariantCulture) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts = Join-Path $buildRoot ('.capture-build\' + $buildId)
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$log = Join-Path $artifacts 'release-build.log'
$buildArgs = @('build',$project,'-c','Release','--artifacts-path',$artifacts,'--nologo','-m:1','-v:minimal','-p:NuGetAudit=false',
    ('-p:QsvNativePackageDirectory=' + $package),('-p:QsvNativeRequired=' + $required))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'capture_build_dotnet_sdk_not_found' }
$originalPath = $env:PATH
try {
    # MCP hosts may have a minimal inherited PATH. Keep child-build provenance
    # stamping functional without changing the machine/user environment.
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        $gitDirectory = Join-Path $env:ProgramFiles 'Git\cmd'
        if (Test-Path -LiteralPath (Join-Path $gitDirectory 'git.exe')) { $env:PATH = $gitDirectory + ';' + $env:PATH }
    }
    & $dotnet @buildArgs *> $log
    $buildExit = $LASTEXITCODE
} finally { $env:PATH = $originalPath }
if ($buildExit -ne 0) { Get-Content -LiteralPath $log -Tail 30; throw ('capture_unified_release_build_failed: ' + $log) }
Get-Content -LiteralPath $log -Tail 6
if ((Get-SourceDigest) -ne $sourceDigest) { throw 'capture_source_changed_during_build: source preserved; retry after concurrent edits finish' }
$app = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin') -Recurse -File -Filter 'NvCapture.deps.json')
if ($app.Count -ne 1) { throw 'capture_unified_output_ambiguous' }
$builtOutput = $app[0].DirectoryName
$requiredFiles = @('NvCapture.exe','NvCapture.dll','NvCapture.deps.json','NvCapture.runtimeconfig.json','DulukaEngine.Recording.dll',
    'DulukaEngine.Encoder.Nvenc.dll','DulukaEngine.Encoder.Qsv.dll','DulukaEngine.Video.Ddagrab.dll','DulukaEngine.Media.dll',
    'DulukaEngine.Audio.dll','DulukaEngine.Audio.Wasapi.dll')
$deps = Get-Content -LiteralPath $app[0].FullName -Raw | ConvertFrom-Json
foreach ($target in $deps.targets.PSObject.Properties) {
    foreach ($library in $target.Value.PSObject.Properties) {
        $runtime = $library.Value.PSObject.Properties['runtime']
        if ($runtime) { foreach ($asset in $runtime.Value.PSObject.Properties) { $requiredFiles += Split-Path -Leaf $asset.Name } }
    }
}
foreach ($name in $requiredFiles | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath (Join-Path $builtOutput $name) -PathType Leaf)) { throw ('capture_runtime_dependency_missing: ' + $name) }
}
if ($nativePresent) {
    & (Join-Path $native 'Test-NativeQsvPackage.ps1') -PackageDirectory $builtOutput -Required true -ProbeAbi
} elseif (Test-Path -LiteralPath (Join-Path $builtOutput 'DulukaQsvNative.dll')) { throw 'capture_unexpected_native_qsv_artifact' }
# Reusing a staging folder is permitted only when its immutable file set agrees.
# Changed artifacts must go to a new -Dest, then the deployment owner can review.
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Recurse -File).Count -gt 0) { throw 'capture_staging_destination_not_empty: choose a new -Dest; existing artifacts preserved' }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
Get-ChildItem -LiteralPath $builtOutput -File | Where-Object { $_.Extension -in '.exe','.dll','.json','.txt' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output $_.Name) }
$ffmpegOutput = Join-Path $output 'FFmpeg'
New-Item -ItemType Directory -Force -Path $ffmpegOutput | Out-Null
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) { Copy-Item -LiteralPath (Join-Path $repo ('Project\FFmpeg\' + $name)) -Destination (Join-Path $ffmpegOutput $name) }
Get-ChildItem -LiteralPath (Join-Path $repo 'Project\FFmpeg') -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $ffmpegOutput $_.Name) }
$manifest = [ordered]@{
    schemaVersion=1; buildUtc=[DateTime]::UtcNow.ToString('o'); configuration='Release'; sourceRoot='Project';
    sourceSha256=$sourceDigest; project='Project/NVIDIA Capture/NVIDIA Capture.vbproj'; projectReferencesBuilt=$true;
    artifactsDirectory=$artifacts; buildLog=$log; output=$output; nativeQsvPackaged=[bool]$nativePresent;
    nativePackageManifestSha256=$(if ($nativePresent) { (Get-FileHash -LiteralPath (Join-Path $package 'qsv-native-package.json') -Algorithm SHA256).Hash } else { $null });
    hardwareVerified=$false; mediaVerified=$false; deployed=$false; files=(Get-StagedFiles)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Output ('CAPTURE_UNIFIED_BUILD_PASS output=' + $output + ' native_qsv_packaged=' + [bool]$nativePresent + ' hardware_verified=false deployed=false')
