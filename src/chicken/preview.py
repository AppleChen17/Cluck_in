"""Local preview for MVP moods. Not the product — Console is."""

from __future__ import annotations

import tkinter as tk
from tkinter import ttk

from PIL import Image, ImageTk

from state_machine import FPS, FRAMES_DIR, ChickenAnim

KEY = 96
GAP = 6
# 九宮格 1–9 由左到右、由上到下。第 5 格雞、第 6 格巢。
CHICKEN_KEY = 4
NEST_KEY = 5
KEY_BG = (231, 240, 200, 255)


def _key_image(src: Image.Image | None) -> Image.Image:
    canvas = Image.new("RGBA", (KEY, KEY), KEY_BG)
    if src is None:
        return canvas
    # 幀是 64x128、角色在底部；方鍵只取下半，跟 LCD 一鍵一圖比較像。
    fitted = src.resize((KEY, KEY * src.height // src.width), Image.Resampling.NEAREST)
    if fitted.height > KEY:
        fitted = fitted.crop((0, fitted.height - KEY, KEY, fitted.height))
        canvas.paste(fitted, (0, 0), fitted)
    else:
        y = KEY - fitted.height
        canvas.paste(fitted, (0, y), fitted)
    return canvas


class PreviewApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.anim = ChickenAnim()
        root.title("Cluck In · mood preview")
        root.configure(bg="#f4efe4")

        self.status = tk.StringVar()
        self.file_name = tk.StringVar()
        self.photos: list[ImageTk.PhotoImage] = [None] * 9  # type: ignore[list-item]
        self.keys: list[tk.Label] = []

        tk.Label(root, textvariable=self.status, font=("Segoe UI", 14), bg="#f4efe4").pack(pady=(16, 4))
        tk.Label(root, textvariable=self.file_name, font=("Consolas", 10), bg="#f4efe4", fg="#666").pack()

        grid = tk.Frame(root, bg="#f4efe4")
        grid.pack(padx=24, pady=16)
        blank = ImageTk.PhotoImage(_key_image(None))
        for i in range(9):
            label = tk.Label(grid, image=blank, bg="#d9e4b8", bd=0)
            label.grid(row=i // 3, column=i % 3, padx=GAP, pady=GAP)
            self.keys.append(label)
        self._blank = blank

        nest_path = FRAMES_DIR / "nest_icon.png"
        nest_src = Image.open(nest_path).convert("RGBA") if nest_path.exists() else None
        self._nest_photo = ImageTk.PhotoImage(_key_image(nest_src))
        self.keys[NEST_KEY].configure(image=self._nest_photo)

        bar = ttk.Frame(root)
        bar.pack(pady=8)
        actions = [
            ("FOCUS", "START_FOCUS"),
            ("IDLE", "STOP_FOCUS"),
            ("PET", "PET_CHICKEN"),
            ("FEED", "FEED_CHICKEN"),
            ("THINKING", "SET_MOOD"),
        ]
        for label, event in actions:
            ttk.Button(bar, text=label, command=lambda e=event: self.fire(e)).pack(side=tk.LEFT, padx=4)

        self.refresh()
        root.after(self._delay(), self.loop)

    def fire(self, event: str) -> None:
        if event == "SET_MOOD":
            self.anim.handle_event("SET_MOOD", {"mood": "thinking"})
        else:
            self.anim.handle_event(event)

    def _delay(self) -> int:
        fps = max(FPS.get(self.anim.mood, 2), 1)
        return int(1000 / fps)

    def loop(self) -> None:
        self.anim.handle_event("tick")
        self.refresh()
        self.root.after(self._delay(), self.loop)

    def refresh(self) -> None:
        view = self.anim.get_view()
        extra = f"  nest feed={view['feedCount']}"
        self.status.set(f"{view['mood']}  ·  {view['fps']} fps{extra}")
        self.file_name.set(f"{view['chicken']}  ·  nest={view['nestIcon']} @6")
        path = FRAMES_DIR / view["chicken"]
        if not path.exists():
            return
        image = Image.open(path).convert("RGBA")
        photo = ImageTk.PhotoImage(_key_image(image))
        self.photos[CHICKEN_KEY] = photo
        self.keys[CHICKEN_KEY].configure(image=photo)


if __name__ == "__main__":
    if not FRAMES_DIR.exists() or not any(FRAMES_DIR.glob("idle_*.png")):
        raise SystemExit("frames/ is empty. Run: python src/chicken/slice_sprites.py")
    win = tk.Tk()
    PreviewApp(win)
    win.mainloop()
