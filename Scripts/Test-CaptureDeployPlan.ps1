#requires -Version 5.1
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$CaptureStage,
 [Parameter(Mandatory=$true)][string]$ContainerStage,
 [Parameter(Mandatory=$true)][string]$NodeStage
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest=Get-Content -LiteralPath (Join-Path $CaptureStage 'capture-build-manifest.json') -Raw | ConvertFrom-Json
if(-not $manifest.projectReferencesBuilt -or -not $manifest.nativeQsvPackaged){throw 'source_build_not_validated_with_native_qsv'}
$files=@($manifest.files)
$seen=@{}
foreach($item in $files) {
 $rel=[string]$item.path
 if([string]::IsNullOrWhiteSpace($rel) -or [IO.Path]::IsPathRooted($rel) -or $rel -match '(^|[\\/])\.\.([\\/]|$)' -or $seen.ContainsKey($rel.ToLowerInvariant())){
    throw ('invalid_or_duplicate_manifest_path '+$rel)
 }
 $seen[$rel.ToLowerInvariant()]=$true
 $sourceRoot=[IO.Path]::GetFullPath((Join-Path $CaptureStage 'NVIDIA Capture')).TrimEnd('\')+'\'
 $p=[IO.Path]::GetFullPath((Join-Path $sourceRoot $rel))
 if(-not $p.StartsWith($sourceRoot,[StringComparison]::OrdinalIgnoreCase)){throw ('source_escape '+$rel)}
 if(-not(Test-Path -LiteralPath $p -PathType Leaf)){throw ('staged_file_missing '+$rel)}
 if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $item.sha256){throw ('staged_hash_mismatch '+$rel)}
}
foreach($file in @('FFmpeg\ffmpeg.exe','FFmpeg\ffprobe.exe','DulukaQsvNative.dll','libvpl.dll','libvpl-LICENSE.txt','qsv-native-package.json')){
 if(-not $seen.ContainsKey($file.ToLowerInvariant())){throw ('required_staged_capture_file_missing '+$file)}
}
$container=@('NvContainer.exe','NvContainer.dll','NvContainer.deps.json','NvContainer.runtimeconfig.json',
 'NvGraphics.Contracts.dll','NvGraphics.dll','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll','System.Management.dll')
foreach($name in $container){if(-not(Test-Path -LiteralPath (Join-Path $ContainerStage $name) -PathType Leaf)){throw ('container_stage_missing '+$name)}}
$node=@(Get-CaptureNodeBundleFiles -NodeStage $NodeStage -RuntimeIndex (Join-Path $root 'build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI\index.js'))
Write-Output ('CAPTURE_DEPLOY_PLAN_PASS files='+($files.Count+$container.Count+$node.Count)+' capture='+$files.Count+' container='+$container.Count+' node='+$node.Count+' ffmpeg_in_capture_manifest=true production_modified=false')
