"""Task 10 的验收：插件能被加载、版本可读、元数据一致。"""

from __future__ import annotations

import asyncio
import logging
import sys
from pathlib import Path

import pytest

METADATA = Path(__file__).resolve().parents[1] / "metadata.yaml"


def read_metadata() -> dict[str, str]:
    pairs: dict[str, str] = {}
    for line in METADATA.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or ":" not in line:
            continue
        key, _, value = line.partition(":")
        pairs[key.strip()] = value.strip().strip('"')
    return pairs


def test_module_version(plugin):
    assert plugin.__version__ == "0.1.0"
    assert plugin.PLUGIN_NAME == "astrbot_plugin_mas3d_game"


def test_plugin_class_is_registered(plugin):
    assert hasattr(plugin, "Mas3dGamePlugin")
    meta = plugin.Mas3dGamePlugin.__plugin_meta__["args"]
    assert meta[0] == "astrbot_plugin_mas3d_game"
    assert meta[3] == plugin.__version__


def test_initialize_logs_version(plugin, caplog):
    with caplog.at_level(logging.INFO, logger="mas3d_game"):
        instance = plugin.Mas3dGamePlugin(context=None)
        asyncio.run(instance.initialize())
    assert any(
        "mas3d_game 0.1.0 loaded" in record.getMessage() for record in caplog.records
    ), [record.getMessage() for record in caplog.records]


def test_metadata_matches_module(plugin):
    meta = read_metadata()
    assert meta["name"] == plugin.PLUGIN_NAME
    assert meta["version"] == plugin.__version__
    assert meta["astrbot_version"] == ">=4.28,<5"
