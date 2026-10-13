"""Shared path resolution for the tools/ scripts.

Everything resolves from the repository root (the parent of this file's
directory) — no user-specific absolute paths. Environment overrides win:

    INBRISK_ROOT          repo root override
    INBRISK_BASELINE_ROOT baseline repo root (for A/B benchmark scripts)
    INBRISK_MCP_EXE       path to inbrisk-mcp.exe
    INBRISK_MCP_DLL       path to inbrisk-mcp.dll
    INBRISK_EXE           path to the installed inbrisk.exe
    INBRISK_BUILD_CONFIG  "Debug" (default) or "Release"
"""

from __future__ import annotations

import os
from pathlib import Path

TFM = "net8.0-windows10.0.19041.0"


def repo_root() -> Path:
    override = os.environ.get("INBRISK_ROOT")
    if override:
        return Path(override)
    return Path(__file__).resolve().parents[1]


def baseline_root() -> Path:
    override = os.environ.get("INBRISK_BASELINE_ROOT")
    if override:
        return Path(override)
    return repo_root().parent / "inbrisk-baseline"


def build_config() -> str:
    return os.environ.get("INBRISK_BUILD_CONFIG", "Debug")


def mcp_exe(root: Path | None = None, config: str | None = None) -> Path:
    override = os.environ.get("INBRISK_MCP_EXE")
    if override:
        return Path(override)
    root = root or repo_root()
    return root / "src" / "Inbrisk.Mcp" / "bin" / (config or build_config()) / TFM / "inbrisk-mcp.exe"


def mcp_dll(root: Path | None = None, config: str | None = None) -> Path:
    override = os.environ.get("INBRISK_MCP_DLL")
    if override:
        return Path(override)
    root = root or repo_root()
    return root / "src" / "Inbrisk.Mcp" / "bin" / (config or build_config()) / TFM / "inbrisk-mcp.dll"


def cli_exe() -> Path:
    override = os.environ.get("INBRISK_EXE")
    if override:
        return Path(override)
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "Programs" / "Inbrisk" / "inbrisk.exe"
    return Path.home() / "AppData" / "Local" / "Programs" / "Inbrisk" / "inbrisk.exe"


def artifacts_dir(root: Path | None = None) -> Path:
    return (root or repo_root()) / "artifacts"


def tests_dir(root: Path | None = None) -> Path:
    return (root or repo_root()) / "tests" / "Inbrisk.Tests"


# Eagerly-bound module constants for the common cases — most scripts just
# want EXE = str(MCP_EXE).
REPO_ROOT = repo_root()
BASELINE_ROOT = baseline_root()
MCP_EXE = mcp_exe()
MCP_EXE_RELEASE = mcp_exe(config="Release")
MCP_DLL = mcp_dll()
MCP_DLL_RELEASE = mcp_dll(config="Release")
CLI_EXE = cli_exe()
ARTIFACTS = artifacts_dir()
TESTS_DIR = tests_dir()
