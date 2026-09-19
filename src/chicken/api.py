"""HTTP face for Main. Run: uvicorn api:app --app-dir src/chicken --port 8001"""

from __future__ import annotations

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

from threading import RLock
from state_machine import FRAMES_DIR, ChickenAnim
from chicken_statistics import ChickenStatistics, default_state_path
from animation_layout import idle_crop

_anim = ChickenAnim(ChickenStatistics(default_state_path()))
_lock = RLock()

app = FastAPI(title="Cluck In Chicken", version="0.1.0")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

if FRAMES_DIR.exists():
    app.mount("/frames", StaticFiles(directory=FRAMES_DIR), name="frames")


class EventBody(BaseModel):
    type: str
    payload: dict = Field(default_factory=dict)


@app.get("/health")
def health() -> dict:
    return {"ok": True}


@app.get("/view")
def view() -> dict:
    with _lock:
        return _view(_anim.get_view())


def _view(data):
    data["chickenUrl"] = f"/frames/{data['chicken']}"
    data["idleCrop"] = idle_crop()
    return data


@app.post("/event")
def event(body: EventBody) -> dict:
    with _lock:
        try:
            return _view(_anim.handle_event(body.type, body.payload))
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc
