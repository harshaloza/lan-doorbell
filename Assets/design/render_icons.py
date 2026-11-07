"""
Renders the SVG artwork into the app's PNG logo and multi-size .ico.

    python Assets/design/render_icons.py [--preview DIR]

Needs Pillow and Google Chrome (or Microsoft Edge), which renders the SVGs headlessly.
Each icon size comes from the artwork drawn for it:
    16 px          <- icon-16.svg  (pixel-grid drawing)
    20, 24, 32 px  <- icon-32.svg  (simplified drawing)
    40 - 256 px    <- logo.svg     (full detail)
"""
import argparse
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
ASSETS = HERE.parent
BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


def browsers() -> list[str]:
    found = [path for path in BROWSERS if os.path.exists(path)]
    if not found:
        raise SystemExit("Google Chrome or Microsoft Edge is needed to render the SVGs.")
    return found


def render(svg: Path, size: int, workdir: Path) -> Image.Image:
    """Renders an SVG to a size x size RGBA image with a transparent background."""
    text = svg.read_text(encoding="utf-8")
    # Scale the drawing by replacing its width/height (the viewBox stays the same).
    head, rest = text.split(">", 1)
    head = re.sub(r'\swidth="[^"]*"', f' width="{size}"', head, count=1)
    head = re.sub(r'\sheight="[^"]*"', f' height="{size}"', head, count=1)
    scaled = workdir / f"{svg.stem}-{size}.svg"
    scaled.write_text(head + ">" + rest, encoding="utf-8")
    out = workdir / f"{svg.stem}-{size}.png"
    for exe in browsers():   # some managed Edge installs silently refuse headless mode
        subprocess.run(
            [
                exe, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                "--force-device-scale-factor=1", "--default-background-color=00000000",
                f"--user-data-dir={workdir / 'profile'}",
                f"--window-size={size},{size}", f"--screenshot={out}", scaled.as_uri(),
            ],
            capture_output=True, timeout=120,
        )
        if out.exists():
            break
    else:
        raise SystemExit(f"No browser could render {svg.name}.")
    image = Image.open(out).convert("RGBA")
    if image.size != (size, size):
        image = image.crop((0, 0, size, size))
    return image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", help="also save every size as a PNG in this folder")
    args = parser.parse_args()

    work = Path(tempfile.mkdtemp(prefix="landoorbell-icons-"))
    try:
        master = render(HERE / "logo.svg", 1024, work)
        small = render(HERE / "icon-32.svg", 512, work)    # 16x the 32 px grid
        tiny = render(HERE / "icon-16.svg", 512, work)     # 32x the 16 px grid

        def shrink(image: Image.Image, size: int, exact_grid: bool = False) -> Image.Image:
            # BOX averaging is exact coverage for drawings made on the target pixel grid.
            return image.resize((size, size), Image.Resampling.BOX if exact_grid else Image.Resampling.LANCZOS)

        icons = {
            16: shrink(tiny, 16, exact_grid=True),
            20: shrink(small, 20),
            24: shrink(small, 24),
            32: shrink(small, 32, exact_grid=True),
            40: shrink(master, 40),
            48: shrink(master, 48),
            64: shrink(master, 64),
            128: shrink(master, 128),
            256: shrink(master, 256),
        }
        largest = icons[256]
        largest.save(
            ASSETS / "LanDoorbell.ico",
            sizes=[(s, s) for s in icons],
            append_images=[img for s, img in icons.items() if s != 256],
        )
        shrink(master, 512).save(ASSETS / "LanDoorbell-Logo.png", optimize=True)

        if args.preview:
            preview = Path(args.preview)
            preview.mkdir(parents=True, exist_ok=True)
            for s, img in icons.items():
                img.save(preview / f"icon-{s}.png")
            master.save(preview / "logo-1024.png")
        print("Wrote", ASSETS / "LanDoorbell.ico", "and", ASSETS / "LanDoorbell-Logo.png")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
