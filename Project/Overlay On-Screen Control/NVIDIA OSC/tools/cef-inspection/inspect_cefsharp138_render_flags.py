from pathlib import Path
import re

dll = Path.home() / ".nuget" / "packages" / "cefsharp.wpf.netcore" / "138.0.340" / "ref" / "net6.0-windows" / "CefSharp.Wpf.dll"
if not dll.is_file():
    raise SystemExit(f"CefSharp 138 assembly not found: {dll}")
data = dll.read_bytes()
for pat in [b'Hardware', b'WpfIsHardwareAccelerationEnabled', b'EnableIsSoftwareRendering',
            b'SoftwareRendering', b'SharedTexture', b'D3DImage', b'IsHardwareAccelerationEnabled']:
    hits = set(m.group().decode(errors='ignore')
               for m in re.finditer(rb'[A-Za-z0-9_.]{0,40}' + pat + rb'[A-Za-z0-9_.]{0,40}', data))
    if hits:
        print(pat.decode(), '->', sorted(hits)[:8])
