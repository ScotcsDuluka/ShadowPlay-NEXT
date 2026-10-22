$ErrorActionPreference='Stop'
$main='C:\My Project\NVIDIA-Shadowplay'
$review='C:\My Project\_ShadowPlay-Group-Layout-A-Review'
$relative='Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\main-menu-live.css'
$source=Join-Path (Join-Path $main 'Project') $relative
$runtime=Join-Path (Join-Path $main 'build\NVIDIA ShadowPlay') $relative
$fixed=Join-Path (Join-Path $review 'Project') $relative
$backup=Join-Path $main 'build_test\NVIDIA OSC TopBar Restore 20261023'
$destTest=Join-Path $main 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuTopBarV5.test.js'
$fromTest=Join-Path $review 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuTopBarV5.test.js'
if(Test-Path $backup){throw 'Backup already exists; abort'}
foreach($f in @($source,$runtime,$fixed,$fromTest)){if(!(Test-Path $f)){throw ('Missing '+$f)}}
if(Test-Path $destTest){throw 'Refusing to overwrite existing top bar test'}
if((Get-FileHash $source).Hash -ne (Get-FileHash $runtime).Hash){throw 'Source/runtime mismatch, abort'}
if((Get-Content -LiteralPath $source -Raw) -notmatch 'visibility:hidden!important;pointer-events:none!important'){throw 'Unexpected source state'}
New-Item -ItemType Directory -Force -Path $backup|Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $backup 'source-before.css') -ErrorAction Stop
Copy-Item -LiteralPath $runtime -Destination (Join-Path $backup 'runtime-before.css') -ErrorAction Stop
try{
 Copy-Item -LiteralPath $fixed -Destination $source -ErrorAction Stop
 Copy-Item -LiteralPath $fixed -Destination $runtime -ErrorAction Stop
 foreach($p in @($source,$runtime)){if((Get-FileHash $p).Hash -ne (Get-FileHash $fixed).Hash){throw ('Hash mismatch after deploy: '+$p)}}
 Copy-Item -LiteralPath $fromTest -Destination $destTest -ErrorAction Stop
 Write-Output ('DEPLOYED_TOPBAR_CSS_AND_TEST BACKUP='+$backup)
} catch{
 Copy-Item -LiteralPath (Join-Path $backup 'source-before.css') -Destination $source -Force
 Copy-Item -LiteralPath (Join-Path $backup 'runtime-before.css') -Destination $runtime -Force
 throw
}
