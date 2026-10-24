"""生成 launcher/assets/app.ico（多尺寸），仅在图标需要更新时运行。"""

from PIL import Image, ImageDraw

SS = 4
SIZE = 256
W = SIZE * SS

img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 圆角方块 + 垂直渐变
radius = int(W * 0.22)
grad = Image.new("RGBA", (W, W))
gd = ImageDraw.Draw(grad)
top = (0x4A, 0x8B, 0xFF)
bottom = (0x1C, 0x47, 0xC4)
for y in range(W):
    t = y / (W - 1)
    color = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    gd.line([(0, y), (W, y)], fill=color)

mask = Image.new("L", (W, W), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, W - 1], radius=radius, fill=255)
img.paste(grad, (0, 0), mask)

# 白色 W
stroke = int(W * 0.078)
pts = [
    (W * 0.255, W * 0.315),
    (W * 0.375, W * 0.695),
    (W * 0.500, W * 0.445),
    (W * 0.625, W * 0.695),
    (W * 0.745, W * 0.315),
]
d = ImageDraw.Draw(img)
d.line(pts, fill=(255, 255, 255, 255), width=stroke, joint="curve")
for p in (pts[0], pts[-1]):
    r = stroke / 2
    d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=(255, 255, 255, 255))

# 右下角在线指示点
dr = int(W * 0.062)
cx, cy = W * 0.755, W * 0.755
d.ellipse([cx - dr, cy - dr, cx + dr, cy + dr], fill=(0x3D, 0xE0, 0x9A, 255))

base = img.resize((SIZE, SIZE), Image.LANCZOS)
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
base.save("app.ico", format="ICO", sizes=[(s, s) for s in sizes])
print("icon written:", base.size)
