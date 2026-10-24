# CBT1 single-use invite E2E: 3 isolated .NET processes with disposable
# account/CBT1 SQLite files. No real account password, token or table touched.
# Requires port 5117 free; stop Local Admin first, restart after testing.
$ErrorActionPreference='Stop'
$exe='C:\My Project\NVIDIA-Shadowplay\Server\Duluka.Server\bin\InviteStage\Duluka.Server.exe'
if(Get-NetTCPConnection -LocalPort 5117 -State Listen -ErrorAction SilentlyContinue){
  throw 'Local Admin 5117 must be stopped for isolated invitation verification.'
}
$tmp=Join-Path $env:TEMP ('duluka-invite-e2e-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force|Out-Null
$accountDb=Join-Path $tmp 'test-accounts.db'
$cbtDb=Join-Path $tmp 'test-cbt1.db'
$codeFile=Join-Path $tmp 'reset-code.txt'
[IO.File]::WriteAllText($codeFile,('A'*48))
$basePort=Get-Random -Minimum 36000 -Maximum 47000
$ordinaryPort=$basePort
$publicPort=$basePort+1
$hostName='aorus-desktop.taile6314b.ts.net'
$serverProcesses=@()
function Settings([int]$port,[bool]$public,[bool]$admin){
  $env:DULUKA_Urls='http://127.0.0.1:'+$port
  $env:DULUKA_PublicApi__Enabled=$public.ToString().ToLowerInvariant()
  $env:DULUKA_LocalAdmin__Enabled=$admin.ToString().ToLowerInvariant()
  $env:DULUKA_PublicApi__Proxy='tailscale'
  $env:DULUKA_PublicApi__Hostname=$hostName
  $env:DULUKA_PublicApi__AllowRegistration='false'
  $env:DULUKA_Portal__AccountMode='main'
  $env:DULUKA_Database__Path=$accountDb
  $env:DULUKA_Cbt1__DatabasePath=$cbtDb
  $env:DULUKA_Cbt1__Enabled='false'
  $env:DULUKA_Cbt1__InviteEnabled='true'
  $env:DULUKA_LocalAdmin__ResetCodeFile=$codeFile
  $env:DULUKA_GitHub__ClientSecret=''
}
function StartTestServer([int]$port,[bool]$public,[bool]$admin){
  Settings $port $public $admin
  $p=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $tmp ("$port-out.log")) -RedirectStandardError (Join-Path $tmp ("$port-err.log"))
  $script:serverProcesses+= $p
  for($i=0;$i -lt 60;$i++){
    if($p.HasExited){throw "Test server exited early: $port"}
    try{$s=Invoke-WebRequest -Uri ("http://127.0.0.1:$port/healthz/ready") -UseBasicParsing -TimeoutSec 2;if($s.StatusCode -eq 200){return}}catch{}
    Start-Sleep -Milliseconds 170
  }
  throw "Test server not ready: $port"
}
function Json([int]$port,[string]$path,[string]$method,[object]$payload,[string]$token='', [bool]$proxy=$false,[bool]$admin=$false,[bool]$origin=$false){
  $headers=@{}
  if($token){$headers.Authorization='Bearer '+$token}
  if($proxy){$headers['X-Forwarded-Proto']='https';$headers['X-Forwarded-Host']=$hostName;$headers['X-Forwarded-For']='203.0.113.55'}
  if($admin){$headers['X-Admin-Request']='1';if($origin){$headers.Origin='http://127.0.0.1:5117'}}
  $args=@{Uri=("http://127.0.0.1:$port"+$path);Method=$method;Headers=$headers;UseBasicParsing=$true;TimeoutSec=5;ErrorAction='Stop'}
  if($method -ne 'GET'){$args.ContentType='application/json';$args.Body=($payload|ConvertTo-Json -Compress -Depth 7)}
  try {$r=Invoke-WebRequest @args;return @{Code=[int]$r.StatusCode;Data=($r.Content|ConvertFrom-Json)}}
  catch {if($_.Exception.Response){return @{Code=[int]$_.Exception.Response.StatusCode;Data=$null}};throw}
}
function Assert([string]$label,[bool]$condition){if(-not $condition){throw ('FAIL: '+$label)};Write-Host ('PASS: '+$label)}
function Register([string]$username){
  $bytes=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
  try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
  $dev=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
  $body=@{username=$username;password='SyntheticPass-'+([guid]::NewGuid().ToString('N'));deviceKey=$dev;deviceName='Invite Test'}
  $res=Json $ordinaryPort '/v1/auth/register' 'POST' $body
  Assert "Synthetic account $username" ($res.Code -eq 200)
  return $res.Data.resource.sessionToken
}
try{
  StartTestServer $ordinaryPort $false $false
  StartTestServer $publicPort $true $false
  StartTestServer 5117 $false $true
  $nameSuffix=([guid]::NewGuid().ToString('N')).Substring(0,9)
  $t1=Register ('inv1'+$nameSuffix)
  $t2=Register ('inv2'+$nameSuffix)
  $baseApp=@{alias='SyntheticOne';email=('inv-'+$nameSuffix+'@example.invalid');windowsVersion='Windows 11';gpuVendor='NVIDIA';testingFocus='Overlay';notes='Synthetic invite test';website='';consent=$true}
  $status=Json $publicPort '/v1/cbt1/status' 'GET' $null '' $true
  Assert 'CBT1 public acceptance closed, invites enabled' ($status.Code -eq 200 -and !$status.Data.accepting -and $status.Data.inviteEnabled)
  Assert 'public account registration still closed' ((Json $publicPort '/v1/auth/register' 'POST' @{} '' $true).Code -eq 403)
  Assert 'Admin never exposed via public API' ((Json $publicPort '/admin/api/cbt1/invites' 'GET' $null '' $true).Code -eq 404)
  $beforeInvitation=Json $publicPort '/v1/cbt1/apply' 'POST' $baseApp $t1 $true
  Write-Output ('UNINVITED_STATUS='+$beforeInvitation.Code)
  Assert 'uninvited applicant blocked' ($beforeInvitation.Code -eq 503)
  $badAdmin=Json 5117 '/admin/api/cbt1/invites' 'POST' @{label='SyntheticBatch';hours=24} '' $false $true $false
  Assert 'operator create denies missing origin' ($badAdmin.Code -eq 403)
  $created=Json 5117 '/admin/api/cbt1/invites' 'POST' @{label='SyntheticBatch';hours=24} '' $false $true $true
  Assert 'local operator creates invite' ($created.Code -eq 200 -and $created.Data.invite.code.Length -eq 69)
  $secret=$created.Data.invite.code
  $inviteId=$created.Data.invite.id
  $list=Json 5117 '/admin/api/cbt1/invites' 'GET' $null '' $false $true
  Assert 'admin list returns metadata not plaintext codes' ($list.Code -eq 200 -and @($list.Data.invites).Count -ge 1 -and
    -not (($list.Data | ConvertTo-Json -Depth 9) -match [regex]::Escape($secret)))
  Assert 'first account redeems invite' ((Json $publicPort '/v1/cbt1/invite/redeem' 'POST' @{code=$secret} $t1 $true).Code -eq 200)
  Assert 'first account sees invited status' ((Json $publicPort '/v1/cbt1/me' 'GET' $null $t1 $true).Data.invited -eq $true)
  Assert 'second account cannot reuse code' ((Json $publicPort '/v1/cbt1/invite/redeem' 'POST' @{code=$secret} $t2 $true).Code -eq 400)
  Assert 'redeemer re-submit is idempotent' ((Json $publicPort '/v1/cbt1/invite/redeem' 'POST' @{code=$secret} $t1 $true).Code -eq 200)
  Assert 'invited user submits CBT1 application' ((Json $publicPort '/v1/cbt1/apply' 'POST' $baseApp $t1 $true).Code -eq 200)
  $applications=Json 5117 '/admin/api/cbt1/applications' 'GET' $null '' $false $true
  Assert 'local admin reviews submitted applications' ($applications.Code -eq 200 -and @($applications.Data.applications).Count -eq 1)
  $applicationId=$applications.Data.applications[0].id
  $decisionPath='/admin/api/cbt1/applications/'+$applicationId+'/decision'
  Assert 'decision without local Origin blocked' (
    (Json 5117 $decisionPath 'POST' @{decision='approved'} '' $false $true $false).Code -eq 403)
  Assert 'local admin approves CBT1 applicant' (
    (Json 5117 $decisionPath 'POST' @{decision='approved'} '' $false $true $true).Code -eq 200)
  $updated=Json $publicPort '/v1/cbt1/me' 'GET' $null $t1 $true
  Assert 'tester sees approved CBT1 decision' ($updated.Code -eq 200 -and $updated.Data.application.decision -eq 'approved')
  $revoke=Json 5117 ('/admin/api/cbt1/invites/'+$inviteId+'/revoke') 'POST' @{} '' $false $true $true
  Assert 'operator revokes invite' ($revoke.Code -eq 200)
  Assert 'revoked code removes invitation access' ((Json $publicPort '/v1/cbt1/me' 'GET' $null $t1 $true).Data.invited -eq $false)
  Assert 'second account remains blocked' ((Json $publicPort '/v1/cbt1/apply' 'POST' $baseApp $t2 $true).Code -eq 503)
  Write-Output 'PASS: isolated CBT1 invitation end-to-end.'
}finally{
  foreach($p in $serverProcesses){if($p -and -not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}}
  Start-Sleep -Milliseconds 350
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  foreach($k in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_LocalAdmin__Enabled','DULUKA_PublicApi__Proxy','DULUKA_PublicApi__Hostname','DULUKA_PublicApi__AllowRegistration','DULUKA_Portal__AccountMode','DULUKA_Database__Path','DULUKA_Cbt1__DatabasePath','DULUKA_Cbt1__Enabled','DULUKA_Cbt1__InviteEnabled','DULUKA_LocalAdmin__ResetCodeFile','DULUKA_GitHub__ClientSecret')){Remove-Item ('Env:'+$k) -ErrorAction SilentlyContinue}
}
