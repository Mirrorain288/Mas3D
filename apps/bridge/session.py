"""3D 教室会话标识与场景前缀的纯函数工具。

游戏里的每一回合都要写进同一条 QQ 会话历史（umo = Monika:FriendMessage:2441388327），
因此这里集中处理三件事：umo 的解析与兜底、注入给模型的场景前缀、展示前剥离前缀。
"""

from __future__ import annotations

import uuid

SCENE_PREFIX = "（3D教室）"
"""注入到用户文本前面的场景说明，让共享人设的 Monika 知道这句话来自 3D 教室。"""

SCENE_PREFIX_ALIASES: tuple[str, ...] = (SCENE_PREFIX, "(3D教室)")
"""半角括号也认，避免模型复述时把前缀写成半角。"""

DEFAULT_UMO = "Monika:FriendMessage:2441388327"
"""QQ 私聊会话的 unified_msg_origin（platform_id:message_type:session_id）。"""

FALLBACK_PLATFORM = "Monika"
"""兜底 umo 的平台前缀必须是一个真实存在的平台适配器 id。

AstrBot 的 MessageSession.from_str 会用 MessageType(...) 强校验 message_type，
未知值直接抛 ValueError；而未知 umo 又会被 astrbot_config_mgr 落到 "default"
配置档，这一档在本机没有对应的 PipelineScheduler，事件会被 EventBus 静默丢掉
（实测：日志打印出事件后没有任何后续，活跃事件表也一直为空）。
所以兜底与专用 umo 都走「已配置平台 + 合法消息类型」。"""

FALLBACK_MESSAGE_TYPE = "FriendMessage"

DEDICATED_UMO = "Monika:FriendMessage:mas3d-player"
"""专用会话：不写进 QQ 历史，记忆从零开始（共享会话太长时切过去）。

平台前缀取 Monika（aiocqhttp 实例 id），这样 umop 路由表里的 "Monika:*:*"
会把它分配到 MoMo 配置档，从而复用同一套 provider / 人设 / 插件；
会话行仍按 umo 独立，所以 QQ 那边不会多出这些回合。"""

SESSION_MODES: tuple[str, ...] = ("shared", "dedicated")
"""shared = 与 QQ 共用同一条会话；dedicated = 游戏独占一条。"""

DEFAULT_SESSION_MODE = "shared"

SHARED_TURN_LIMIT = 60
"""共享会话的软上限（游戏回合数）：超过只提示可换会话，不删任何历史。"""


def scene_text(text: str) -> str:
    """把玩家输入包装成带场景前缀的文本。"""
    return f"{SCENE_PREFIX}{text.strip()}"


def strip_scene_prefix(text: str) -> str:
    """剥掉回复开头的场景前缀（可能带空白或多层）。"""
    out = text.lstrip()
    stripped = True
    while stripped:
        stripped = False
        for prefix in SCENE_PREFIX_ALIASES:
            if out.startswith(prefix):
                out = out[len(prefix) :].lstrip()
                stripped = True
                break
    return out.strip()


def is_valid_umo(value: str) -> bool:
    """umo 必须是 platform_id:message_type:session_id 三段且都非空。"""
    parts = value.split(":", 2)
    return len(parts) == 3 and all(part.strip() for part in parts)


def resolve_umo(raw: str | None) -> str:
    """配置里给的 umo 不合法时回落到 QQ 私聊会话。"""
    value = (raw or "").strip()
    return value if is_valid_umo(value) else DEFAULT_UMO


def resolve_mode(raw: str | None) -> str:
    """会话模式只认 shared / dedicated，其它一律回落到共享。"""
    value = (raw or "").strip().lower()
    return value if value in SESSION_MODES else DEFAULT_SESSION_MODE


def umo_for_mode(mode: str, shared_umo: str = DEFAULT_UMO) -> str:
    """按模式给出这一回合要用的 umo。"""
    if resolve_mode(mode) == "shared":
        return resolve_umo(shared_umo)
    return DEDICATED_UMO


def fallback_umo() -> str:
    """事件 setter 不可用时用的独立会话 id（不与 QQ 会话混写）。

    必须保持三段合法且平台前缀已被 umop 路由，否则事件会被丢进 default 档。
    """
    return f"{FALLBACK_PLATFORM}:{FALLBACK_MESSAGE_TYPE}:mas3d-{uuid.uuid4().hex}"
