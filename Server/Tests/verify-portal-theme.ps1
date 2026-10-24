# Visual asset contract test with disposable SQLite and a private loopback HTTP port.
# NO edits/restarts to live main account DB, servers, or Tailscale Funnel.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http
$root=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $root 'Duluka.Server\bin\PortalThemeIsolated\Duluka.Server.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Build isolated PortalThemeIsolated server first'}
$temp=Join-Path $env:TEMP ('DulukaPortalTheme-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$port=Get-Random -Minimum 26000 -Maximum 39999
$hostname='aorus-desktop.taile6314b.ts.net'
$env:DULUKA_Urls='http://127.0.0.1:'+$port
$env:DULUKA_PublicApi__Enabled='true'
$env:DULUKA_PublicApi__Proxy='tailscale'
$env:DULUKA_PublicApi__Hostname=$hostname
$env:DULUKA_Database__Path=Join-Path $temp 'accounts.db'
$env:DULUKA_Cbt1__DatabasePath=Join-Path $temp 'cbt1.db'
$env:DULUKA_Cbt1__Enabled='false'
$env:DULUKA_Portal__AccountMode='staging'
$env:DULUKA_PublicApi__AllowRegistration='false'
$proc=$null
$headers=@{'X-Forwarded-Proto'='https';'X-Forwarded-Host'=$hostname;'X-Forwarded-For'='203.0.113.47'}
try {
  $proc=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru -RedirectStandardOutput (Join-Path $temp 'out.log') -RedirectStandardError (Join-Path $temp 'err.log')
  $client=[Net.Http.HttpClient]::new()
  $client.Timeout=[TimeSpan]::FromSeconds(4)
  function Get-Resource([string]$path) {
    $req=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,('http://127.0.0.1:'+$port+$path))
    foreach($key in $headers.Keys){[void]$req.Headers.TryAddWithoutValidation($key,$headers[$key])}
    try {
      $res=$client.SendAsync($req).GetAwaiter().GetResult()
      try {
        $body=$res.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return @{status=[int]$res.StatusCode;body=$body;headers=$res.Headers}
      }finally{$res.Dispose()}
    }finally{$req.Dispose()}
  }
  $ready=$false
  for($i=0;$i -lt 40;$i++){try {if((Get-Resource '/healthz/ready').status -eq 200){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 250}
  if(!$ready){throw 'Isolated server readiness failed'}
  foreach($path in @('/portal','/portal/app.css','/portal/app.js','/portal/scene.css','/portal/scene.js','/portal/config')) {
    $result=Get-Resource $path
    if($result.status -ne 200){throw ('HTTP '+$result.status+' '+$path)}
    if($path -eq '/portal' -and $result.body -notmatch 'constellation-bg'){throw 'Visual portal markup missing'}
    if($path -eq '/portal/scene.css' -and $result.body -notmatch 'showcase-runway'){throw 'Visual styles missing'}
    if($path -eq '/portal/scene.js' -and $result.body -notmatch 'prefers-reduced-motion'){throw 'Reduced motion behavior missing'}
    if($path -eq '/portal/config') {
      $cfg=$result.body|ConvertFrom-Json
      if($cfg.accountMode -ne 'staging'){throw 'Wrong isolated account mode'}
    }
    Write-Output ('PASS HTTP 200 '+$path)
  }
  Write-Output 'PORTAL_THEME_ISOLATED_HTTP_PASS'
}finally{
  if($proc -and !$proc.HasExited){Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue}
  if($client){$client.Dispose()}
  Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
  foreach($key in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_PublicApi__Proxy','DULUKA_PublicApi__Hostname','DULUKA_Database__Path','DULUKA_Cbt1__DatabasePath','DULUKA_Cbt1__Enabled','DULUKA_Portal__AccountMode','DULUKA_PublicApi__AllowRegistration')){Remove-Item ('Env:'+$key) -ErrorAction SilentlyContinue}
}
