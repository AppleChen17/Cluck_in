"""Cut MVP moods from the Cup Nooble sheet. Spec §10: idle / focused / thinking."""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
SHEET = Path(__file__).resolve().parents[2] / "chicken default.png"
FREE_SHEET = Path(__file__).resolve().parents[2] / "Free Chicken Sprites.png"
NEST_SHEET = Path(__file__).resolve().parents[2] / "Egg_And_Nest.png"
FRAMES = ROOT / "frames"

TILE = 16
SCALE = 2  # 16 -> 32，雞是原本的 1/2，方鍵才畫得下愛心
CANVAS = (64, 128)  # 畫布不變，角色縮小後上頭才有空間

# (row, col) on the 16x16 grid
CLIPS: dict[str, list[tuple[int, int]]] = {
    "focused": [(17, 1), (17, 3), (17, 5), (17, 7)],  # 17_01 17_03 17_05 17_07，裁切從 17.5 開始
    "feed": [(12, 0), (12, 1)],  # 用眼睛數第 13 排 = 程式列 12（從 0 起算）
    "pet": [(8, 0), (8, 1), (8, 2), (8, 3)],  # 08_0 壓下、08_2 壓住、08_3+08_0 彈回
    "tired": [(3, 4), (3, 5)],  # schema 保留，MVP 不用
}

# 素材只有兩格時，依序重複寫成更多幀。
REPEATS = {"feed": 5}
# 愛心+小雞是同一格、高兩列（列 25–26），不是只剪愛心。
HEART_CHICK_COLS = (1, 2, 3, 4)
# Free Chicken Sprites.png：01_0 … 01_3（程式列 1）
FREE_IDLE = [(1, 0), (1, 1), (1, 2), (1, 3)]


def _scale(tile: Image.Image) -> Image.Image:
    return tile.resize((TILE * SCALE, TILE * SCALE), Image.Resampling.NEAREST)


def _bottom(tile: Image.Image) -> Image.Image:
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    x = (CANVAS[0] - tile.width) // 2
    y = CANVAS[1] - tile.height
    canvas.paste(tile, (x, y), tile)
    return canvas


def _fly_chick(sheet: Image.Image, col: int) -> Image.Image:
    """飛的格子從 17.5 起剪 16x16，不放大，再跟其他狀態一樣貼底部。"""
    y0 = int(17.5 * TILE)
    box = (col * TILE, y0, (col + 1) * TILE, y0 + TILE)
    return _bottom(_scale(sheet.crop(box)))


def _heart_chick(sheet: Image.Image, col: int) -> Image.Image:
    box = (col * TILE, 25 * TILE, (col + 1) * TILE, 27 * TILE)
    sprite = sheet.crop(box).resize((TILE * SCALE, TILE * SCALE * 2), Image.Resampling.NEAREST)
    return sprite


# 使用者提供的像素問號（相對左上，鉤 + 點）
_QUESTION_CELLS = (
    (1, 0),
    (2, 0),
    (3, 0),
    (0, 1),
    (4, 1),
    (0, 2),
    (4, 2),
    (4, 3),
    (3, 4),
    (2, 5),
    (2, 6),
    (2, 8),
)


def _draw_question(base: Image.Image, bob: int) -> Image.Image:
    canvas = Image.new("RGBA", base.size, (0, 0, 0, 0))
    canvas.paste(base, (0, 5), base)
    draw = ImageDraw.Draw(canvas)
    cell = 1
    origin_x = 22
    origin_y = 0 + bob
    ink = (32, 32, 32, 255)
    for cx, cy in _QUESTION_CELLS:
        x0 = origin_x + cx * cell
        y0 = origin_y + cy * cell
        draw.rectangle((x0, y0, x0 + cell - 1, y0 + cell - 1), fill=ink)
    return canvas


def slice_sheet(sheet_path: Path = SHEET, out_dir: Path = FRAMES) -> list[Path]:
    if not sheet_path.exists():
        raise FileNotFoundError(f"Missing sprite sheet: {sheet_path}")

    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob("*.png"):
        old.unlink()

    sheet = Image.open(sheet_path).convert("RGBA")
    written: list[Path] = []

    free = Image.open(FREE_SHEET).convert("RGBA")
    idle_tiles = []
    for row, col in FREE_IDLE:
        box = (col * TILE, row * TILE, (col + 1) * TILE, (row + 1) * TILE)
        idle_tiles.append(_scale(free.crop(box)))
    for i, tile in enumerate(idle_tiles):
        path = out_dir / f"idle_{i:02d}.png"
        _bottom(tile).save(path)
        written.append(path)

    for mood, cells in CLIPS.items():
        if mood == "focused":
            for i, (_row, col) in enumerate(cells):
                path = out_dir / f"{mood}_{i:02d}.png"
                _fly_chick(sheet, col).save(path)
                written.append(path)
            continue
        tiles = []
        for row, col in cells:
            box = (col * TILE, row * TILE, (col + 1) * TILE, (row + 1) * TILE)
            tiles.append(_scale(sheet.crop(box)))
        cycle = tiles * REPEATS.get(mood, 1)
        if mood == "pet" and len(tiles) >= 4:
            # 08_0 → 08_1 → 08_2×8 → 08_3 → 08_0，壓完再回到原本高度
            cycle = [tiles[0], tiles[1]] + [tiles[2]] * 8 + [tiles[3], tiles[0]]
        if mood == "feed":
            hearts = [_heart_chick(sheet, col) for col in HEART_CHICK_COLS]
            cycle.extend(hearts * 2)
        for i, tile in enumerate(cycle):
            path = out_dir / f"{mood}_{i:02d}.png"
            _bottom(tile).save(path)
            written.append(path)

    idle_tiles = []
    for col in (0, 1):
        box = (col * TILE, 0, (col + 1) * TILE, TILE)
        idle_tiles.append(_scale(sheet.crop(box)))
    for i, (src, bob) in enumerate(((idle_tiles[0], 0), (idle_tiles[1], 2))):
        path = out_dir / f"thinking_{i:02d}.png"
        _bottom(_draw_question(src, bob)).save(path)
        written.append(path)

    if NEST_SHEET.exists():
        nest = Image.open(NEST_SHEET).convert("RGBA")
        nest_tile = _scale(nest.crop((TILE * 3, 0, TILE * 4, TILE)))  # 00_3 空巢
        nest_path = out_dir / "nest_icon.png"
        _bottom(nest_tile).save(nest_path)
        written.append(nest_path)

    return written


if __name__ == "__main__":
    files = slice_sheet()
    print(f"wrote {len(files)} files to {FRAMES}")
    for path in files:
        print(f"  {path.name}")
