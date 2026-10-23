# Read-only: prove which OSC files the live CEF HTTP endpoint serves.
# Source of truth is Project/ on Git Stable. Nothing is copied or deployed.
param(
  [string[]]$Files = @(
    'index.html','mods/main-menu-live.js','mods/main-menu-live.css',
    'mods/main-menu-unified-native.js','mods/main-menu-group-editor.js',
    'mods/osc-motion.js','mods/osc-motion.css',
    'mods/launcher-language-sync.js','mods/osc-ui-localization.js'
  ),
  [switch]$AsJson
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$nodeRoot=Split-Path -Parent $PSScriptRoot
$source=Join-Path $nodeRoot 'osc'
$repo=(& git -C $nodeRoot rev-parse --show-toplevel 2>$null).Trim()
if(!$repo){throw 'Could not identify Git repository'}
$buildNode=Join-Path $repo 'build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI'
$runtime=Join-Path $buildNode 'osc'
$branch=(& git -C $repo branch --show-current).Trim()
$head=(& git -C $repo rev-parse HEAD).Trim()
$dirty=@(& git -C $repo status --porcelain -- 'Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc')
$listeners=@(Get-NetTCPConnection -LocalPort 59011 -State Listen -ErrorAction SilentlyContinue)
$processInfo=$null
if($listeners.Count -gt 0){
  $ownerPid=[int]$listeners[0].OwningProcess
  $processInfo=Get-CimInstance Win32_Process -Filter "ProcessId=$ownerPid" -ErrorAction SilentlyContinue |
    Select-Object ProcessId,Name,ExecutablePath,CommandLine
}
function SHA([string]$file) {
  if(!(Test-Path -LiteralPath $file -PathType Leaf)){return $null}
  (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
function HttpSHA([string]$name) {
  if($listeners.Count -eq 0){return $null}
  $segments=$name.Split('/')|ForEach-Object{[Uri]::EscapeDataString($_)}
  $url='http://127.0.0.1:59011/'+($segments -join '/')+'?osc_source_audit='+[DateTime]::UtcNow.Ticks
  $req=[Net.HttpWebRequest][Net.WebRequest]::Create($url)
  $req.Timeout=2500;$req.ReadWriteTimeout=2500
  $req.CachePolicy=New-Object System.Net.Cache.RequestCachePolicy([System.Net.Cache.RequestCacheLevel]::BypassCache)
  try {
    $response=$req.GetResponse()
    try{
      $stream=$response.GetResponseStream()
      $buffer=New-Object System.IO.MemoryStream
      try{
        $stream.CopyTo($buffer)
        $digest=[Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($digest.ComputeHash($buffer.ToArray())).Replace('-','') }
        finally {$digest.Dispose()}
      }finally{$buffer.Dispose()}
    }finally{$response.Close()}
  }catch{return 'HTTP_ERROR: '+$_.Exception.Message}
}
$rows=@()
foreach($name in $Files) {
  if($name -notmatch '^[a-zA-Z0-9_\-./]+$' -or $name.Contains('..') -or $name.StartsWith('/')){throw "Unsafe file: $name"}
  $rel=$name.Replace('/','\')
  $p=SHA (Join-Path $source $rel)
  $b=SHA (Join-Path $runtime $rel)
  $h=HttpSHA $name
  $origin='OFFLINE'
  if($h -and $h.StartsWith('HTTP_ERROR:')){$origin='HTTP ERROR'}
  elseif($h){
    if($p -and $b -and $h -eq $p -and $h -eq $b){$origin='BOTH (identical)'}
    elseif($b -and $h -eq $b){$origin='BUILD'}
    elseif($p -and $h -eq $p){$origin='PROJECT'}
    else{$origin='OTHER / UNVERIFIED'}
  }
  $rows += [pscustomobject]@{
    file=$name;liveOrigin=$origin;projectEqualsBuild=[bool]($p -and $b -and $p -eq $b)
    projectSHA256=$p;buildSHA256=$b;httpSHA256=$h
  }
}
$result=[pscustomobject]@{
  checkedAt=[DateTime]::UtcNow.ToString('o')
  branch=$branch;commit=$head;source=$source;runtime=$runtime
  sourceDirtyEntries=@($dirty);port=59011;online=[bool]($listeners.Count -gt 0)
  listenerProcess=$processInfo;files=$rows
}
if($AsJson){$result|ConvertTo-Json -Depth 8}
else{
  "SOURCE:   $source";"RUNTIME:  $runtime";"GIT:      $branch @ $head"
  "OSC changes in Git: $($dirty.Count)"
  "HTTP 59011: $(if($listeners.Count){'ONLINE'}else{'OFFLINE'})"
  if($processInfo){"HOST:     $($processInfo.Name) PID $($processInfo.ProcessId) - $($processInfo.ExecutablePath)"}
  $rows|Select-Object file,liveOrigin,projectEqualsBuild|Format-Table -AutoSize|Out-String -Width 200
  'READ-ONLY: Nothing copied to Build. No OSC process started.'
}
