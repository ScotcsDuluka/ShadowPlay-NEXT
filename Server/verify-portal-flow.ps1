# Full staging auth -> CBT1 flow. All data synthetic and stored in temporary SQLite.
# Does not change public Funnel, any live process, or production accounts.
param([string]$BinaryPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$exe=if($BinaryPath){$BinaryPath}else{Join-Path $root 'Duluka.Server\bin\TailscaleStage\Duluka.Server.exe'}
if(-not(Test-Path -LiteralPath $exe)){throw 'Server binary missing'}
$ts='C:\Program Files\Tailscale\tailscale.exe'
$hostName=((& $ts status --json | ConvertFrom-Json).Self.DNSName).TrimEnd('.')
$port=Get-Random -Minimum 30000 -Maximum 42000
$tmp=Join-Path $env:TEMP ('duluka-cbt1-flow-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$env:DULUKA_Urls="http://127.0.0.1:$port"
$env:DULUKA_PublicApi__Enabled='true'
$env:DULUKA_PublicApi__Proxy='tailscale'
$env:DULUKA_PublicApi__Hostname=$hostName
$env:DULUKA_Database__Path=Join-Path $tmp 'accounts.db'
$env:DULUKA_Cbt1__DatabasePath=Join-Path $tmp 'cbt1.db'
$env:DULUKA_Cbt1__Enabled='true'
$env:DULUKA_GitHub__ClientSecret=''
$client=[System.Net.Http.HttpClient]::new()
$client.Timeout=[TimeSpan]::FromSeconds(4)
$token=''
$forwarded=@{ 'X-Forwarded-Proto'='https'; 'X-Forwarded-Host'=$hostName; 'X-Forwarded-For'='203.0.113.44' }
function Call([string]$verb,[string]$path,[object]$body){
    $message=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($verb),"http://127.0.0.1:$port$path")
    try{
        foreach($name in $forwarded.Keys){[void]$message.Headers.TryAddWithoutValidation($name,$forwarded[$name])}
        if($token){[void]$message.Headers.TryAddWithoutValidation('Authorization','Bearer '+$token)}
        if($null -ne $body){$message.Content=[Net.Http.StringContent]::new(($body|ConvertTo-Json -Depth 7 -Compress),[Text.Encoding]::UTF8,'application/json')}
        $response=$client.SendAsync($message).GetAwaiter().GetResult()
        try{
            $json=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return @{ Status=[int]$response.StatusCode; Data=($json|ConvertFrom-Json)}
        }finally{$response.Dispose()}
    }finally{$message.Dispose()}
}
function Assert([string]$name,[bool]$ok){
    if(-not $ok){throw "FAIL $name"}
    Write-Output ("PASS "+$name)
}
$server=$null
try{
    $server=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru -RedirectStandardOutput (Join-Path $tmp 'out.log') -RedirectStandardError (Join-Path $tmp 'err.log')
    $ready=$false
    for($i=0;$i -lt 50;$i++){try{if((Call 'GET' '/healthz/ready' $null).Status -eq 200){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 180}
    if(-not $ready){throw 'Staging server unavailable'}
    $status=Call 'GET' '/v1/cbt1/status' $null
    Assert 'CBT1 enabled in isolated test database' ($status.Status -eq 200 -and $status.Data.accepting -eq $true)
    $nonce=[guid]::NewGuid().ToString('N')
    $username='test'+$nonce.Substring(0,12)
    $password='StagingOnly-'+$nonce.Substring(12,15)
    $bytes=New-Object byte[] 32
    $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
    $deviceKey=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    $auth=@{username=$username;password=$password;deviceKey=$deviceKey;deviceName='Staging Portal Test'}
    $registered=Call 'POST' '/v1/auth/register' $auth
    Assert 'register username and password' ($registered.Status -eq 200 -and $registered.Data.resource.sessionToken.Length -ge 32)
    $token=$registered.Data.resource.sessionToken
    Assert 'authenticated account /me' ((Call 'GET' '/v1/account/me' $null).Status -eq 200)
    Assert 'CBT1 not applied yet' ((Call 'GET' '/v1/cbt1/me' $null).Data.application -eq $null)
    $payload=@{alias='StagingTester';email=("test-"+$nonce+'@example.invalid');windowsVersion='Windows 11';gpuVendor='NVIDIA';testingFocus='Overlay';notes='Synthetic integration test';website='';consent=$true}
    $submitted=Call 'POST' '/v1/cbt1/apply' $payload
    Assert 'submit CBT1 application' ($submitted.Status -eq 200 -and $submitted.Data.received -eq $true)
    $own=Call 'GET' '/v1/cbt1/me' $null
    Assert 'own application visible only to session' ($own.Status -eq 200 -and $own.Data.application.decision -eq 'pending')
    $token=''
    Assert 'anonymous CBT1 application rejected' ((Call 'GET' '/v1/cbt1/me' $null).Status -eq 401)
    $loggedIn=Call 'POST' '/v1/auth/login' $auth
    Assert 'username login after registration' ($loggedIn.Status -eq 200 -and $loggedIn.Data.resource.sessionToken.Length -ge 32)
    $token=$loggedIn.Data.resource.sessionToken
    Assert 'application still linked after new login' ((Call 'GET' '/v1/cbt1/me' $null).Data.application.decision -eq 'pending')
    Assert 'operator route remains hidden' ((Call 'GET' '/v1/cbt1/admin/applications' $null).Status -eq 404)

    # Account management: verify /me + device listing, an explicitly requested
    # disconnect of another device, and authenticated password change.
    $profile=(Call 'GET' '/v1/account/me' $null)
    $oldDeviceId=$profile.Data.resource.currentDevice.deviceId
    Assert 'account details show matching username and active device' (
        $profile.Status -eq 200 -and $profile.Data.resource.username -eq $username -and
        -not [string]::IsNullOrWhiteSpace($oldDeviceId))
    $knownDevices=Call 'GET' '/v1/account/devices' $null
    Assert 'account device list includes current device' (
        $knownDevices.Status -eq 200 -and
        @($knownDevices.Data.resource.devices | Where-Object deviceId -eq $oldDeviceId).Count -eq 1)
    $oldToken=$token
    $secondDevice=New-Object byte[] 32
    $rng2=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$rng2.GetBytes($secondDevice)}finally{$rng2.Dispose()}
    $secondAuth=$auth.Clone()
    $secondAuth.deviceKey=[Convert]::ToBase64String($secondDevice).TrimEnd('=').Replace('+','-').Replace('/','_')
    $secondAuth.deviceName='Secondary Synthetic Device'
    $secondLogin=Call 'POST' '/v1/auth/login' $secondAuth
    Assert 'second device login succeeds' (
        $secondLogin.Status -eq 200 -and $secondLogin.Data.resource.sessionToken.Length -ge 32)
    $token=$secondLogin.Data.resource.sessionToken
    $secondProfile=Call 'GET' '/v1/account/me' $null
    Assert 'second device has a distinct device ID' (
        $secondProfile.Status -eq 200 -and
        $secondProfile.Data.resource.currentDevice.deviceId -ne $oldDeviceId)
    $secondDevices=Call 'GET' '/v1/account/devices' $null
    Assert 'second device can see the two linked devices' (
        $secondDevices.Status -eq 200 -and @($secondDevices.Data.resource.devices).Count -ge 2)
    $disconnected=Call 'POST' ('/v1/account/devices/'+$oldDeviceId+'/revoke') @{}
    Assert 'disconnect first device succeeds' (
        $disconnected.Status -eq 200 -and $disconnected.Data.resource.revoked -eq $true)
    $token=$oldToken
    Assert 'disconnected device session is invalidated' (
        (Call 'GET' '/v1/account/me' $null).Status -eq 401)
    $token=$secondLogin.Data.resource.sessionToken

    $newPassword='New-Only-'+$nonce.Substring(3,17)
    Assert 'wrong current password cannot change password' (
        (Call 'POST' '/v1/account/password' @{currentPassword='incorrect';newPassword=$newPassword}).Status -eq 400)
    Assert 'password update with valid current password succeeds' (
        (Call 'POST' '/v1/account/password' @{currentPassword=$password;newPassword=$newPassword}).Status -eq 200)
    Assert 'old password no longer signs in' ((Call 'POST' '/v1/auth/login' $secondAuth).Status -eq 401)
    $secondAuth.password=$newPassword
    $changedLogin=Call 'POST' '/v1/auth/login' $secondAuth
    Assert 'new password logs in to same account' (
        $changedLogin.Status -eq 200 -and $changedLogin.Data.resource.accountId -eq $profile.Data.resource.accountId)
    $token=$changedLogin.Data.resource.sessionToken
    Assert 'logout revokes session' ((Call 'POST' '/v1/auth/session/revoke' $null).Status -eq 200)
    Assert 'revoked bearer not usable' ((Call 'GET' '/v1/cbt1/me' $null).Status -eq 401)
    Write-Output 'PASS full staging register/login/CBT1/logout integration.'
}finally{
    if($null -ne $server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue}
    $client.Dispose()
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    foreach($name in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_PublicApi__Proxy','DULUKA_PublicApi__Hostname','DULUKA_Database__Path','DULUKA_Cbt1__DatabasePath','DULUKA_Cbt1__Enabled','DULUKA_GitHub__ClientSecret')){Remove-Item ("Env:"+$name) -ErrorAction SilentlyContinue}
}
