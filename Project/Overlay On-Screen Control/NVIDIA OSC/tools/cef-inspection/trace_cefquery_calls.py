from pathlib import Path
import re

# Current owner: Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc
ui_root = Path(__file__).resolve().parents[3] / "NVIDIA NodeAPI" / "osc"
p = ui_root / "app.js"
src = p.read_text(encoding="utf-8", errors="replace")

print("=== cefQuery call sites ===")
for m in re.finditer(r'cefQuery\(', src):
    seg = src[m.start():m.start()+300].replace('\n', ' ')
    print(m.start(), seg[:280])
    print('---')

print("=== onCefQueryMessage / native callbacks ===")
for m in re.finditer(r'onCef\w+|cefMessage|cefCancelQuery', src):
    seg = src[max(0, m.start()-60):m.start()+160].replace('\n', ' ')
    print(m.start(), m.group(), '||', seg[:200])
    print('---')
