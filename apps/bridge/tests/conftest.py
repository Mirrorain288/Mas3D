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

    event.AstrMessageEvent = AstrMessageEvent
    event.filter = _Filter()

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
