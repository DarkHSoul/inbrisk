"""ctypes client for inbrisk_ffi.dll. The DLL talks to the running runtime."""

from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path


def _dll_path() -> Path:
    env = os.environ.get("INBRISK_FFI")
    if env:
        return Path(env)
    root = Path(__file__).resolve().parents[2]
    for name in ("release", "debug"):
        candidate = root / "target" / name / "inbrisk_ffi.dll"
        if candidate.exists():
            return candidate
    raise FileNotFoundError(
        "inbrisk_ffi.dll was not found. Build crates/inbrisk-ffi or set INBRISK_FFI."
    )


_DLL = ctypes.CDLL(str(_dll_path()))
_DLL.inbrisk_status.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
_DLL.inbrisk_status.restype = ctypes.c_ssize_t
_DLL.inbrisk_run_json.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_size_t]
_DLL.inbrisk_run_json.restype = ctypes.c_ssize_t


def _read(call) -> dict:
    buf = ctypes.create_string_buffer(1024 * 1024)
    written = call(buf)
    if written < 0:
        raise RuntimeError("inbrisk_ffi call failed")
    return json.loads(buf.raw[:written].decode("utf-8"))


def status() -> dict:
    return _read(lambda buf: _DLL.inbrisk_status(ctypes.addressof(buf), len(buf)))


def run(plan: dict, dry_run: bool = False) -> dict:
    raw = json.dumps(plan).encode("utf-8")
    return _read(
        lambda buf: _DLL.inbrisk_run_json(raw, 1 if dry_run else 0, ctypes.addressof(buf), len(buf))
    )
