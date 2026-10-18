import re

p = r"C:/My Project/NVIDIA-Shadowplay/Project/Overlay OSC/NVIDIA OSC/osc/app.js"
src = open(p, encoding='utf-8', errors='replace').read()

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
