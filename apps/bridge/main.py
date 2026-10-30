"""Mas3D 太空教室的 AstrBot 桥接插件。

职责（按任务顺序落地）：
- Task 10：插件骨架，能被 AstrBot 正常加载并打印版本。
- Task 11：构造游戏事件并把 unified_msg_origin 指到 QQ 私聊会话。
- Task 12：注册 /mas3d/api/* 与 /mas3d/ws 的本机端点，令牌鉴权。
- Task 13/14/15：HAST-SEC 动作意图代理、本地 TTS 与口型时间线。
"""

from __future__ import annotations

import logging
from typing import Any

from astrbot.api.star import Context, Star, register

from .adapter import QueueTurnBridge, TurnBridge, run_turn
from .session import DEFAULT_UMO, resolve_umo

PLUGIN_NAME = "astrbot_plugin_mas3d_game"
__version__ = "0.1.0"

logger = logging.getLogger("mas3d_game")


def configured_umo(config: Any) -> str:
    """从插件配置里读 umo；配置缺失或不合法时回落到 QQ 私聊会话。"""
    raw = None
    if isinstance(config, dict):
        raw = config.get("umo")
    elif config is not None:
        getter = getattr(config, "get", None)
        if callable(getter):
            raw = getter("umo", None)
    return resolve_umo(raw if isinstance(raw, str) else None)


@register(
    PLUGIN_NAME,
    "Mirrorain288",
    "Mas3D 太空教室：把浏览器里的对话接进 AstrBot 的 Monika 会话，并回传表情与动作意图。",
    __version__,
)
class Mas3dGamePlugin(Star):
    """插件入口。真正的桥接逻辑在 adapter / web_api / hastsec / tts 模块里。"""

    def __init__(self, context: Context, config=None):
        super().__init__(context)
        self.config = config
        self.degraded = False
        self.umo = configured_umo(config)
        self.bridge: TurnBridge | None = None

    async def initialize(self) -> None:
        self.bridge = self._build_bridge()
        if self.bridge is None:
            self.degraded = True
            logger.warning(
                "mas3d_game %s 拿不到 AstrBot 的事件队列，对话桥接暂不可用",
                __version__,
            )
        error = getattr(self.bridge, "last_error", None)
        if error:
            logger.warning("mas3d_game 桥接初始化有问题：%s", error)
        logger.info("mas3d_game %s loaded (umo=%s)", __version__, self.umo)

    async def terminate(self) -> None:
        self.bridge = None
        logger.info("mas3d_game %s unloaded", __version__)

    def _build_bridge(self) -> QueueTurnBridge | None:
        context = self.context
        getter = getattr(context, "get_event_queue", None)
        if not callable(getter):
            return None
        try:
            queue = getter()
        except Exception as exc:  # noqa: BLE001 - 上下文缺失不应让插件加载失败
            logger.warning("读取 AstrBot 事件队列失败：%s", exc)
            return None
        if queue is None:
            return None
        return QueueTurnBridge(queue, umo=self.umo)

    async def handle_turn(self, text: str) -> str:
        """供 Task 12 的 Web API 调用：跑一回合并返回 Monika 的回复文本。"""
        if self.bridge is None:
            raise RuntimeError("桥接未初始化")
        return await run_turn(self.bridge, text)


__all__ = [
    "DEFAULT_UMO",
    "Mas3dGamePlugin",
    "PLUGIN_NAME",
    "configured_umo",
    "run_turn",
    "__version__",
]
