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


def test_initialize_shares_one_voice_lock(plugin):
    """TTS 与 ASR 必须拿同一把 asyncio.Lock：本机 GPU 同一时刻只跑一个后端。"""
    instance = plugin.Mas3dGamePlugin(context=None)
    asyncio.run(instance.initialize())

    lock = instance._voice_lock
    assert isinstance(lock, asyncio.Lock)
    assert instance.tts is not None
    assert instance.tts._lock is lock
    assert instance.asr is not None
    assert instance.asr._lock is lock


def test_handle_asr_delegates_to_asr_client(plugin):
    instance = plugin.Mas3dGamePlugin(context=None)
    asyncio.run(instance.initialize())

    class _StubAsr:
        def __init__(self) -> None:
            self.calls: list[bytes] = []

        async def transcribe(self, raw: bytes) -> dict:
            self.calls.append(raw)
            return {"text": "你好", "language": "zh", "durationMs": 10, "model": "small"}

    stub = _StubAsr()
    instance.asr = stub
    result = asyncio.run(instance.handle_asr(b"RIFF....WAVE"))
    assert result["text"] == "你好"
    assert stub.calls == [b"RIFF....WAVE"]


def test_handle_asr_before_initialize_raises(plugin):
    instance = plugin.Mas3dGamePlugin(context=None)
    with pytest.raises(RuntimeError):
        asyncio.run(instance.handle_asr(b"RIFF....WAVE"))


def test_metadata_matches_module(plugin):
    meta = read_metadata()
    assert meta["name"] == plugin.PLUGIN_NAME
    assert meta["version"] == plugin.__version__
    assert meta["astrbot_version"] == ">=4.28,<5"
