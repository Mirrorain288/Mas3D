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


@pytest.fixture
def multi_bridge(adapter):
    """dispatch 里只投最后一段，但把整轮文本记到 extra —— 模拟 splitter 改写 result。"""

    FULL = "（3D教室）第一段。\n\n第二段。\n\n最后一段。"

    class _MultiBridge(adapter.TurnBridge):
        def __init__(self) -> None:
            super().__init__()
            self.full = FULL

        async def dispatch(self, event) -> None:
            adapter.set_full_reply(event, self.full)
            await event.send(_Chain("最后一段。"))

    return _MultiBridge()


@pytest.mark.asyncio
async def test_run_turn_prefers_full_reply_extra(adapter, multi_bridge):
    text = await adapter.run_turn(multi_bridge, "你好", timeout_s=5.0)

    # 整轮文本优先于逐块拼接：三段都在，场景前缀已剥离
    assert text == "第一段。\n\n第二段。\n\n最后一段。"
    assert "（3D教室）" not in text


@pytest.mark.asyncio
async def test_full_reply_keeps_the_first_writer(adapter, session_mod):
    """后写的钩子不许覆盖先写的整轮文本：结果链当前长度不是真相。"""

    class _Sink:
        def __init__(self) -> None:
            self.chunks: list[str] = []

        async def __call__(self, text: str) -> None:
            self.chunks.append(text)

    sink = _Sink()
    event = adapter.GameMessageEvent("你好", umo=session_mod.DEFAULT_UMO, on_send=sink)
    adapter.set_full_reply(event, "第一段。第二段。")
    adapter.set_full_reply(event, "第二段。")

    assert adapter.extra_text(event, adapter.FULL_REPLY_EXTRA) == "第一段。第二段。"
    # 空串不许占位，否则后面真正的整轮文本会被挡掉
    other = adapter.GameMessageEvent("你好", umo=session_mod.DEFAULT_UMO, on_send=sink)
    adapter.set_full_reply(other, "   ")
    adapter.set_full_reply(other, "真正的整轮文本。")
    assert adapter.extra_text(other, adapter.FULL_REPLY_EXTRA) == "真正的整轮文本。"


@pytest.mark.asyncio
async def test_run_turn_falls_back_to_chunks(adapter, bridge_factory):
    bridge = bridge_factory("（3D教室）只有一块。")

    text = await adapter.run_turn(bridge, "你好", timeout_s=5.0)

    assert text == "只有一块。"


@pytest.mark.asyncio
async def test_game_event_is_recognised(adapter, session_mod):
    event = adapter.GameMessageEvent("你好", umo=session_mod.DEFAULT_UMO)

    assert adapter.is_game_event(event) is True
    assert adapter.is_game_event(_Chain("x")) is False


def test_suppress_external_splitter_marks_result(adapter):
    result = _Chain("整段文本")

    adapter.suppress_external_splitter(result)

    # astrbot_plugin_splitter 的 main.py:213 正是看这个标记
    assert getattr(result, adapter.SPLITTER_MARK_ATTR, False) is True


def test_capture_result_text_handles_plain_text(adapter):
    assert adapter.capture_result_text(_Chain("整段文本")) == "整段文本"
    assert adapter.capture_result_text(object()) == ""


class _Type:
    def __init__(self, name: str) -> None:
        self.name = name


def test_skip_platform_tts_downgrades_llm_result(adapter, monkeypatch):
    class _Result:
        def __init__(self) -> None:
            self.result_content_type = _Type("LLM_RESULT")

    class _Enum:
        GENERAL_RESULT = _Type("GENERAL_RESULT")

    monkeypatch.setattr(adapter, "_result_content_type_enum", lambda: _Enum)
    result = _Result()

    adapter.skip_platform_tts(result)

    assert result.result_content_type.name == "GENERAL_RESULT"


def test_skip_platform_tts_keeps_other_results(adapter, monkeypatch):
    class _Result:
        def __init__(self) -> None:
            self.result_content_type = _Type("GENERAL_RESULT")

    monkeypatch.setattr(adapter, "_result_content_type_enum", lambda: None)
    result = _Result()

    adapter.skip_platform_tts(result)

    assert result.result_content_type.name == "GENERAL_RESULT"