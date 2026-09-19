"""Calendar access, behind a backend interface.

Named `gcal` rather than `calendar` on purpose: this module uses flat imports
(uvicorn --app-dir src/external), so a package called `calendar` here would
shadow the standard library module of that name for everything in the process.
"""
