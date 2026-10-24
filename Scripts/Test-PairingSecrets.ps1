[CmdletBinding()]
param([switch]$WorkingTree)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$violations = New-Object System.Collections.Generic.List[string]
if ($WorkingTree) {
    $tracked = @(git -C $repo ls-files)
} else {
    $tracked = @(git -C $repo diff --cached --name-only --diff-filter=ACMRT)
}
foreach ($name in $tracked) {
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    $norm = $name -replace '\\','/'
    if ($norm -match '(?i)(pairing-current-user\.dpapi|\.pairing-secret|nvcontainer\.private\.json)') {
        $violations.Add($norm + ': protected/private secret file cannot be committed')
        continue
    }
    if ($norm -notmatch '\.(cs|cpp|h|js|json|vb|ps1|md|txt|xml)$') { continue }
    try {
        if ($WorkingTree) {
            $file=Join-Path $repo $norm
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
            if ((Get-Item -LiteralPath $file).Length -gt 2MB) { continue }
            $content=[IO.File]::ReadAllText($file)
        } else {
            $content = (& git -C $repo show (':' + $norm) 2>$null) -join [Environment]::NewLine
        }
        if ($norm -match '(?i)(^|/)nvcontainer\.json$') {
            try {
                $config = $content | ConvertFrom-Json
                $candidate = if ($config.pairing) { $config.pairing.secret } else { $null }
                if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                    $violations.Add($norm + ': plaintext pairing.secret prohibited')
                }
            } catch { $violations.Add($norm + ': invalid JSON config') }
        }
        # Prevent accidentally hardcoding 32-hex legacy credentials in source,
        # JSON Init files, and CEF fallback responses (including escaped quotes).
        if ($content -match '(?i)(?:secret|NODE_JSON).{0,48}[0-9a-f]{32}' -or
            $content -match '(?i)\\?"secret\\?"\s*:\s*\\?"[0-9a-f]{24,}') {
            $violations.Add($norm + ': suspected embedded pairing credential')
        }
    } catch {
        $violations.Add($norm + ': unable to inspect staged content')
    }
}
if ($violations.Count -gt 0) {
    Write-Error ('Pairing secret guard blocked the change:' + [Environment]::NewLine +
        ($violations -join [Environment]::NewLine))
    exit 1
}
Write-Output ('PAIRING_GUARD_PASS: checked ' + $tracked.Count + ' ' +
    $(if($WorkingTree){'tracked working-tree files'}else{'staged files'}))
