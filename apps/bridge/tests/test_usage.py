"""共享会话的用量计数与会话模式（软上限 + 专用会话切换）。"""

from __future__ import annotations

import json

import pytest


@pytest.fixture(scope="session")
def usage_mod(plugin):
    from astrbot_plugin_mas3d_game import usage as mod

    return mod


@pytest.fixture(scope="session")
def session_mod(plugin):
    from astrbot_plugin_mas3d_game import session as mod

    return mod


def test_defaults_to_shared_with_zero_turns(usage_mod, tmp_path):
    usage = usage_mod.SessionUsage(tmp_path / "usage.json")
    assert usage.mode == "shared"
    assert usage.shared_turns == 0
    assert usage.snapshot() == {"mode": "shared", "sharedTurns": 0, "limit": 60, "overLimit": False}


def test_record_turn_counts_only_in_shared_mode(usage_mod, tmp_path):
    usage = usage_mod.SessionUsage(tmp_path / "usage.json")
    usage.record_turn()
    usage.record_turn()
    assert usage.shared_turns == 2

    usage.set_mode("dedicated")
    usage.record_turn()
    # 专用会话不写 QQ，那份记录不该继续长
    assert usage.shared_turns == 2


def test_mode_is_persisted(usage_mod, tmp_path):
    path = tmp_path / "usage.json"
    usage_mod.SessionUsage(path).set_mode("dedicated")
    assert json.loads(path.read_text(encoding="utf-8")) == {"mode": "dedicated", "sharedTurns": 0}

    reloaded = usage_mod.SessionUsage(path)
    assert reloaded.mode == "dedicated"


def test_rejects_unknown_mode(usage_mod, tmp_path):
    usage = usage_mod.SessionUsage(tmp_path / "usage.json")
    with pytest.raises(ValueError):
        usage.set_mode("cloud")


def test_corrupt_file_falls_back_to_defaults(usage_mod, tmp_path):
    path = tmp_path / "usage.json"
    path.write_text("{not json", encoding="utf-8")
    usage = usage_mod.SessionUsage(path)
    assert usage.mode == "shared"
    assert usage.shared_turns == 0


def test_over_limit_flag(usage_mod, tmp_path):
    usage = usage_mod.SessionUsage(tmp_path / "usage.json", limit=2)
    usage.record_turn()
    assert usage.snapshot()["overLimit"] is False
    usage.record_turn()
    assert usage.snapshot()["overLimit"] is True


def test_umo_for_mode_switches_session(session_mod):
    assert session_mod.umo_for_mode("shared", session_mod.DEFAULT_UMO) == session_mod.DEFAULT_UMO
    assert session_mod.umo_for_mode("dedicated", session_mod.DEFAULT_UMO) == session_mod.DEDICATED_UMO
    # 未知模式按共享处理，避免把玩家悄悄丢进一条空会话
    assert session_mod.umo_for_mode("nonsense", session_mod.DEFAULT_UMO) == session_mod.DEFAULT_UMO
