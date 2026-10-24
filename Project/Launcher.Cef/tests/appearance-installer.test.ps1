# Isolated .spmod installer regression. No live Launcher config/assets.
$ErrorActionPreference='Stop'
$base = Split-Path -Parent $PSScriptRoot
$creator = Join-Path $base 'Modules\new-appearance-package.ps1'
$installer = Join-Path $base 'Modules\install-appearance-module.ps1'
$tmp = Join-Path $env:TEMP ('spnext-appearance-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$pass=0
function Check($name,$valid) { if (!$valid) {throw "FAIL: $name"}; $script:pass++;Write-Output "PASS: $name" }
try {
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $img = Join-Path $tmp 'background.png'
    $bitmap = [Drawing.Bitmap]::new(64,48)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    try { $g.Clear([Drawing.Color]::FromArgb(20,50,22)) } finally {$g.Dispose()}
    try {$bitmap.Save($img,[Drawing.Imaging.ImageFormat]::Png)} finally {$bitmap.Dispose()}
    $pack = Join-Path $tmp 'forest-test.spmod'
    & $creator -ImageFile $img -Id 'forest-test' -Name 'Forest Test' -Author 'Regression' -Output $pack -Accent '#76B900' -AccentBright '#9BD532'
    Check 'package created' (Test-Path -LiteralPath $pack)
    $z=[IO.Compression.ZipFile]::OpenRead($pack)
    try{
      $names=@($z.Entries|ForEach-Object {$_.FullName})
      Check 'manifest + normalized ZIP members' ($names.Count -eq 2 -and $names -contains 'module.json' -and $names -contains 'assets/background.png')
    }finally{$z.Dispose()}
    $root=Join-Path $tmp 'isolated'
    & $installer -Package $pack -AssetsRoot $root
    $catalog=(Get-Content -LiteralPath (Join-Path $root 'catalog.json') -Raw | ConvertFrom-Json)
    Check 'isolated profile registered' ($catalog.schemaVersion -eq 1 -and @($catalog.profiles).Count -eq 1 -and $catalog.profiles[0].id -eq 'forest-test')
    Check 'image staged inside isolated assets' (Test-Path -LiteralPath (Join-Path $root 'assets\forest-test\background.png'))
    $duplicateBlocked=$false
    try { & $installer -Package $pack -AssetsRoot $root | Out-Null } catch { $duplicateBlocked=$true }
    Check 'duplicate installation blocked' $duplicateBlocked
    $wrongHashBlocked=$false
    try { & $installer -Package $pack -AssetsRoot $root -ExpectedSha256 ('0'*64) | Out-Null } catch { $wrongHashBlocked=$true }
    Check 'wrong SHA256 rejected' $wrongHashBlocked
    & $installer -Package $pack -AssetsRoot $root -Replace | Out-Null
    Check 'explicit replacement works without duplicate catalog entries' ((@( (Get-Content (Join-Path $root 'catalog.json') -Raw | ConvertFrom-Json).profiles)).Count -eq 1)
    $bad = Join-Path $tmp 'malicious.spmod'
    $z=[IO.Compression.ZipFile]::Open($bad,[IO.Compression.ZipArchiveMode]::Create)
    try {
      foreach($entryName in @('module.json','../unsafe.png')) {
        $en=$z.CreateEntry($entryName)
        $s=New-Object IO.StreamWriter($en.Open())
        try {$s.Write('{}')}finally{$s.Dispose()}
      }
    }finally{$z.Dispose()}
    $badBlocked=$false
    try { & $installer -Package $bad -AssetsRoot (Join-Path $tmp 'malicious-output') | Out-Null }catch{$badBlocked=$true}
    Check 'traversal member rejected' $badBlocked
    Check 'traversal did not create any outside file' (!(Test-Path -LiteralPath (Join-Path $tmp 'unsafe.png')))
    $uninstall = Join-Path $base 'Modules\uninstall-appearance-module.ps1'
    & $uninstall -Id 'forest-test' -AssetsRoot $root -BackupRoot (Join-Path $tmp 'backups')
    $after = Get-Content -LiteralPath (Join-Path $root 'catalog.json') -Raw | ConvertFrom-Json
    Check 'uninstall removes catalog entry' (@($after.profiles).Count -eq 0)
    Check 'uninstall removes live asset' (!(Test-Path -LiteralPath (Join-Path $root 'assets\forest-test')))
    Check 'uninstall writes backup' (@(Get-ChildItem -LiteralPath (Join-Path $tmp 'backups') -Directory).Count -eq 1)
    $protected = $false
    try { & $uninstall -Id 'default' -AssetsRoot $root -BackupRoot (Join-Path $tmp 'backups') | Out-Null } catch { $protected = $true }
    Check 'default profile cannot be removed' $protected
    $importer = Join-Path $base 'Modules\import-background.ps1'
    $export = Join-Path $tmp 'exports'
    & $importer -ImageFile $img -AssetsRoot $root -ExportRoot $export -BackupRoot (Join-Path $tmp 'backups') | Out-Null
    $imported = Get-Content (Join-Path $root 'catalog.json') -Raw | ConvertFrom-Json
    $importedProfiles = @($imported.profiles | Where-Object {$_.id -match '^bg-background-[a-f0-9]{8}$'})
    Check 'one click import registers generated appearance module' ($importedProfiles.Count -eq 1)
    Check 'import exports shareable .spmod file' (@(Get-ChildItem -LiteralPath $export -File -Filter '*.spmod').Count -eq 1)
    Check 'import materializes image under generated module id' (Test-Path -LiteralPath (Join-Path $root ('assets\'+$importedProfiles[0].id+'\background.png')))
    Write-Output "APPEARANCE_ISOLATED_PASS $pass checks"
} finally {
    if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Recurse -Force}
}
