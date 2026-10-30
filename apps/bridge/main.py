"""Mas3D 太空教室的 AstrBot 桥接插件。

职责（按任务顺序落地）：
- Task 10：插件骨架，能被 AstrBot 正常加载并打印版本。
- Task 11：构造游戏事件并把 unified_msg_origin 指到 QQ 私聊会话。
- Task 12：在插件进程里自起只绑 127.0.0.1 的 /mas3d/api/* 与 /mas3d/ws，令牌鉴权。
- Task 13/14/15：HAST-SEC 动作意图代理、本地 TTS 与口型时间线。
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

from astrbot.api.star import Context, Star, register

from .adapter import QueueTurnBridge, TurnBridge, run_turn
from .hastsec import HastSecClient
from .session import DEFAULT_UMO, resolve_umo
from .token import load_token, token_path
from .web_api import LocalServer, create_app

PLUGIN_NAME = "astrbot_plugin_mas3d_game"
__version__ = "0.1.0"

WEB_HOST = "127.0.0.1"
WEB_PORT = 8790
DATA_DIR = Path(__file__).resolve().parent / "data"
"""插件自己的数据目录（data/ 已在 .gitignore 里），令牌就放这里。"""

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


def configured_port(config: Any) -> int:
    """本机端点端口；配置里给了合法值就用它，否则 8790。"""
    raw: Any = None
    if isinstance(config, dict):
        raw = config.get("port")
    elif config is not None:
        getter = getattr(config, "get", None)
        if callable(getter):
            raw = getter("port", None)
    try:
        port = int(raw)
    except (TypeError, ValueError):
        return WEB_PORT
    return port if 1024 <= port <= 65535 else WEB_PORT


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
        self.token: str | None = None
        self.server: LocalServer | None = None
        self.hast: HastSecClient | None = None

    async def initialize(self) -> None:
        self.bridge = self._build_bridge()
        if self.bridge is None:
            self.degraded = True
            logger.warning(
                "mas3d_game %s 拿不到 AstrBot 的事件队列，对话桥接暂不可用",
                __version__,
            )
        self.hast = HastSecClient(session_path=DATA_DIR / "mas3d" / "session.json")
        error = getattr(self.bridge, "last_error", None)
        if error:
            logger.warning("mas3d_game 桥接初始化有问题：%s", error)
        await self._start_web_api()
        logger.info("mas3d_game %s loaded (umo=%s)", __version__, self.umo)

    async def terminate(self) -> None:
        if self.server is not None:
            await self.server.stop()
            self.server = None
        self.bridge = None
        logger.info("mas3d_game %s unloaded", __version__)

    async def _start_web_api(self) -> None:
        """起本机端点；失败只降级并告警，不影响 AstrBot 自己的 QQ 链路。"""
        if self.context is None:
            # 只有真实 AstrBot 加载时才需要端点；单测里不占端口、不写令牌文件
            return
        self.token = load_token(DATA_DIR)
        port = configured_port(self.config)
        server = LocalServer(create_app(self, self.token, plugin_version=__version__), port=port)
        if await server.start():
            self.server = server
            logger.info(
                "mas3d_game 本机端点已就绪：%s/mas3d/api/health，WS %s/mas3d/ws，令牌文件 %s",
                server.url,
                server.url.replace("http", "ws"),
                token_path(DATA_DIR),
            )
            return
        self.degraded = True
        logger.warning("mas3d_game 本机端点没起来：%s", server.error)

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

    async def handle_plan(self, text: str, context: Any = ()) -> dict[str, Any]:
        """供动作端点 / WS 调用：问 HAST-SEC 要一份动作计划（不可用时由调用方降级）。"""
        if self.hast is None:
            raise RuntimeError("动作后端未初始化")
        return await self.hast.plan(text, context or ())


__all__ = [
    "DATA_DIR",
    "DEFAULT_UMO",
    "Mas3dGamePlugin",
    "PLUGIN_NAME",
    "WEB_HOST",
    "WEB_PORT",
    "configured_port",
    "configured_umo",
    "run_turn",
    "__version__",
]
