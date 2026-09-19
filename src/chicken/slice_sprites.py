"""Cut MVP moods from the Cup Nooble sheet. Spec §10: idle / focused / thinking."""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
SHEET = ROOT / "chicken default.png"
FREE_SHEET = ROOT / "Free Chicken Sprites.png"
NEST_SHEET = ROOT / "Egg_And_Nest.png"
DESK_SHEET = ROOT / "work_station.png"
MILK_SHEET = ROOT / "Milk and grass item Simple.png"
HOUSE_SHEET = ROOT / "Trees, stumps and bushes.png"
EGG_SHEET = ROOT / "Egg_Spritesheet.png"
FRAMES = ROOT / "frames"
# 最下排第 3 個樹幹（左起兩顆小樁之後）
HOUSE_BOX = (38, 99, 56, 111)
HOUSE_SCALE = 0.45  # 目前樹幹再縮成 1/2
HOUSE_LIFT = 4
EGG_ROW = 1  # 01_0 … 01_8
EGG_COLS = 9
EGG_SIT = 4  # 蛋往下坐進樹幹頂

TILE = 16
SCALE = 2  # 16 -> 32，雞是原本的 1/2，方鍵才畫得下愛心
CANVAS = (64, 128)  # 畫布不變，角色縮小後上頭才有空間

# (row, col) on the 16x16 grid
FLY_COLS = (1, 3, 5, 7)  # 飛：17_01 17_03 17_05 17_07，裁切從 17.5 開始
CLIPS: dict[str, list[tuple[int, int]]] = {
    "start": [(17, col) for col in FLY_COLS],  # 書桌前、沒牛奶
    "paused": [(17, col) for col in FLY_COLS],  # 書桌前、有牛奶
    "focused": [(7, 0), (7, 1), (7, 2), (7, 3), (7, 4)],  # 07_0 … 07_4 在巢裡
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


def _bottom(tile: Image.Image, lift: int = 0) -> Image.Image:
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    x = (CANVAS[0] - tile.width) // 2
    y = CANVAS[1] - tile.height - lift
    canvas.paste(tile, (x, y), tile)
    return canvas


def _desk(*, flip: bool = False) -> Image.Image:
    desk = Image.open(DESK_SHEET).convert("RGBA")
    if flip:
        desk = desk.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
    side = int(TILE * SCALE * 1.5)
    return desk.resize((side, side), Image.Resampling.NEAREST)


def _desk_xy(*, flip: bool = False) -> tuple[Image.Image, int, int]:
    desk = _desk(flip=flip)
    dx = (CANVAS[0] - desk.width) // 2
    dy = CANVAS[1] - desk.height - 8
    return desk, dx, dy


def _milk() -> Image.Image:
    """0_02 那瓶牛奶（列 0、第 3 格）。"""
    sheet = Image.open(MILK_SHEET).convert("RGBA")
    return sheet.crop((TILE * 2, 0, TILE * 3, TILE))


def _paste_milk(canvas: Image.Image, desk: Image.Image, dx: int, dy: int) -> None:
    if not MILK_SHEET.exists():
        return
    milk = _milk()
    canvas.paste(
        milk,
        (dx + desk.width - milk.width - 2, dy + 36 - milk.height),
        milk,
    )


def _empty_desk() -> Image.Image:
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    desk, dx, dy = _desk_xy()
    canvas.paste(desk, (dx, dy), desk)
    return canvas


def _with_desk(placed: Image.Image) -> Image.Image:
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    desk, dx, dy = _desk_xy()
    canvas.paste(desk, (dx, dy), desk)
    canvas.paste(placed, (0, 0), placed)
    return canvas


def _with_desk_milk(placed: Image.Image) -> Image.Image:
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    desk, dx, dy = _desk_xy()
    canvas.paste(desk, (dx, dy), desk)
    canvas.paste(placed, (0, 0), placed)
    _paste_milk(canvas, desk, dx, dy)
    return canvas


def _nest_tile() -> Image.Image:
    nest = Image.open(NEST_SHEET).convert("RGBA")
    nest = nest.crop((TILE * 3, 0, TILE * 4, TILE))  # 00_3 空巢
    side = int(TILE * SCALE * 1.2)
    return nest.resize((side, side), Image.Resampling.NEAREST)


def _house_tile() -> Image.Image:
    house = Image.open(HOUSE_SHEET).convert("RGBA").crop(HOUSE_BOX)
    # 預覽方鍵只取畫布下半 64px；再縮到 HOUSE_SCALE
    max_h = CANVAS[1] // 2
    max_w = CANVAS[0]
    fit = min(max_w / house.width, max_h / house.height) * HOUSE_SCALE
    w = max(1, round(house.width * fit))
    h = max(1, round(house.height * fit))
    return house.resize((w, h), Image.Resampling.NEAREST)


def _stump_xy(stump: Image.Image) -> tuple[int, int]:
    x = (CANVAS[0] - stump.width) // 2
    y = CANVAS[1] - stump.height - HOUSE_LIFT
    return x, y


def _egg_tile(sheet: Image.Image, col: int) -> Image.Image:
    box = (col * TILE, EGG_ROW * TILE, (col + 1) * TILE, (EGG_ROW + 1) * TILE)
    return sheet.crop(box).resize((TILE * SCALE, TILE * SCALE), Image.Resampling.NEAREST)


def _house_with_egg(egg: Image.Image | None = None) -> Image.Image:
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    stump = _house_tile()
    sx, sy = _stump_xy(stump)
    canvas.paste(stump, (sx, sy), stump)
    if egg is None:
        return canvas
    # 蛋本體大約在 16 格 y=13；2 倍後底在 26，再往下坐 EGG_SIT
    ex = (CANVAS[0] - egg.width) // 2
    ey = sy - 13 * SCALE + EGG_SIT
    canvas.paste(egg, (ex, ey), egg)
    return canvas


def _in_nest(tile: Image.Image) -> Image.Image:
    """小雞坐進第六格的鳥巢（巢在後、雞在前）。"""
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    nest = _nest_tile()
    nx = (CANVAS[0] - nest.width) // 2
    ny = CANVAS[1] - nest.height
    canvas.paste(nest, (nx, ny), nest)
    cx = (CANVAS[0] - tile.width) // 2
    cy = CANVAS[1] - tile.height - 6
    canvas.paste(tile, (cx, cy), tile)
    return canvas


def _fly_placed(sheet: Image.Image, col: int) -> Image.Image:
    """飛的格子從 17.5 起剪 16x16，不放大。"""
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


_QUESTION_OUTLINE = (
    (-1, 0),
    (1, 0),
    (0, -1),
    (0, 1),
)


def _draw_question(base: Image.Image, bob: int) -> Image.Image:
    canvas = Image.new("RGBA", base.size, (0, 0, 0, 0))
    canvas.paste(base, (0, 5), base)
    draw = ImageDraw.Draw(canvas)
    cell = 1
    origin_x = 22
    origin_y = 1 + bob  # 留 1px 給白邊
    ink = (32, 32, 32, 255)
    rim = (255, 255, 255, 255)
    ink_set = set(_QUESTION_CELLS)
    outline = {
        (cx + dx, cy + dy)
        for cx, cy in _QUESTION_CELLS
        for dx, dy in _QUESTION_OUTLINE
        if (cx + dx, cy + dy) not in ink_set
    }

    def _dot(cx: int, cy: int, color: tuple[int, int, int, int]) -> None:
        x0 = origin_x + cx * cell
        y0 = origin_y + cy * cell
        if x0 < 0 or y0 < 0 or x0 >= canvas.width or y0 >= canvas.height:
            return
        draw.rectangle((x0, y0, x0 + cell - 1, y0 + cell - 1), fill=color)

    for cx, cy in outline:
        _dot(cx, cy, rim)
    for cx, cy in _QUESTION_CELLS:
        _dot(cx, cy, ink)
    return canvas


def slice_sheet(sheet_path: Path = SHEET, out_dir: Path = FRAMES) -> list[Path]:
    if not sheet_path.exists():
        raise FileNotFoundError(f"Missing sprite sheet: {sheet_path}")

    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob("*.png"):
        if old.stem.startswith("work"):
            continue
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
        if mood in {"start", "paused"}:
            compose = _with_desk_milk if mood == "paused" else _with_desk
            for i, col in enumerate(FLY_COLS):
                path = out_dir / f"{mood}_{i:02d}.png"
                placed = _fly_placed(sheet, col)
                if DESK_SHEET.exists():
                    placed = compose(placed)
                placed.save(path)
                written.append(path)
            continue
        if mood == "focused":
            for i, (row, col) in enumerate(cells):
                box = (col * TILE, row * TILE, (col + 1) * TILE, (row + 1) * TILE)
                path = out_dir / f"{mood}_{i:02d}.png"
                placed = _scale(sheet.crop(box))
                if NEST_SHEET.exists():
                    placed = _in_nest(placed)
                else:
                    placed = _bottom(placed)
                placed.save(path)
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
            placed = _bottom(tile)
            if mood == "pet" and DESK_SHEET.exists():
                placed = _with_desk(placed)
            placed.save(path)
            written.append(path)

    idle_tiles = []
    for col in (0, 1):
        box = (col * TILE, 0, (col + 1) * TILE, TILE)
        idle_tiles.append(_scale(sheet.crop(box)))
    for i, (src, bob) in enumerate(((idle_tiles[0], 0), (idle_tiles[1], 2))):
        path = out_dir / f"thinking_{i:02d}.png"
        placed = _bottom(_draw_question(src, bob))
        if DESK_SHEET.exists():
            placed = _with_desk(placed)
        placed.save(path)
        written.append(path)

    if NEST_SHEET.exists():
        nest_path = out_dir / "nest_icon.png"
        _bottom(_nest_tile()).save(nest_path)
        written.append(nest_path)

    if DESK_SHEET.exists():
        desk_path = out_dir / "desk_empty.png"
        _empty_desk().save(desk_path)
        written.append(desk_path)

    if HOUSE_SHEET.exists():
        if EGG_SHEET.exists():
            eggs = Image.open(EGG_SHEET).convert("RGBA")
            for i in range(EGG_COLS):
                path = out_dir / f"house_{i:02d}.png"
                _house_with_egg(_egg_tile(eggs, i)).save(path)
                written.append(path)
            icon = out_dir / "house_icon.png"
            _house_with_egg(_egg_tile(eggs, 0)).save(icon)
            written.append(icon)
        else:
            house_path = out_dir / "house_icon.png"
            _house_with_egg().save(house_path)
            written.append(house_path)

    return written


if __name__ == "__main__":
    files = slice_sheet()
    print(f"wrote {len(files)} files to {FRAMES}")
    for path in files:
        print(f"  {path.name}")
