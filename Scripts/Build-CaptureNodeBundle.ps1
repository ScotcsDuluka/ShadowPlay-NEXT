#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Dest,
      [string]$BaseIndex = '')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source=Join-Path $repo 'Project\Overlay On-Screen Control\NVIDIA NodeAPI'
if (-not $BaseIndex) { $BaseIndex=Join-Path $repo 'build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI\index.js' }
$BaseIndex=[IO.Path]::GetFullPath($BaseIndex)
$destPath=[IO.Path]::GetFullPath($Dest)
if (Test-Path -LiteralPath $destPath) { throw 'capture_node_bundle_destination_exists' }
$baseHash=(Get-FileHash -LiteralPath $BaseIndex -Algorithm SHA256).Hash
$base=Get-Content -LiteralPath $BaseIndex -Raw
if ((Get-FileHash -LiteralPath $BaseIndex -Algorithm SHA256).Hash -ne $baseHash) {throw 'capture_node_bundle_base_index_changed_during_read'}
$anchor="require('./shims/route-floor.js')(app, io);"
if (@([regex]::Matches($base,[regex]::Escape($anchor))).Count -ne 1 -or $base -match 'capture-runtime-status\.js') {
    throw 'capture_node_bundle_base_registration_ambiguous'
}
$addition=@'
// Capture routes use the current guarded gateway; other existing routes remain unchanged.
app.use('/DulukaCapture', require('./localApiSecurity.js').httpMiddleware);
require('./shims/capture-engine-status.js')(app);
require('./shims/capture-capabilities.js')(app);
require('./shims/capture-runtime-status.js')(app);
'@
$generated=$base.Replace($anchor,$addition + "`r`n" + $anchor)
$files=@('index.js','localApiSecurity.js','localGpuRequestGuard.js',
 'shims\capture-engine-status.js','shims\capture-capabilities.js',
 'shims\capture-runtime-status.js','shims\capture-display-routing.js')
New-Item -ItemType Directory -Path $destPath | Out-Null
foreach($file in $files) {
    $target=Join-Path $destPath $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    if($file -eq 'index.js') {[IO.File]::WriteAllText($target,$generated,[Text.UTF8Encoding]::new($false))}
    else {Copy-Item -LiteralPath (Join-Path $source $file) -Destination $target}
}
if ((Get-FileHash -LiteralPath $BaseIndex -Algorithm SHA256).Hash -ne $baseHash) {throw 'capture_node_bundle_base_index_changed_during_build'}
$manifest=[ordered]@{schemaVersion=1; baseIndexSha256=$baseHash;
  files=@(foreach($file in $files){[ordered]@{path=$file;sha256=(Get-FileHash -LiteralPath (Join-Path $destPath $file) -Algorithm SHA256).Hash}})}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destPath 'capture-node-manifest.json') -Encoding utf8
Write-Output ('CAPTURE_NODE_BUNDLE_STAGED files='+$files.Count+' destination='+$destPath+' deployed=false')
