$ErrorActionPreference='Stop'
$main='C:\My Project\NVIDIA-Shadowplay'
$review='C:\My Project\_ShadowPlay-Group-Layout-A-Review'
$relative='Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\main-menu-group-editor.css'
$source=Join-Path (Join-Path $main 'Project') $relative
$runtime=Join-Path (Join-Path $main 'build\NVIDIA ShadowPlay') $relative
$patch=Join-Path (Join-Path $review 'Project') $relative
$backup=Join-Path $main 'build_test\NVIDIA OSC Customize Overflow Fix 20261023'
$testSource=Join-Path $review 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuV5Overflow.test.js'
$testDest=Join-Path $main 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuV5Overflow.test.js'
foreach($f in @($source,$runtime,$patch,$testSource)){if(!(Test-Path -LiteralPath $f)){throw ('Missing '+$f)}}
if(Test-Path -LiteralPath $backup){throw ('Backup already exists '+$backup)}
if(Test-Path -LiteralPath $testDest){throw ('Test already exists '+$testDest)}
if((Get-FileHash $source).Hash -ne (Get-FileHash $runtime).Hash){throw 'Existing Source and runtime mismatch'}
if((Get-Content -LiteralPath $source -Raw) -notmatch '\.nv-g-panel\{min-width:0'){throw 'Unexpected Source Group Editor CSS'}
New-Item -ItemType Directory -Force -Path $backup|Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $backup 'source-before.css') -ErrorAction Stop
Copy-Item -LiteralPath $runtime -Destination (Join-Path $backup 'runtime-before.css') -ErrorAction Stop
try {
 Copy-Item -LiteralPath $patch -Destination $source -ErrorAction Stop
 Copy-Item -LiteralPath $patch -Destination $runtime -ErrorAction Stop
 if((Get-FileHash $patch).Hash -ne (Get-FileHash $source).Hash -or (Get-FileHash $patch).Hash -ne (Get-FileHash $runtime).Hash){throw 'Post-deploy hash verification failed'}
 Copy-Item -LiteralPath $testSource -Destination $testDest -ErrorAction Stop
 Write-Output ('OVERFLOW_DEPLOYED backup='+$backup)
} catch {
 Copy-Item -LiteralPath (Join-Path $backup 'source-before.css') -Destination $source -Force
 Copy-Item -LiteralPath (Join-Path $backup 'runtime-before.css') -Destination $runtime -Force
 if(Test-Path $testDest){Remove-Item -LiteralPath $testDest -Force}
 throw
}
