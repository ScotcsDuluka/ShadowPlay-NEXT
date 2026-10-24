# Isolated native account reset integration. No real account credentials or DB touched.
$ErrorActionPreference='Stop'
$exe='C:\My Project\NVIDIA-Shadowplay\Server\Duluka.Server\bin\AdminResetStage\Duluka.Server.exe'
$tmp=Join-Path $env:TEMP ('duluka-reset-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$port=Get-Random -Minimum 31500 -Maximum 42000
$db=Join-Path $tmp 'test.db'
$codeFile=Join-Path $tmp 'recovery-code.txt'
$random=New-Object byte[] 24
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try{$rng.GetBytes($random)}finally{$rng.Dispose()}
$code=([BitConverter]::ToString($random)).Replace('-','')
[IO.File]::WriteAllText($codeFile,$code,[Text.UTF8Encoding]::new($false))
$server=$null
$admin=$null
function Status([string]$url,[string]$method,[string]$body,[hashtable]$headers) {
  try{
    $args=@{Uri=$url;Method=$method;Headers=$headers;UseBasicParsing=$true;TimeoutSec=5;ErrorAction='Stop'}
    if($method -ne 'GET'){$args.ContentType='application/json';$args.Body=$body}
    $r=Invoke-WebRequest @args
    return @{Code=[int]$r.StatusCode;Data=($r.Content | ConvertFrom-Json)}
  }
  catch{if($_.Exception.Response){return @{Code=[int]$_.Exception.Response.StatusCode;Data=$null}};throw}
}
function Pass([string]$label,[int]$actual,[int]$expected){
 if($actual -ne $expected){throw ('FAIL '+$label+' got '+$actual+' expected '+$expected)}
 Write-Output ('PASS '+$label+' '+$expected)
}
function Ready([string]$url){
  for($i=0;$i -lt 45;$i++){try{$r=Invoke-WebRequest -UseBasicParsing $url -TimeoutSec 2;if($r.StatusCode -eq 200){return}}catch{};Start-Sleep -Milliseconds 180}
  throw ('Not ready '+$url)
}
try{
  $env:DULUKA_Urls="http://127.0.0.1:$port"
  $env:DULUKA_PublicApi__Enabled='false'
  $env:DULUKA_LocalAdmin__Enabled='false'
  $env:DULUKA_Database__Path=$db
  $env:DULUKA_Cbt1__DatabasePath=Join-Path $tmp 'cbt1.db'
  $server=Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $tmp 'account.log') -RedirectStandardError (Join-Path $tmp 'account.err')
  Ready "http://127.0.0.1:$port/healthz/ready"
  $rand=[guid]::NewGuid().ToString('N')
  $user='test'+$rand.Substring(0,10)
  $old='Old-Test-'+$rand.Substring(10,12)
  $next='New-Test-'+$rand.Substring(10,12)
  $bytes=New-Object byte[] 32
  $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
  try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
  $device=([Convert]::ToBase64String($bytes)).TrimEnd('=').Replace('+','-').Replace('/','_')
  $auth=@{username=$user;password=$old;deviceKey=$device;deviceName='Reset Test'}
  $reg=Status "http://127.0.0.1:$port/v1/auth/register" 'POST' ($auth|ConvertTo-Json -Compress) @{}
  Pass 'create disposable account' $reg.Code 200
  $accountId=$reg.Data.resource.accountId
  $session=$reg.Data.resource.sessionToken
  $env:DULUKA_Urls='http://127.0.0.1:5117'
  $env:DULUKA_LocalAdmin__Enabled='true'
  $env:DULUKA_LocalAdmin__ResetCodeFile=$codeFile
  $admin=Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $tmp 'admin.log') -RedirectStandardError (Join-Path $tmp 'admin.err')
  Ready 'http://127.0.0.1:5117/healthz/ready'
  $url='http://127.0.0.1:5117/admin/api/accounts/'+$accountId+'/reset-password'
  $headers=@{Origin='http://127.0.0.1:5117';'X-Admin-Request'='1'}
  $payload=@{recoveryCode=$code;confirmUsername=$user;newPassword=$next}
  Pass 'recovery route refuses missing local Origin' (Status $url 'POST' ($payload|ConvertTo-Json -Compress) @{'X-Admin-Request'='1'}).Code 403
  $payload.recoveryCode=('0'*48)
  Pass 'wrong code rejected' (Status $url 'POST' ($payload|ConvertTo-Json -Compress) $headers).Code 403
  $payload.recoveryCode=$code
  $payload.confirmUsername='wrong-person'
  Pass 'wrong username rejected' (Status $url 'POST' ($payload|ConvertTo-Json -Compress) $headers).Code 400
  $payload.confirmUsername=$user
  $result=Status $url 'POST' ($payload|ConvertTo-Json -Compress) $headers
  Pass 'reset new password' $result.Code 200
  if(-not $result.Data.resource.sessionsRevoked){throw 'Old sessions not revoked'}
  if([IO.File]::ReadAllText($codeFile).Trim() -eq $code){throw 'Recovery code did not rotate'}
  Write-Output 'PASS recovery code rotated locally'
  Pass 'recovery code cannot be replayed' (Status $url 'POST' ($payload|ConvertTo-Json -Compress) $headers).Code 403
  Pass 'previous session revoked' (Status "http://127.0.0.1:$port/v1/account/me" 'GET' $null @{Authorization='Bearer '+$session}).Code 401
  Pass 'old password rejected' (Status "http://127.0.0.1:$port/v1/auth/login" 'POST' ($auth|ConvertTo-Json -Compress) @{}).Code 401
  $auth.password=$next
  $login=Status "http://127.0.0.1:$port/v1/auth/login" 'POST' ($auth|ConvertTo-Json -Compress) @{}
  Pass 'new password signs in' $login.Code 200
  if($login.Data.resource.accountId -ne $accountId){throw 'Account ID changed unexpectedly'}
  Write-Output 'PASS original account identity preserved'
  Write-Output 'PASS isolated admin recovery integration.'
}finally{
 foreach($p in @($admin,$server)){if($p -and -not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}}
 Start-Sleep -Milliseconds 350
 Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
 foreach($name in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_LocalAdmin__Enabled','DULUKA_LocalAdmin__ResetCodeFile','DULUKA_Database__Path','DULUKA_Cbt1__DatabasePath')){Remove-Item ('Env:'+$name) -ErrorAction SilentlyContinue}
}
