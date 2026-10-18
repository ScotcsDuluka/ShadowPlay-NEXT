import re

p = r"C:/My Project/NVIDIA-Shadowplay/Project/Overlay OSC/NVIDIA OSC/osc/app.js"
src = open(p, encoding='utf-8', errors='replace').read()

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
