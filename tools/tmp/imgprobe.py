# scratch: pixel probe of attached screenshot (not a deliverable)
from PIL import Image

p = r".zwork/attachments/dcb14454-5b4c-4040-9789-9afc4e0b9806/image.png"
im = Image.open(p).convert("RGB")
W, H = im.size
px = im.load()
print("size", W, H, "mode", im.mode)

def lum(c):
    return 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]

corner = px[0, 0]
print("corner", corner, "corner_br", px[W - 1, H - 1])

def is_green(c):
    return c[1] > 90 and c[1] > c[0] + 40 and c[1] > c[2] + 40

greencols = [x for x in range(W) if sum(1 for y in range(H) if is_green(px[x, y])) > H * 0.3]
print("green strip columns", greencols)

greenrows = [y for y in range(H) if sum(1 for x in range(W) if is_green(px[x, y])) > W * 0.005]
runs = []
for y in greenrows:
    if runs and y == runs[-1][1] + 1:
        runs[-1][1] = y
    else:
        runs.append([y, y])
print("green strip y-runs", runs)

dark = [y for y in range(H) if sum(1 for x in range(W) if lum(px[x, y]) < 30) > W * 0.5]
pruns = []
for y in dark:
    if pruns and y == pruns[-1][1] + 1:
        pruns[-1][1] = y
    else:
        pruns.append([y, y])
print("dark(panel) y-runs", pruns)

for i, (y0, y1) in enumerate(pruns):
    pts = [(x, y) for y in range(y0, y1 + 1) for x in range(W) if lum(px[x, y]) > 150]
    if pts:
        xs = [q[0] for q in pts]
        ys = [q[1] for q in pts]
        print(f"band{i} y={y0}-{y1} h={y1-y0+1} bright x={min(xs)}-{max(xs)} y={min(ys)}-{max(ys)} touches_right={max(xs)==W-1}")
    else:
        print(f"band{i} y={y0}-{y1} h={y1-y0+1} no bright")

y = (pruns[0][0] + pruns[0][1]) // 2 if pruns else 0
row = [x for x in range(W) if lum(px[x, y]) < 60]
print("panel x extent at band0 mid", min(row), max(row))

same_rows = sum(1 for yy in range(H - 1) if all(px[x, yy] == px[x, yy + 1] for x in range(W)))
same_cols = sum(1 for xx in range(W - 1) if all(px[xx, yy] == px[xx + 1, yy] for yy in range(H)))
print("identical adjacent rows", same_rows, "identical adjacent cols", same_cols)

e = 0
n = 0
for yy in range(H):
    for xx in range(W - 1):
        e += abs(lum(px[xx, yy]) - lum(px[xx + 1, yy]))
        n += 1
print("mean |dx| luminance", round(e / n, 2))

from collections import Counter
c = Counter(px[x, yy] for yy in range(H) for x in range(W))
print("top colors", c.most_common(6))
