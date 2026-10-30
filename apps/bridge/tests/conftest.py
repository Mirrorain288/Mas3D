"""测试用的最小 astrbot 替身。

真实 AstrBot 只在运行时加载插件，所以测试不依赖它的 Python 环境：
这里把 astrbot.api / astrbot.api.star / astrbot.api.event 造出来，
再以包的形式加载 apps/bridge/main.py（submodule_search_locations 让插件内部
可以继续用相对导入扩展成多文件）。
"""

from __future__ import annotations

import importlib.util
import logging
import sys
import types
from pathlib import Path

import pytest

BRIDGE_DIR = Path(__file__).resolve().parents[1]
PLUGIN_MODULE = "astrbot_plugin_mas3d_game"

REGISTERED: list[tuple[tuple, dict]] = []


def _install_astrbot_stub() -> None:
    if "astrbot" in sys.modules:
        return

    astrbot = types.ModuleType("astrbot")
    astrbot.__path__ = []  # 声明成包，允许 astrbot.api 之类的子模块
    api = types.ModuleType("astrbot.api")
    api.__path__ = []
    api.logger = logging.getLogger("astrbot")

    star = types.ModuleType("astrbot.api.star")

    class Context:  # noqa: D401 - 替身只需要能实例化
        """真实 Context 的极简替身。"""

    class Star:
        def __init__(self, context: Context | None = None, config=None):
            self.context = context
            self.config = config

    def register(*args, **kwargs):
        def decorator(cls):
            REGISTERED.append((args, kwargs))
            cls.__plugin_meta__ = {"args": args, "kwargs": kwargs}
            return cls

        return decorator

    star.Context = Context
    star.Star = Star
    star.register = register

    event = types.ModuleType("astrbot.api.event")

    class AstrMessageEvent:  # noqa: D401
        """真实事件的极简替身。"""

    class _Filter:
        def command(self, *args, **kwargs):
            def decorator(fn):
                return fn

            return decorator

        def on_llm_response(self, *args, **kwargs):
            def decorator(fn):
                return fn

            return decorator

        def on_decorating_result(self, *args, **kwargs):
            def decorator(fn):
                return fn

            return decorator

    event.AstrMessageEvent = AstrMessageEvent
    event.filter = _Filter()

    core = types.ModuleType("astrbot.core")
    core.__path__ = []
    core_platform = types.ModuleType("astrbot.core.platform")
    core_platform.__path__ = []
    core_message = types.ModuleType("astrbot.core.message")
    core_message.__path__ = []

    class MessageType:
        GROUP_MESSAGE = "GroupMessage"
        FRIEND_MESSAGE = "FriendMessage"
        OTHER_MESSAGE = "OtherMessage"

    class PlatformMetadata:
        def __init__(self, name, description="", id=None, **kwargs):
            self.name = name
            self.description = description
            self.id = id or name
            self.support_streaming_message = kwargs.get("support_streaming_message", True)

    class AstrBotMessage:
        def __init__(self):
            self.type = MessageType.OTHER_MESSAGE
            self.self_id = ""
            self.session_id = ""
            self.message_id = ""
            self.group = None
            self.sender = None
            self.message = []
            self.message_str = ""
            self.raw_message = None
            self.timestamp = 0

    class MessageMember:
        def __init__(self, user_id, nickname=None):
            self.user_id = user_id
            self.nickname = nickname

    class Plain:
        def __init__(self, text=""):
            self.text = text

    class _MessageSession:
        def __init__(self, platform_id, message_type, session_id):
            self.platform_name = platform_id
            self.platform_id = platform_id
            self.message_type = message_type
            self.session_id = session_id

        def __str__(self):
            return f"{self.platform_id}:{self.message_type}:{self.session_id}"

    class CoreMessageEvent:
        def __init__(self, message_str="", message_obj=None, platform_meta=None, session_id=""):
            self.message_str = message_str
            self.message_obj = message_obj
            self.platform_meta = platform_meta
            self.session = _MessageSession(
                platform_meta.id if platform_meta else "",
                getattr(message_obj, "type", None),
                session_id,
            )
            self.sent = []
            self._extras = {}
            self.role = "member"
            self.is_wake = False
            self.call_llm = True
            self._has_send_oper = False

        @property
        def unified_msg_origin(self):
            return str(self.session)

        @unified_msg_origin.setter
        def unified_msg_origin(self, value):
            platform_id, message_type, session_id = value.split(":", 2)
            self.session = _MessageSession(platform_id, message_type, session_id)

        def get_messages(self):
            return getattr(self.message_obj, "message", [])

        def get_message_type(self):
            return getattr(self.message_obj, "type", None)

        def is_private_chat(self):
            return self.get_message_type() == MessageType.FRIEND_MESSAGE

        def get_group_id(self):
            return ""

        def get_self_id(self):
            return getattr(self.message_obj, "self_id", "")

        def get_sender_id(self):
            sender = getattr(self.message_obj, "sender", None)
            return getattr(sender, "user_id", "") if sender else ""

        def get_sender_name(self):
            sender = getattr(self.message_obj, "sender", None)
            return getattr(sender, "nickname", "") if sender else ""

        def get_platform_name(self):
            return self.platform_meta.name

        def get_platform_id(self):
            return self.platform_meta.id

        def set_extra(self, key, value):
            self._extras[key] = value

        def get_extra(self, key=None, default=None):
            if key is None:
                return self._extras
            return self._extras.get(key, default)

        def cleanup_temporary_local_files(self):
            return None

        async def send(self, message):
            self.sent.append(message)

        async def send_streaming(self, generator, use_fallback=False):
            async for chunk in generator:
                self.sent.append(chunk)

    astr_message_event = types.ModuleType("astrbot.core.platform.astr_message_event")
    astr_message_event.AstrMessageEvent = CoreMessageEvent
    astrbot_message = types.ModuleType("astrbot.core.platform.astrbot_message")
    astrbot_message.AstrBotMessage = AstrBotMessage
    astrbot_message.MessageMember = MessageMember
    message_type = types.ModuleType("astrbot.core.platform.message_type")
    message_type.MessageType = MessageType
    platform_metadata = types.ModuleType("astrbot.core.platform.platform_metadata")
    platform_metadata.PlatformMetadata = PlatformMetadata
    components = types.ModuleType("astrbot.core.message.components")
    components.Plain = Plain

    core.platform = core_platform
    core.message = core_message
    core_platform.astr_message_event = astr_message_event
    core_platform.astrbot_message = astrbot_message
    core_platform.message_type = message_type
    core_platform.platform_metadata = platform_metadata
    core_message.components = components
    astrbot.core = core

    sys.modules["astrbot.core"] = core
    sys.modules["astrbot.core.platform"] = core_platform
    sys.modules["astrbot.core.platform.astr_message_event"] = astr_message_event
    sys.modules["astrbot.core.platform.astrbot_message"] = astrbot_message
    sys.modules["astrbot.core.platform.message_type"] = message_type
    sys.modules["astrbot.core.platform.platform_metadata"] = platform_metadata
    sys.modules["astrbot.core.message"] = core_message
    sys.modules["astrbot.core.message.components"] = components

    sys.modules["astrbot"] = astrbot
    sys.modules["astrbot.api"] = api
    sys.modules["astrbot.api.star"] = star
    sys.modules["astrbot.api.event"] = event
    astrbot.api = api
    astrbot.logger = api.logger


@pytest.fixture(scope="session")
def plugin():
    """把 apps/bridge 当成 astrbot_plugin_mas3d_game 包导入。"""
    _install_astrbot_stub()
    if PLUGIN_MODULE in sys.modules:
        return sys.modules[PLUGIN_MODULE]

    spec = importlib.util.spec_from_file_location(
        PLUGIN_MODULE,
        BRIDGE_DIR / "main.py",
        submodule_search_locations=[str(BRIDGE_DIR)],
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[PLUGIN_MODULE] = module
    spec.loader.exec_module(module)
    return module