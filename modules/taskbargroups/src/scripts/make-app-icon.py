"""Generate TaskbarGroups/app.ico: a taskbar strip with a flyout panel of grouped icons."""
from PIL import Image, ImageDraw
import os

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Teal tile
d.rounded_rectangle((32, 32, S - 32, S - 32), radius=210, fill=(14, 116, 144, 255))

# Taskbar strip along the bottom, clipped to the tile shape
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((32, 32, S - 32, S - 32), radius=210, fill=255)
strip = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(strip).rectangle((0, 770, S, S), fill=(8, 47, 73, 255))
strip.putalpha(Image.composite(strip.getchannel("A"), Image.new("L", (S, S), 0), mask))
img.alpha_composite(strip)
d = ImageDraw.Draw(img)
for i, x in enumerate((250, 512, 774)):
    d.ellipse((x - 46, 850, x + 46, 942), fill=(255, 255, 255, 255) if i == 1 else (125, 170, 195, 255))

# Flyout panel above the middle taskbar icon, with a pointer notch
d.rounded_rectangle((130, 150, S - 130, 660), radius=90, fill=(240, 249, 255, 255))
d.polygon([(450, 650), (574, 650), (512, 730)], fill=(240, 249, 255, 255))

# Three grouped app tiles inside the flyout
colors = [(251, 146, 60), (52, 211, 153), (167, 139, 250)]
tile, gap = 190, 52
x0 = (S - (3 * tile + 2 * gap)) // 2
for i, c in enumerate(colors):
    x = x0 + i * (tile + gap)
    d.rounded_rectangle((x, 305, x + tile, 305 + tile), radius=48, fill=c + (255,))

out = os.path.join(os.path.dirname(__file__), "..", "TaskbarGroups", "app.ico")
img.save(out, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
img.resize((256, 256), Image.LANCZOS).save(os.path.join(os.environ.get("TEMP", "."), "app_preview.png"))
print("wrote", os.path.abspath(out))
