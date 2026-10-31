"""共享会话的用量计数与模式切换。

游戏回合和 QQ 共用同一个 unified_msg_origin（design.md D1），每一句都会追加进
同一条会话历史：实测 358 回合就有 1.4 MB 上下文。这里只做两件事——数清游戏写了
多少句、记下当前用哪条会话——既不删历史，也不改 AstrBot 的数据库；提示与切换
都由界面按需触发（openspec specs/bridge/astrbot-session）。
"""

from __future__ import annotations

import json
import os
from pathlib import Path

from .session import DEFAULT_SESSION_MODE, SHARED_TURN_LIMIT, SESSION_MODES, resolve_mode


class SessionUsage:
    """把「用了哪条会话、往里写了多少句」持久化到一个小 JSON。"""

    def __init__(self, path: str | Path, limit: int = SHARED_TURN_LIMIT) -> None:
        self.path = Path(path)
        self.limit = int(limit)
        self._mode = DEFAULT_SESSION_MODE
        self._shared_turns = 0
        self._load()

    @property
    def mode(self) -> str:
        return self._mode

    @property
    def shared_turns(self) -> int:
        return self._shared_turns

    def snapshot(self) -> dict[str, object]:
        """给 health / 会话端点用的快照；overLimit 只用于提示。"""
        return {
            "mode": self._mode,
            "sharedTurns": self._shared_turns,
            "limit": self.limit,
            "overLimit": self._shared_turns >= self.limit,
        }

    def record_turn(self) -> None:
        """记一回合；只有共享模式才计入（专用会话不污染 QQ 那份记录）。"""
        if self._mode != "shared":
            return
        self._shared_turns += 1
        self._save()

    def set_mode(self, mode: str) -> dict[str, object]:
        """切会话模式；不合法的值抛 ValueError（由端点翻成 400）。"""
        value = (mode or "").strip().lower()
        if value not in SESSION_MODES:
            raise ValueError(f"会话模式只能是 {' / '.join(SESSION_MODES)}")
        if value != self._mode:
            self._mode = value
            self._save()
        return self.snapshot()

    # ------------------------------------------------------------------ 持久化
    def _load(self) -> None:
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return
        if not isinstance(raw, dict):
            return
        self._mode = resolve_mode(raw.get("mode"))
        turns = raw.get("sharedTurns")
        if isinstance(turns, int) and turns >= 0:
            self._shared_turns = turns

    def _save(self) -> None:
        payload = {"mode": self._mode, "sharedTurns": self._shared_turns}
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.path.with_suffix(".tmp")
            tmp.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, self.path)
        except OSError:
            # 计数只是提示用，写不进去不该影响对话
            pass


__all__ = ["SessionUsage"]
