"""把 3D 教室的一回合包装成 AstrBot 事件，并截获回复。

设计要点（openspec/changes/add-monika-space-classroom/design.md D1）：

1. 复用 AstrMessageEvent 子类，覆写 unified_msg_origin 指向真实 QQ 私聊会话，
   于是会话历史、人设、记忆插件、工具全部和 QQ 上的 Monika 共用同一份上下文。
2. send() 被改写成把文本交给回调，不经过 QQ 平台适配器 —— 游戏里看到回复，
   QQ 里不会突然蹦出没头没尾的一句（是否回显由后续设置决定）。
3. unified_msg_origin 的 setter 是 AstrBot 内部 API（astr_message_event.py:112）；
   一旦上游删掉它，降级成独立 umo（mas3d:GameMessage:<uuid>），并置 degraded。
"""

from __future__ import annotations

import asyncio
import time
import uuid
from collections.abc import AsyncGenerator, Awaitable, Callable
from typing import Any

from astrbot.core.message.components import Plain
from astrbot.core.platform.astr_message_event import AstrMessageEvent
from astrbot.core.platform.astrbot_message import AstrBotMessage, MessageMember
from astrbot.core.platform.message_type import MessageType
from astrbot.core.platform.platform_metadata import PlatformMetadata

from .session import (
    DEFAULT_UMO,
    fallback_umo,
    is_valid_umo,
    resolve_umo,
    scene_text,
    strip_scene_prefix,
)

TURN_TIMEOUT_S = 120.0
"""单回合上限：外部大模型很慢时也要给前端一个结束信号。"""

START_TIMEOUT_S = 8.0
"""事件被管线接管的等待上限；超过说明 EventBus 没在跑（例如插件重载中）。"""

POLL_INTERVAL_S = 0.05

GAME_PLATFORM = PlatformMetadata(
    name="mas3d_game",
    description="Mas3D 太空教室（3D 页面）",
    id="mas3d_game",
    support_streaming_message=False,
)
"""事件带的平台元数据。id 故意不叫 Monika，避免任何发送路径落到 QQ 适配器。"""

SendCallback = Callable[[str], Awaitable[None]]

FULL_REPLY_EXTRA = "mas3d_full_reply"
"""整轮回复的完整文本存在事件的 extra 里。"""

SPLITTER_MARK_ATTR = "__splitter_processed"
"""astrbot_plugin_splitter 用来跳过已处理 result 的私有标记。"""


def extra_text(event: Any, key: str) -> str:
    """读事件 extra 里的文本；没有或不是字符串就返回空串。"""
    try:
        value = event.get_extra(key, "")
    except Exception:  # noqa: BLE001 - 老版本事件或替身对象可能没有 extra
        return ""
    return value.strip() if isinstance(value, str) else ""


def set_full_reply(event: Any, text: str) -> None:
    """把整轮回复记到事件上，供 run_turn 直接取用。

    先写者胜：LLM 的 completion_text 是最早、最完整的形态；后续钩子只在它缺席时
    补齐（例如纯工具回合没有 completion_text）。反过来让后写覆盖，就等于把结果链
    当前的长度当成真相——那正是「只拿到最后一段」这个 bug 的成因。
    """
    if not isinstance(text, str) or not text.strip():
        return
    if extra_text(event, FULL_REPLY_EXTRA):
        return
    try:
        event.set_extra(FULL_REPLY_EXTRA, text)
    except Exception:  # noqa: BLE001 - 记不下来就退回按块拼接
        pass


def is_game_event(event: Any) -> bool:
    """事件是不是本插件自己造的游戏事件（按平台 id 判定）。"""
    getter = getattr(event, "get_platform_id", None)
    if not callable(getter):
        return False
    try:
        return getter() == GAME_PLATFORM.id
    except Exception:  # noqa: BLE001
        return False


def capture_result_text(result: Any) -> str:
    """从 MessageEventResult 里取纯文本；取不到返回空串。"""
    getter = getattr(result, "get_plain_text", None)
    if not callable(getter):
        return ""
    try:
        text = getter()
    except Exception:  # noqa: BLE001
        return ""
    return text if isinstance(text, str) else ""


def _result_content_type_enum() -> Any:
    """延迟取 AstrBot 的 ResultContentType；单测里可以替换掉。"""
    try:
        from astrbot.core.message.message_event_result import ResultContentType
    except Exception:  # noqa: BLE001 - 老版本或测试桩没有这个模块
        return None
    return ResultContentType


def skip_platform_tts(result: Any) -> None:
    """让 AstrBot 自带的 TTS 不处理这条回复。

    游戏回合和 QQ 共用 umo，result_decorate 会按 umo 查会话开关，逐段跑
    GSV 合成（每段 5s 上下，而且中文翻译常带 emoji 直接 400）。游戏里的语音
    由本插件自己的 /mas3d/api/voice/synth 负责（apps/bridge/tts.py），所以把
    这条 result 的内容类型降成普通结果，TTS 与自动分段都会跳过它。
    """
    current = getattr(result, "result_content_type", None)
    if getattr(current, "name", "") != "LLM_RESULT":
        return
    enum = _result_content_type_enum()
    target = getattr(enum, "GENERAL_RESULT", None) if enum is not None else None
    if target is None:
        return
    try:
        result.result_content_type = target
    except (AttributeError, TypeError):
        pass


def suppress_external_splitter(result: Any) -> None:
    """让 astrbot_plugin_splitter 放过这一条 result。

    该插件在 on_decorating_result（priority=-1e17，晚于本插件）里检查
    getattr(result, "__splitter_processed", False)（插件源码 main.py:213），
    命中就直接 return。于是它不会把回复拆成多段、也不会绕开事件直接往
    QQ 会话发。插件没装时多设一个属性无副作用。
    """
    try:
        setattr(result, SPLITTER_MARK_ATTR, True)
    except (AttributeError, TypeError):
        pass


class TurnDispatchError(RuntimeError):
    """事件没有被 AstrBot 管线接管（EventBus 未运行或配置找不到）。"""


def chain_text(message: Any) -> str:
    """从 MessageChain 里取出纯文本；拿不到就返回空串。"""
    if message is None:
        return ""
    getter = getattr(message, "get_plain_text", None)
    if callable(getter):
        try:
            return str(getter() or "")
        except Exception:  # noqa: BLE001 - 第三方消息段的容错分支
            return ""
    if isinstance(message, str):
        return message
    return str(message)


class GameMessageEvent(AstrMessageEvent):
    """3D 教室回合事件：umo 指向 QQ 会话，send 走本地回调。"""

    def __init__(
        self,
        text: str,
        umo: str = DEFAULT_UMO,
        on_send: SendCallback | None = None,
        *,
        sender_id: str = "mas3d-player",
        sender_name: str = "玩家",
    ) -> None:
        message = AstrBotMessage()
        message.type = MessageType.FRIEND_MESSAGE
        message.self_id = "mas3d-game"
        message.session_id = "mas3d-game"
        message.message_id = f"mas3d-{uuid.uuid4().hex}"
        message.sender = MessageMember(user_id=sender_id, nickname=sender_name)
        message.message = [Plain(text)]
        message.message_str = text
        message.raw_message = {"source": "mas3d"}

        super().__init__(
            message_str=text,
            message_obj=message,
            platform_meta=GAME_PLATFORM,
            session_id="mas3d-game",
        )

        self.on_send = on_send
        self.sent_chunks: list[str] = []
        self.degraded = False
        self.applied_umo = apply_umo(self, umo)

    async def send(self, message: Any) -> None:
        """截获回复：只记录并回调，不碰 QQ 平台。"""
        text = chain_text(message)
        if not text.strip():
            return
        self._has_send_oper = True
        self.sent_chunks.append(text)
        if self.on_send is not None:
            await self.on_send(text)

    async def send_streaming(
        self,
        generator: AsyncGenerator[Any, None],
        use_fallback: bool = False,
    ) -> None:
        """基类对不支持的平台是空实现；这里把流逐段吃掉，免得回复丢失。"""
        async for chunk in generator:
            await self.send(chunk)


def apply_umo(event: Any, umo: str) -> str:
    """把事件的会话指向 QQ 会话；setter 缺失时降级成独立 umo。"""
    target = resolve_umo(umo)
    try:
        event.unified_msg_origin = target
    except (AttributeError, TypeError, ValueError):
        fallback = fallback_umo()
        event.degraded = True
        try:
            event.unified_msg_origin = fallback
        except (AttributeError, TypeError, ValueError):
            pass
        return fallback
    if not is_valid_umo(str(event.unified_msg_origin)):
        event.degraded = True
    return target


class TurnBridge:
    """把事件送进 AstrBot 管线并等它跑完的最小抽象（测试里可替换）。"""

    def __init__(self, umo: str = DEFAULT_UMO) -> None:
        self.umo = resolve_umo(umo)
        self.last_event: GameMessageEvent | None = None

    def create_event(self, text: str, on_send: SendCallback) -> GameMessageEvent:
        return GameMessageEvent(text=text, umo=self.umo, on_send=on_send)

    async def dispatch(self, event: GameMessageEvent) -> None:  # pragma: no cover - 抽象
        raise NotImplementedError


def _registry_is_active(event: GameMessageEvent) -> bool:
    """事件当前是否还在管线里（用 AstrBot 的活跃事件表判断）。"""
    try:
        from astrbot.core.utils.active_event_registry import active_event_registry
    except Exception:  # noqa: BLE001 - 老版本没有这张表就当作已完成
        return False
    events = getattr(active_event_registry, "_events", None)
    if not isinstance(events, dict):
        return False
    return event in events.get(event.unified_msg_origin, ())


class QueueTurnBridge(TurnBridge):
    """真实实现：把事件投进 AstrBot 的 event queue，再轮询活跃表等它结束。"""

    def __init__(
        self,
        event_queue: Any,
        umo: str = DEFAULT_UMO,
        *,
        is_active: Callable[[GameMessageEvent], bool] | None = None,
        start_timeout_s: float = START_TIMEOUT_S,
        poll_interval_s: float = POLL_INTERVAL_S,
    ) -> None:
        super().__init__(umo)
        self._queue = event_queue
        self._is_active = is_active or _registry_is_active
        self._start_timeout_s = start_timeout_s
        self._poll_interval_s = poll_interval_s

    async def dispatch(self, event: GameMessageEvent) -> None:
        self._queue.put_nowait(event)
        await self._await_completion(event)

    async def _await_completion(self, event: GameMessageEvent) -> None:
        deadline = time.monotonic() + self._start_timeout_s
        seen = False
        while True:
            if self._is_active(event):
                seen = True
            elif seen:
                return
            elif time.monotonic() > deadline:
                raise TurnDispatchError(
                    "事件没有被 AstrBot 管线接管：EventBus 未运行或找不到该会话的配置",
                )
            await asyncio.sleep(self._poll_interval_s)


async def run_turn(
    bridge: TurnBridge,
    text: str,
    *,
    timeout_s: float = TURN_TIMEOUT_S,
    on_chunk: Callable[[str], Awaitable[None]] | None = None,
) -> str:
    """跑一回合：注入场景前缀 → 交给桥接 → 返回剥掉前缀的回复。"""
    prompt = text.strip()
    if not prompt:
        raise ValueError("回合文本为空")

    chunks: list[str] = []

    async def collect(chunk: str) -> None:
        chunks.append(chunk)
        if on_chunk is not None:
            await on_chunk(chunk)

    event = bridge.create_event(scene_text(prompt), collect)
    bridge.last_event = event
    await asyncio.wait_for(bridge.dispatch(event), timeout=timeout_s)
    full = extra_text(event, FULL_REPLY_EXTRA)
    if full:
        return strip_scene_prefix(full.strip())
    return strip_scene_prefix("\n".join(chunks))