"""跨语言契约：同一份 plan 用例必须被 TS 与 Python 两侧的校验器一致判定。"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]
CASES = json.loads((ROOT / "packages" / "protocol" / "test" / "fixtures" / "plan-cases.json").read_text(encoding="utf-8"))["cases"]


def _hastsec_module():
    return sys.modules["astrbot_plugin_mas3d_game.hastsec"]


@pytest.mark.parametrize("case", CASES, ids=[case["name"] for case in CASES])
def test_validate_plan_matches_typescript(case: dict) -> None:
    validate_plan = _hastsec_module().validate_plan
    if case["ok"]:
        plan = validate_plan(case["plan"])
        assert plan["operation"]
        if case["plan"].get("reply", "missing") is None:
            assert "reply" not in plan
    else:
        with pytest.raises(ValueError):
            validate_plan(case["plan"])
