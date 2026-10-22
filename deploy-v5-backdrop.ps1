$ErrorActionPreference='Stop'
$main='C:\My Project\NVIDIA-Shadowplay'
$review='C:\My Project\_ShadowPlay-Group-Layout-A-Review'
$rel='Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\main-menu-live.css'
$source=Join-Path (Join-Path $main 'Project') $rel
$runtime=Join-Path (Join-Path $main 'build\NVIDIA ShadowPlay') $rel
$patch=Join-Path (Join-Path $review 'Project') $rel
$backup=Join-Path $main 'build_test\NVIDIA GroupLayout Backdrop Fix 20261022'
if(Test-Path -LiteralPath $backup){throw 'Backup already exists: stop instead of overwriting'}
foreach($p in @($source,$runtime,$patch)){if(!(Test-Path -LiteralPath $p)){throw ('Missing '+$p)}}
if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $runtime).Hash){throw 'Source and runtime already diverged'}
if((Get-Content -LiteralPath $source -Raw) -notmatch '--nv-top-preview-height:380px'){throw 'Unexpected source stylesheet state'}
New-Item -ItemType Directory -Force -Path $backup|Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $backup 'main-menu-live.source.before.css') -ErrorAction Stop
Copy-Item -LiteralPath $runtime -Destination (Join-Path $backup 'main-menu-live.runtime.before.css') -ErrorAction Stop
try {
 Copy-Item -LiteralPath $patch -Destination $source -ErrorAction Stop
 Copy-Item -LiteralPath $patch -Destination $runtime -ErrorAction Stop
 foreach($p in @($source,$runtime)){if((Get-FileHash -LiteralPath $p).Hash -ne (Get-FileHash -LiteralPath $patch).Hash){throw ('Hash mismatch '+$p)}}
 $test=Join-Path $main 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuTopBackdropV5.test.js'
 if(Test-Path -LiteralPath $test){throw 'Existing test file: refusing overwrite'}
 Copy-Item -LiteralPath (Join-Path $review 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuTopBackdropV5.test.js') -Destination $test -ErrorAction Stop
 Write-Output ('BACKDROP_CSS_UPDATED_BACKUP='+$backup)
} catch {
 Copy-Item -LiteralPath (Join-Path $backup 'main-menu-live.source.before.css') -Destination $source -Force
 Copy-Item -LiteralPath (Join-Path $backup 'main-menu-live.runtime.before.css') -Destination $runtime -Force
 throw
}
