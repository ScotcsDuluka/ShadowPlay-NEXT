$ErrorActionPreference = 'Stop'
$main = 'C:\My Project\NVIDIA-Shadowplay'
$review = 'C:\My Project\_ShadowPlay-Group-Layout-A-Review'
$runtime = Join-Path $main 'build\NVIDIA ShadowPlay'
$rel = 'Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods'
$backup = Join-Path $main 'build_test\NVIDIA GroupLayout TopPreview Fix 20261022'
$names = @('main-menu-live.css', 'main-menu-unified-native.js',
  'main-menu-group-model.js', 'main-menu-group-editor.js', 'main-menu-group-editor.css')
if(Test-Path -LiteralPath $backup){throw 'Backup exists; refusing to replace'}
foreach($n in $names){
  $source = Join-Path (Join-Path $main 'Project') (Join-Path $rel $n)
  $built = Join-Path $runtime (Join-Path $rel $n)
  $patched = Join-Path (Join-Path $review 'Project') (Join-Path $rel $n)
  if(!(Test-Path $source) -or !(Test-Path $built) -or !(Test-Path $patched)){throw ('Missing required file '+$n)}
  if((Get-FileHash $source).Hash -ne (Get-FileHash $built).Hash){throw ('Source/runtime mismatch '+$n)}
}
New-Item -ItemType Directory -Force -Path (Join-Path $backup 'source'),(Join-Path $backup 'runtime') | Out-Null
foreach($n in $names){
  Copy-Item -LiteralPath (Join-Path (Join-Path $main 'Project') (Join-Path $rel $n)) -Destination (Join-Path $backup ('source\'+$n)) -ErrorAction Stop
  Copy-Item -LiteralPath (Join-Path $runtime (Join-Path $rel $n)) -Destination (Join-Path $backup ('runtime\'+$n)) -ErrorAction Stop
}
try {
 foreach($n in $names){
  $patched = Join-Path (Join-Path $review 'Project') (Join-Path $rel $n)
  $source = Join-Path (Join-Path $main 'Project') (Join-Path $rel $n)
  $built = Join-Path $runtime (Join-Path $rel $n)
  Copy-Item -LiteralPath $patched -Destination $source -ErrorAction Stop
  Copy-Item -LiteralPath $patched -Destination $built -ErrorAction Stop
  if((Get-FileHash $source).Hash -ne (Get-FileHash $patched).Hash -or (Get-FileHash $built).Hash -ne (Get-FileHash $patched).Hash){
    throw ('Post-deploy integrity mismatch: '+$n)
  }
  Write-Output ('UPDATED '+$n)
 }
 $t='Overlay On-Screen Control\NVIDIA NodeAPI\tests'
 foreach($name in @('mainMenuGroupModelV5.test.js','mainMenuGroupEditorV5.test.js','mainMenuTopPreviewV5.test.js')){
  $from=Join-Path (Join-Path $review 'Project') (Join-Path $t $name)
  $to=Join-Path (Join-Path $main 'Project') (Join-Path $t $name)
  if(Test-Path -LiteralPath $to){throw ('Test already exists: '+$name)}
  Copy-Item -LiteralPath $from -Destination $to -ErrorAction Stop
  Write-Output ('TEST '+$name)
 }
 Write-Output ('DEPLOY_COMPLETE_BACKUP='+$backup)
} catch {
 foreach($n in $names){
  Copy-Item -LiteralPath (Join-Path $backup ('source\'+$n)) -Destination (Join-Path (Join-Path $main 'Project') (Join-Path $rel $n)) -Force
  Copy-Item -LiteralPath (Join-Path $backup ('runtime\'+$n)) -Destination (Join-Path $runtime (Join-Path $rel $n)) -Force
 }
 throw
}
