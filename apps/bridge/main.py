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

from astrbot.api.event import filter
from astrbot.api.star import Context, Star, register

from .adapter import (
    QueueTurnBridge,
    TurnBridge,
    capture_result_text,
    is_game_event,
    run_turn,
    set_full_reply,
    skip_platform_tts,
    suppress_external_splitter,
)
from .hastsec import HastSecClient
from .session import DEFAULT_UMO, SESSION_MODES, resolve_umo, umo_for_mode
from .token import load_token, token_path
from .usage import SessionUsage
from .tts import TtsClient
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
        self.tts: TtsClient | None = None
        self.usage = SessionUsage(DATA_DIR / "mas3d" / "usage.json")

    async def initialize(self) -> None:
        self.bridge = self._build_bridge()
        if self.bridge is None:
            self.degraded = True
            logger.warning(
                "mas3d_game %s 拿不到 AstrBot 的事件队列，对话桥接暂不可用",
                __version__,
            )
        self.hast = HastSecClient(session_path=DATA_DIR / "mas3d" / "session.json")
        self.tts = TtsClient(DATA_DIR / "mas3d" / "cache" / "tts")
        error = getattr(self.bridge, "last_error", None)
        if error:
            logger.warning("mas3d_game 桥接初始化有问题：%s", error)
        await self._start_web_api()
        logger.info(
            "mas3d_game %s loaded (umo=%s, mode=%s, sharedTurns=%s)",
            __version__,
            self.current_umo(),
            self.usage.mode,
            self.usage.shared_turns,
        )

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

    # ---------------------------------------------------------------- 回复捕获
    # 游戏回合和 QQ 共用同一个 umo，所以 astrbot_plugin_splitter 也会处理它：
    # 它把回复切成多段、每段绕过事件直接往 QQ 会话发，还会把 result 改写成
    # 只剩最后一段（见插件 main.py:213/522-547）。下面两个钩子分别做两件事：
    #   1. 在切分之前把模型的整轮文本记到事件上，run_turn 直接取用；
    #   2. 给 result 打上该插件的私有标记，让它整条跳过，避免分段与 QQ 外泄。

    @filter.on_llm_response()
    async def _remember_llm_text(self, event: Any, response: Any) -> None:
        """拿到模型整轮输出（含全部段落），比逐块拼接可靠。"""
        if not is_game_event(event):
            return
        text = getattr(response, "completion_text", "") or ""
        if isinstance(text, str):
            set_full_reply(event, text)

    @filter.on_decorating_result()
    async def _freeze_reply(self, event: Any) -> None:
        """把钩子链里的最终整段文本存下来，并挡住外部分段插件。"""
        if not is_game_event(event):
            return
        getter = getattr(event, "get_result", None)
        result = None
        if callable(getter):
            try:
                result = getter()
            except Exception:  # noqa: BLE001 - 取不到就只按块拼接
                result = None
        if result is None:
            return
        text = capture_result_text(result)
        if text.strip():
            set_full_reply(event, text)
        suppress_external_splitter(result)
        skip_platform_tts(result)

    def current_umo(self) -> str:
        """这一回合要用的会话 id：共享 QQ 会话，或游戏专用的那条。"""
        return umo_for_mode(self.usage.mode, self.umo)

    def session_snapshot(self) -> dict[str, Any]:
        """给 /health 与 /session 用：当前模式、已写入回合数、在用的 umo。"""
        snapshot = self.usage.snapshot()
        snapshot["umo"] = self.current_umo()
        snapshot["configuredUmo"] = self.umo
        return snapshot

    def set_session_mode(self, mode: str) -> dict[str, Any]:
        """切共享 / 专用会话；不合法抛 ValueError（端点翻成 400）。"""
        value = (mode or "").strip().lower()
        if value not in SESSION_MODES:
            raise ValueError(f"会话模式只能是 {' / '.join(SESSION_MODES)}")
        self.usage.set_mode(value)
        logger.info("mas3d_game 会话模式切到 %s（umo=%s）", value, self.current_umo())
        return self.session_snapshot()

    async def handle_turn(self, text: str) -> str:
        """供 Task 12 的 Web API 调用：跑一回合并返回 Monika 的回复文本。"""
        if self.bridge is None:
            raise RuntimeError("桥接未初始化")
        reply = await run_turn(self.bridge, text, umo=self.current_umo())
        self.usage.record_turn()
        return reply

    async def handle_plan(self, text: str, context: Any = ()) -> dict[str, Any]:
        """供动作端点 / WS 调用：问 HAST-SEC 要一份动作计划（不可用时由调用方降级）。"""
        if self.hast is None:
            raise RuntimeError("动作后端未初始化")
        return await self.hast.plan(text, context or ())

    async def handle_voice(self, text: str, sentence: int | None = None) -> dict[str, Any]:
        """供语音端点调用：把一句（或整轮）台词合成成 WAV 并给出逐句时间线。"""
        if self.tts is None:
            raise RuntimeError("语音后端未初始化")
        return await self.tts.synthesize(text, sentence)

    def voice_path(self, voice_id: str) -> Path | None:
        """把 voice_id 映射到缓存里的 WAV 路径；不合法或不存在都返回 None。"""
        if self.tts is None:
            return None
        return self.tts.wav_path(voice_id)


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
