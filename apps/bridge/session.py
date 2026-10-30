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

FALLBACK_PLATFORM = "mas3d"
FALLBACK_MESSAGE_TYPE = "GameMessage"


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


def fallback_umo() -> str:
    """事件 setter 不可用时用的独立会话 id（不与 QQ 会话混写）。"""
    return f"{FALLBACK_PLATFORM}:{FALLBACK_MESSAGE_TYPE}:{uuid.uuid4().hex}"
