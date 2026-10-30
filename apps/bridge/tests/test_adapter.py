"""Task 11：游戏事件与 QQ 会话共用 umo，回复不进 QQ 发送路径。"""

from __future__ import annotations

import pytest

QQ_UMO = "Monika:FriendMessage:2441388327"


@pytest.fixture(scope="session")
def adapter(plugin):
    from astrbot_plugin_mas3d_game import adapter as mod

    return mod


@pytest.fixture(scope="session")
def session_mod(plugin):
    from astrbot_plugin_mas3d_game import session as mod

    return mod


class _Chain:
    """MessageChain 的替身：只要能给出纯文本。"""

    def __init__(self, text: str) -> None:
        self._text = text

    def get_plain_text(self, with_other_comps_mark: bool = False) -> str:
        return self._text


@pytest.fixture
def bridge_factory(adapter):
    """产出「按剧本回一句话」的桥接；dispatch 里调用 event.send 模拟管线投递。"""

    def make(reply: str):
        class _ScriptedBridge(adapter.TurnBridge):
            def __init__(self) -> None:
                super().__init__()
                self.reply = reply
                self.received: list[str] = []

            async def dispatch(self, event) -> None:
                self.received.append(event.message_str)
                await event.send(_Chain(self.reply))

        return _ScriptedBridge()

    return make


@pytest.mark.asyncio
async def test_umo_is_overwritten(adapter, session_mod):
    event = adapter.GameMessageEvent("你好", umo=session_mod.DEFAULT_UMO)

    assert event.unified_msg_origin == QQ_UMO
    assert event.degraded is False
    assert event.is_private_chat() is True
    message_type = event.get_message_type()
    assert getattr(message_type, "value", message_type) == "FriendMessage"
    # 平台元数据故意不是 QQ 适配器，任何发送都不会落到 aiocqhttp 队列
    assert event.get_platform_id() == "mas3d_game"
    assert event.get_platform_name() == "mas3d_game"


@pytest.mark.asyncio
async def test_reply_goes_to_callback_not_qq(adapter, bridge_factory):
    qq_sent: list[str] = []  # AIOCQHTTP 平台适配器的发送队列；本测试断言它始终为空
    bridge = bridge_factory("（3D教室）我在。")

    reply = await adapter.run_turn(bridge, "在吗")

    assert reply == "我在。"
    assert qq_sent == []
    assert bridge.last_event is not None
    assert bridge.last_event.sent_chunks == ["（3D教室）我在。"]


@pytest.mark.asyncio
async def test_scene_prefix_injected_and_stripped(adapter, bridge_factory):
    bridge = bridge_factory("（3D教室）星空很安静。")

    reply = await adapter.run_turn(bridge, "  外面的星空好看吗  ")

    assert bridge.received == ["（3D教室）外面的星空好看吗"]
    assert reply == "星空很安静。"


def test_strip_scene_prefix_handles_halfwidth(session_mod):
    assert session_mod.strip_scene_prefix("（3D教室）你好") == "你好"
    assert session_mod.strip_scene_prefix("(3D教室)\n你好") == "你好"
    assert session_mod.strip_scene_prefix("（3D教室）（3D教室）你好") == "你好"
    assert session_mod.strip_scene_prefix("没有前缀") == "没有前缀"


def test_resolve_umo_falls_back(session_mod):
    assert session_mod.resolve_umo(None) == QQ_UMO
    assert session_mod.resolve_umo("  ") == QQ_UMO
    assert session_mod.resolve_umo("bad-umo") == QQ_UMO
    assert session_mod.resolve_umo("webchat:FriendMessage:x") == "webchat:FriendMessage:x"


class _NoSetterEvent:
    """模拟上游删掉 unified_msg_origin setter 的情况。"""

    def __init__(self) -> None:
        self.degraded = False

    @property
    def unified_msg_origin(self) -> str:
        return "aiocqhttp:FriendMessage:0"


def test_umo_setter_missing_degrades(adapter):
    event = _NoSetterEvent()

    applied = adapter.apply_umo(event, QQ_UMO)

    assert applied.startswith("mas3d:GameMessage:")
    assert event.degraded is True


@pytest.mark.asyncio
async def test_queue_bridge_waits_until_inactive(adapter):
    class _Queue:
        def __init__(self) -> None:
            self.events = []

        def put_nowait(self, event) -> None:
            self.events.append(event)

    queue = _Queue()
    calls = {"n": 0}

    def is_active(event) -> bool:
        calls["n"] += 1
        return calls["n"] == 1

    bridge = adapter.QueueTurnBridge(
        queue,
        umo=QQ_UMO,
        is_active=is_active,
        start_timeout_s=1.0,
        poll_interval_s=0.01,
    )
    event = bridge.create_event("（3D教室）你好", lambda _text: None)

    await bridge.dispatch(event)

    assert queue.events == [event]
    assert calls["n"] >= 2


@pytest.mark.asyncio
async def test_queue_bridge_raises_when_never_picked_up(adapter):
    class _Queue:
        def put_nowait(self, event) -> None:
            return None

    bridge = adapter.QueueTurnBridge(
        _Queue(),
        umo=QQ_UMO,
        is_active=lambda _event: False,
        start_timeout_s=0.05,
        poll_interval_s=0.01,
    )
    event = bridge.create_event("（3D教室）你好", lambda _text: None)

    with pytest.raises(adapter.TurnDispatchError):
        await bridge.dispatch(event)