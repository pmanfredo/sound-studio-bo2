"""Generates src/SoundStudio.App/Assets/app.ico: a rounded tile with a waveform.

Run: python tools/make_icon.py   (needs Pillow)
"""
import math
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "SoundStudio.App", "Assets", "app.ico")


def render(size: int) -> Image.Image:
    scale = 4  # supersample for smooth edges
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Background: dark tile with a warm orange-to-amber vertical gradient.
    radius = int(s * 0.22)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=255)
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    top, bottom = (242, 118, 84), (245, 165, 36)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    img.paste(grad, (0, 0), mask)

    # Waveform bars.
    bars = 7
    heights = [0.28, 0.5, 0.78, 0.6, 0.85, 0.45, 0.3]
    margin = s * 0.18
    width = s - 2 * margin
    pitch = width / bars
    bar_w = pitch * 0.58
    mid = s / 2
    for i, h in enumerate(heights):
        x0 = margin + i * pitch + (pitch - bar_w) / 2
        half = h * s * 0.33
        d.rounded_rectangle([x0, mid - half, x0 + bar_w, mid + half], radius=bar_w / 2, fill=(255, 255, 255, 245))

    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    big = render(256)
    big.save(OUT, sizes=[(n, n) for n in sizes])
    big.save(OUT.replace(".ico", ".png"))
    print("wrote", os.path.normpath(OUT))
