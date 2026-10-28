#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceDirectory, [int]$MaxSeconds=120)
$ErrorActionPreference='Stop'
if ($MaxSeconds -lt 15 -or $MaxSeconds -gt 300) { throw 'Scene duration must be bounded to 15..300 seconds.' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Windows.Forms;
public sealed class CaptureTestScene : Form {
    readonly Timer timer = new Timer();
    readonly DateTime started=DateTime.UtcNow;
    readonly string stopFile;
    readonly int seconds;
    int sequence;
    public CaptureTestScene(Rectangle bounds, string stop, int duration) {
        stopFile=stop; seconds=duration;
        FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
        Bounds=bounds; TopMost=true; ShowInTaskbar=false; DoubleBuffered=true;
        BackColor=Color.FromArgb(8,18,32);
        timer.Interval=33;
        timer.Tick+=(s,e)=> {
            if ((DateTime.UtcNow-started).TotalSeconds>=seconds || System.IO.File.Exists(stopFile)) { Close(); return; }
            sequence++; Invalidate();
        };
        Shown+=(s,e)=>timer.Start();
        FormClosed+=(s,e)=>timer.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        using(var pen=new Pen(Color.FromArgb(30,60,80)))
            for(int x=0;x<Width;x+=80) for(int y=0;y<Height;y+=80) e.Graphics.DrawRectangle(pen,x,y,80,80);
        using(var font=new Font("Segoe UI",28,FontStyle.Bold)) {
            e.Graphics.DrawString("SHADOWPLAY NEXT / CONTROLLED CAPTURE TEST",font,Brushes.White,45,45);
            e.Graphics.DrawString("VIDEO ONLY / NO MICROPHONE / NO SYSTEM AUDIO",font,Brushes.LightGreen,45,100);
            e.Graphics.DrawString("FRAME " + sequence.ToString("D6"),font,Brushes.White,45,155);
        }
        int markerX=(sequence*13)%Math.Max(1,Width-180);
        e.Graphics.FillRectangle(Brushes.LimeGreen,markerX,Height/2,180,140);
        for(int bit=0;bit<16;bit++)
            e.Graphics.FillRectangle(((sequence>>bit)&1)==0?Brushes.Black:Brushes.White,45+bit*35,Height-140,32,80);
    }
}
'@
$forms=@()
foreach($screen in [Windows.Forms.Screen]::AllScreens) {
    $form=New-Object CaptureTestScene($screen.Bounds,(Join-Path $EvidenceDirectory 'scene.stop'),$MaxSeconds)
    $form.Show()
    $forms+=$form
}
[Windows.Forms.Application]::DoEvents()
@{ ready=$true; input='test-owned moving grid, frame counter and barcode';
   audio='disabled'; screens=@([Windows.Forms.Screen]::AllScreens|ForEach-Object{
       @{device=$_.DeviceName;left=$_.Bounds.Left;top=$_.Bounds.Top;width=$_.Bounds.Width;height=$_.Bounds.Height}
   }) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'scene-ready.json') -Encoding UTF8
try {
    while (@($forms|Where-Object{-not $_.IsDisposed}).Count -gt 0) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 10
    }
} finally { foreach($form in $forms) { $form.Dispose() } }
