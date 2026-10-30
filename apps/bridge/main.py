"""Mas3D 太空教室的 AstrBot 桥接插件。

职责（按任务顺序落地）：
- Task 10：插件骨架，能被 AstrBot 正常加载并打印版本。
- Task 11：构造游戏事件并把 unified_msg_origin 指到 QQ 私聊会话。
- Task 12：注册 /mas3d/api/* 与 /mas3d/ws 的本机端点，令牌鉴权。
- Task 13/14/15：HAST-SEC 动作意图代理、本地 TTS 与口型时间线。
"""

from __future__ import annotations

import logging

from astrbot.api.star import Context, Star, register

PLUGIN_NAME = "astrbot_plugin_mas3d_game"
__version__ = "0.1.0"

logger = logging.getLogger("mas3d_game")


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

    async def initialize(self) -> None:
        # Task 12 会在这里注册本机 HTTP/WS 端点
        logger.info("mas3d_game %s loaded", __version__)

    async def terminate(self) -> None:
        logger.info("mas3d_game %s unloaded", __version__)
