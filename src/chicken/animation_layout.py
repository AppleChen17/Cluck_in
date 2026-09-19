"""One shared transparent-padding crop for every Idle, pet and feed frame."""
from functools import lru_cache
from PIL import Image
from state_machine import FRAMES_DIR


@lru_cache(maxsize=1)
def idle_crop():
    boxes = []
    sizes = set()
    for mood in ("idle", "pet", "feed"):
        for path in FRAMES_DIR.glob(f"{mood}_*.png"):
            with Image.open(path) as image:
                sizes.add(image.size)
                box = image.convert("RGBA").getchannel("A").getbbox()
                if box:
                    boxes.append(box)
    if len(sizes) != 1 or not boxes:
        raise ValueError("Idle animation frames must share a non-empty canvas")
    width, height = sizes.pop()
    left = max(0, min(b[0] for b in boxes) - 2)
    top = max(0, min(b[1] for b in boxes) - 2)
    right = min(width, max(b[2] for b in boxes) + 2)
    bottom = min(height, max(b[3] for b in boxes) + 2)
    return {"x": left, "y": top, "width": right - left, "height": bottom - top}
