# Capture the REAL Launcher host window, not an auxiliary/minimized CEF window.
# Read-only diagnostic; no focus changes, UI clicks or service restarts.
param(
    [string]$Out = "launcher-shot.png",
    [int]$MinWidth = 800,
    [int]$MinHeight = 450
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('Win32LauncherShot' -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32LauncherShot {
    public delegate bool CB(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
}
$script:best = [IntPtr]::Zero
$script:bestArea = 0L
$script:rect = $null
$script:candidates = @()
$cb = [Win32LauncherShot+CB] {
    param($h, $l)
    $sb = [System.Text.StringBuilder]::new(256)
    [Win32LauncherShot]::GetClassNameW($h, $sb, 256) | Out-Null
    if ($sb.ToString() -ne "NvLauncherHostWindow") { return $true }
    $r = New-Object Win32LauncherShot+RECT
    if (-not [Win32LauncherShot]::GetWindowRect($h, [ref]$r)) { return $true }
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    $visible = [Win32LauncherShot]::IsWindowVisible($h)
    $script:candidates += "$h ${w}x${ht} visible=$visible"
    if ($visible -and $w -ge $MinWidth -and $ht -ge $MinHeight -and
        ([long]$w * [long]$ht) -gt $script:bestArea) {
        $script:best = $h
        $script:bestArea = [long]$w * [long]$ht
        $script:rect = $r
    }
    return $true
}
[Win32LauncherShot]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
Write-Output "Launcher windows: $($script:candidates -join '; ')"
if ($script:best -eq [IntPtr]::Zero) {
    throw "No visible Launcher host at least ${MinWidth}x${MinHeight}; screenshot not taken."
}
$w = $script:rect.R - $script:rect.L
$ht = $script:rect.B - $script:rect.T
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
try { $printed = [Win32LauncherShot]::PrintWindow($script:best, $dc, 2) }
finally { $g.ReleaseHdc($dc); $g.Dispose() }
if (-not $printed) { $bmp.Dispose(); throw "PrintWindow failed; no image was saved." }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out (${w}x${ht})"
