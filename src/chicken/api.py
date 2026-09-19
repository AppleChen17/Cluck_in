"""HTTP face for Main. Run: uvicorn api:app --app-dir src/chicken --port 8001"""

from __future__ import annotations

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

from state_machine import FRAMES_DIR, get_view, handle_event

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
    data = get_view()
    data["chickenUrl"] = f"/frames/{data['chicken']}"
    return data


@app.post("/event")
def event(body: EventBody) -> dict:
    try:
        data = handle_event(body.type, body.payload)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    data["chickenUrl"] = f"/frames/{data['chicken']}"
    return data
