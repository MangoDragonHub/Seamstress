# Builds the images in docs/images from raw screenshots: crops, numbered outlines and callouts.
#
#   python3 docs/tools/annotate.py <folder with raw screenshots> docs/images
#
# The raw screenshots come from the application's own capture switches, for example:
#
#   Seamstress --size 1800x1000 --template "T-Shirt" --simulate 300 --select Front --screenshot raw/overview.png
#
#   --template <name>     add a built-in garment (repeatable)      --simulate <frames>  simulate before showing
#   --select <name|seam>  select a piece or garment by name        --tool <tool>        start with that tool
#   --uv                  show the UV layout                       --quads              use quad topology
#   --view checker,wire   3D display options                       --avatar MakeHuman   use that avatar
#   --view prefs|export|bodyshape   capture that dialog instead of the main window
#   --gender <0..1>       use the MakeHuman avatar with that gender (raw files mh_0, mh_0.5, mh_1)
#   --view propsbottom    scroll the properties panel to its end
#
# Raw files expected: overview, arranged, editpoints, sew, uv, quads, layered, bottom, pinch_before (a simulated
# skirt), pinch (the same with --view pinch), mh_0, mh_0.5, mh_1,
# dlg_prefs, dlg_export, dlg_bodyshape. The pixel positions below assume a 1920x1042 capture.
import sys
from PIL import Image, ImageDraw, ImageFont
src, out = sys.argv[1], sys.argv[2]
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
COLORS = ["#ff5a5f", "#ffb400", "#2ec4b6", "#8e7cff", "#5bc0eb", "#f15bb5", "#9bc53d", "#ff8c42", "#e4e6ea"]

def load(name): return Image.open(f"{src}/{name}.png").convert("RGB")
def save(im, name): im.save(f"{out}/{name}.png", optimize=True); print(name, im.size)

def badge(d, x, y, n, color, r=17):
    d.ellipse([x - r, y - r, x + r, y + r], fill=color, outline="#111316", width=3)
    f = ImageFont.truetype(FONT, int(r * 1.15))
    d.text((x, y), str(n), fill="#111316", font=f, anchor="mm")

def outline(im, boxes, width=4, r=17):
    """boxes: (x0, y0, x1, y1, number, badge position 'tl'|'tr'|'bl'|'br'|'t'|'c')"""
    d = ImageDraw.Draw(im)
    for i, (x0, y0, x1, y1, n, where) in enumerate(boxes):
        c = COLORS[i % len(COLORS)]
        d.rounded_rectangle([x0, y0, x1, y1], radius=14, outline=c, width=width)
        px, py = where if isinstance(where, tuple) else {"tl": (x0 + 22, y0 + 22), "tr": (x1 - 22, y0 + 22), "bl": (x0 + 22, y1 - 22), "br": (x1 - 22, y1 - 22),
                  "t": ((x0 + x1) // 2, y0), "b": ((x0 + x1) // 2, y1), "c": ((x0 + x1) // 2, (y0 + y1) // 2)}[where]
        badge(d, px, py, n, c, r)
    return im

def circle(d, x, y, r, color, width=4): d.ellipse([x - r, y - r, x + r, y + r], outline=color, width=width)

def label(d, x, y, text, color="#ffe066", size=17, anchor="lm"):
    f = ImageFont.truetype(FONT, size)
    box = d.textbbox((x, y), text, font=f, anchor=anchor)
    d.rounded_rectangle([box[0] - 7, box[1] - 5, box[2] + 7, box[3] + 5], radius=6, fill="#111316", outline=color, width=2)
    d.text((x, y), text, fill=color, font=f, anchor=anchor)

# 1. Annotated overview of the whole window
im = load("overview")
outline(im, [
    (2, 2, 392, 30, 1, (372, 16)),
    (6, 34, 732, 67, 2, (754, 50)),
    (1548, 34, 1916, 67, 3, (1570, 50)),
    (2, 72, 280, 1016, 4, (140, 560)),
    (284, 72, 801, 1016, 5, (312, 132)),
    (806, 72, 1587, 1016, 6, (1195, 760)),
    (1591, 72, 1918, 1016, 7, (1892, 90)),
    (2, 1019, 1918, 1040, 8, (960, 1028)),
], r=15)
save(im, "overview-annotated")

# 2. Toolbar close-up with a number on every tool
tb = load("overview").crop((0, 30, 740, 69)).resize((1480, 78), Image.LANCZOS)
canvas = Image.new("RGB", (1480, 128), "#1e1f22"); canvas.paste(tb, (0, 0))
d = ImageDraw.Draw(canvas)
for i, x in enumerate([36, 101, 199, 279, 347, 417, 481, 548, 604, 637, 699]):
    badge(d, x * 2, 103, i + 1, COLORS[i % len(COLORS)], 15)
save(canvas, "toolbar")

# 3. 3D view buttons
vb = load("overview").crop((384, 74, 801, 102)).resize((1251, 84), Image.LANCZOS)
canvas = Image.new("RGB", (1251, 134), "#1e1f22"); canvas.paste(vb, (0, 0))
d = ImageDraw.Draw(canvas)
for i, x in enumerate([407, 445, 483, 525, 593, 640, 681, 726, 773]):
    badge(d, (x - 384) * 3, 108, i + 1, COLORS[i % len(COLORS)], 15)
save(canvas, "view-buttons")

# 4. Workflow: arranged, then simulated
save(load("arranged").crop((283, 72, 1588, 1016)), "step-arranged")
save(load("overview").crop((283, 72, 1588, 1016)), "step-simulated")

# 5. Edit points and seams in the 2D view
ep = load("editpoints").crop((806, 380, 1587, 690))
save(ep, "edit-points")
sw = load("sew").crop((806, 380, 1920, 690))
d = ImageDraw.Draw(sw)
sw_off = (806, 380)
save(sw, "seams")
save(load("sew").crop((1590, 72, 1920, 470)), "seam-properties")

# 6. Gizmo with callouts
g = load("overview").crop((330, 290, 760, 640))
d = ImageDraw.Draw(g)
label(d, 215, 34, "arrows: drag to move", "#ffe066", 16, "lm")
d.line([(205, 44), (196, 62)], fill="#ffe066", width=3)
label(d, 18, 318, "rings: drag to rotate", "#ffe066", 16, "lm")
d.line([(118, 300), (128, 262)], fill="#ffe066", width=3)
save(g, "gizmo")

# 7. Panels
save(load("layered").crop((0, 72, 281, 345)).resize((562, 546), Image.LANCZOS), "layers")
save(load("overview").crop((1590, 72, 1920, 345)), "properties-topology")
save(load("overview").crop((1590, 345, 1920, 890)), "properties-arrangement-fabric")
save(load("bottom").crop((1590, 72, 1920, 1016)), "properties-material-uv")

# 8. Features
save(load("quads").crop((283, 72, 1588, 1016)), "topology-quads")
save(load("uv").crop((283, 72, 1588, 1016)), "uv-checker")
save(load("layered").crop((283, 72, 801, 1016)), "layering")
# Pinching: the draped skirt, and the same skirt with its front hem held pulled out
pinch = Image.new("RGB", (518 * 2, 620), "#24262b")
for i, (name, text) in enumerate([("pinch_before", "After simulating"), ("pinch", "Pinch on: front hem dragged outwards")]):
    pinch.paste(load(name).crop((283, 72, 801, 692)), (518 * i, 0))
    label(ImageDraw.Draw(pinch), 518 * i + 259, 70, text, "#ffe066", 16, "mm")
save(pinch, "pinch")

# MakeHuman body shape: gender 0, 0.5 and 1 side by side, each labelled
bodies = Image.new("RGB", (460 * 3, 880), "#24262b")
for i, (g, text) in enumerate([("0", "Gender 0 %  (female)"), ("0.5", "Gender 50 %  (neutral)"), ("1", "Gender 100 %  (male)")]):
    bodies.paste(load("mh_" + g).crop((312, 110, 772, 990)), (460 * i, 0))
    label(ImageDraw.Draw(bodies), 460 * i + 230, 30, text, "#ffe066", 17, "mm")
save(bodies, "makehuman-body-shapes")
for n in ["dlg_prefs", "dlg_export", "dlg_bodyshape"]:
    save(load(n), n.replace("dlg_", "dialog-"))
