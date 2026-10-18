from pathlib import Path
import re

# Current owner: Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc
ui_root = Path(__file__).resolve().parents[3] / "NVIDIA NodeAPI" / "osc"
p = ui_root / "app.js"
src = p.read_text(encoding="utf-8", errors="replace")

for q in ["QUERY_OSC_REGISTER_CLOSE_EVENT", "QUERY_FULLSCREEN_STATE",
          "QUERY_HTTPSERVER_START", "QUERY_OSC_SET_DISPLAY_RECTS",
          "QUERY_OSC_SET_EXPERIMENTAL"]:
    print("=" * 20, q)
    for m in re.finditer(re.escape(q), src):
        # walk back to find the enclosing cefQuery({...}) — print a wide window
        lo = max(0, m.start() - 500)
        seg = src[lo:m.start() + 500].replace('\n', ' ')
        print(seg)
        print('---')
