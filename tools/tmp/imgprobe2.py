# scratch 2: per-card layout metrics (not a deliverable)
from PIL import Image

p = r".zwork/attachments/dcb14454-5b4c-4040-9789-9afc4e0b9806/image.png"
im = Image.open(p).convert("RGB")
W, H = im.size
px = im.load()

def lum(c):
    return 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]

cards = [(1, 79), (88, 166), (176, 254)]
for i, (y0, y1) in enumerate(cards, 1):
    pts = [(x, y) for y in range(y0, y1 + 1) for x in range(W) if lum(px[x, y]) > 110]
    icon = [q for q in pts if q[0] < 95]
    text = [q for q in pts if q[0] >= 95]
    def box(v):
        if not v:
            return None
        xs = [q[0] for q in v]
        ys = [q[1] for q in v]
        return (min(xs), max(xs), min(ys), max(ys))
    print(f"card{i} band y={y0}-{y1} h={y1-y0+1}")
    print("   icon bbox", box(icon), "text bbox", box(text))
    if text:
        vals = [lum(px[x, y]) for (x, y) in text]
        print("   text lum max/mean", round(max(vals)), round(sum(vals) / len(vals)))
        rows = sorted({y for (x, y) in text})
        # group rows into lines
        lines = []
        for y in rows:
            if lines and y == lines[-1][-1] + 1:
                lines[-1].append(y)
            else:
                lines.append([y])
        print("   text lines (y-runs)", [(l[0], l[-1], len(l)) for l in lines])
        print("   text left x", min(x for (x, y) in text), "right x", max(x for (x, y) in text))
    # column at right edge inside panel
    edge = [y for y in range(y0, y1 + 1) if lum(px[W - 2, y]) > 60]
    print("   bright px on last two cols", len(edge))

# grey margins around panels
print()
for x in (0, 5, 10, 14, 15, 19, 20, 30):
    print("col", x, "sample y=40", px[x, 40])
print("row0", [px[x, 0] for x in (0, 100, 200, 260)])
print("row263", [px[x, 263] for x in (0, 100, 200, 260)])

# vertical margins: which rows are grey (22) vs black
greyrows = [y for y in range(H) if sum(1 for x in range(W) if lum(px[x, y]) > 15) > W * 0.5]
print("grey rows count", len(greyrows), "first", greyrows[:6], "last", greyrows[-6:])
