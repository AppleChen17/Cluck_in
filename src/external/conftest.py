"""Puts src/external on sys.path for pytest.

The production code uses flat imports (`from schemas import ...`), matching
src/ai-engine, which works because uvicorn is given --app-dir. pytest's default
"prepend" import mode prepends each conftest.py's directory to sys.path, which
reproduces that without changing the production import style.
"""
