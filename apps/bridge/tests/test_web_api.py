"""Task 12 的验收：本机端点、令牌、回合串行、限流与错误翻译。"""

from __future__ import annotations

import asyncio
import importlib
import json
import time

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

TOKEN = "0123456789abcdef" * 4
UMO = "Monika:FriendMessage:2441388327"


class StubRunner:
    """替代真实插件对象：只需要 umo / degraded / handle_turn。"""

    def __init__(self, reply: str = "我在。", *, delay: float = 0.0, error: Exception | None = None):
        self.umo = UMO
        self.degraded = False
        self.reply = reply
        self.delay = delay
        self.error = error
        self.calls: list[str] = []

    async def handle_turn(self, text: str) -> str:
        self.calls.append(text)
        if self.delay:
            await asyncio.sleep(self.delay)
        if self.error is not None:
            raise self.error
        return self.reply


class SessionRunner(StubRunner):
    """带会话模式的 runner：记录切换，快照与真实插件同形状。"""

    def __init__(self, mode: str = "shared", *, shared_turns: int = 3, limit: int = 60, reject: bool = False):
        super().__init__()
        self.mode = mode
        self.shared_turns = shared_turns
        self.limit = limit
        self.reject = reject
        self.mode_calls: list[str] = []

    def session_snapshot(self) -> dict:
        return {
            "mode": self.mode,
            "sharedTurns": self.shared_turns,
            "limit": self.limit,
            "overLimit": self.shared_turns >= self.limit,
            "umo": UMO,
        }

    def set_session_mode(self, mode: str) -> dict:
        if self.reject:
            raise RuntimeError("会话切换不可用")
        if mode not in ("shared", "dedicated"):
            raise ValueError("会话模式只能是 shared / dedicated")
        self.mode_calls.append(mode)
        self.mode = mode
        return self.session_snapshot()


@pytest.fixture(scope="module")
def web_api(plugin):
    return importlib.import_module("astrbot_plugin_mas3d_game.web_api")


@pytest.fixture()
def make_client(web_api):
    clients: list[TestClient] = []

    def _make(runner: StubRunner, **kwargs) -> TestClient:
        token = kwargs.pop("token", TOKEN)
        app = web_api.create_app(runner, token, **kwargs)
        client = TestClient(app)
        clients.append(client)
        return client

    yield _make
    for client in clients:
        client.close()


def test_health_without_token_401(web_api, make_client):
    client = make_client(StubRunner())
    response = client.get("/mas3d/api/health")
    assert response.status_code == 401


def test_health_with_token_200(web_api, make_client):
    client = make_client(StubRunner())
    response = client.get("/mas3d/api/health", headers={"Authorization": f"Bearer {TOKEN}"})
    assert response.status_code == 200
    payload = response.json()
    assert payload["ok"] is True
    assert payload["plugin"] == "0.1.0"
    assert payload["umo"] == UMO
    assert payload["degraded"] is False
    # runner 没有会话接口时也要给出最小形状，前端不用分支
    assert payload["session"] == {
        "mode": "shared",
        "sharedTurns": 0,
        "limit": 0,
        "overLimit": False,
        "umo": UMO,
    }


def test_health_accepts_query_token(web_api, make_client):
    client = make_client(StubRunner())
    assert client.get(f"/mas3d/api/health?token={TOKEN}").status_code == 200


def test_health_with_wrong_token_401(web_api, make_client):
    client = make_client(StubRunner())
    response = client.get("/mas3d/api/health", headers={"Authorization": "Bearer nope"})
    assert response.status_code == 401


def test_health_from_remote_host_403(web_api):
    app = web_api.create_app(StubRunner(), TOKEN)
    client = TestClient(app, client=("10.1.2.3", 5000))
    try:
        response = client.get("/mas3d/api/health", headers={"Authorization": f"Bearer {TOKEN}"})
    finally:
        client.close()
    assert response.status_code == 403


def test_ws_bad_token_closes_1008(web_api, make_client):
    client = make_client(StubRunner())
    with pytest.raises(WebSocketDisconnect) as info:
        with client.websocket_connect("/mas3d/ws?token=bad") as socket:
            socket.receive_json()
    assert info.value.code == 1008


def test_turn_streams_ack_thinking_reply(web_api, make_client):
    runner = StubRunner(reply="（3D教室）我在，别担心。")
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "你好"})
        ack = socket.receive_json()
        thinking = socket.receive_json()
        reply = socket.receive_json()
    assert ack["type"] == "ack" and ack["turnId"]
    assert thinking["type"] == "thinking" and thinking["turnId"] == ack["turnId"]
    assert reply["type"] == "reply" and reply["turnId"] == ack["turnId"]
    assert reply["text"] == "（3D教室）我在，别担心。"
    # 表情由前端 deriveCue 推导，桥接不带 cue
    assert "cue" not in reply
    assert runner.calls == ["你好"]


def test_turn_id_is_echoed_back(web_api, make_client):
    """前端自造 id 必须原样回显：ack 丢包时回复靠它认领回合。"""
    runner = StubRunner(reply="（3D教室）在。")
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "你好", "turnId": "p-7-lx2k"})
        frames = [socket.receive_json() for _ in range(3)]
    assert [f["turnId"] for f in frames] == ["p-7-lx2k"] * 3


def test_overlong_turn_id_is_replaced(web_api, make_client):
    """超长 id 当作没带：不让客户端把任意长字符串塞进帧里。"""
    runner = StubRunner(reply="（3D教室）在。")
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "你好", "turnId": "x" * 65})
        frames = [socket.receive_json() for _ in range(3)]
    assert frames[0]["turnId"] != "x" * 65
    assert frames[0]["turnId"] == frames[2]["turnId"]
    assert len(frames[0]["turnId"]) == 12


def test_empty_text_does_not_dispatch(web_api, make_client):
    runner = StubRunner()
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "   \n  "})
        frame = socket.receive_json()
    assert frame["type"] == "error"
    assert frame["code"] == "bad_request"
    assert runner.calls == []


def test_long_text_does_not_dispatch(web_api, make_client):
    runner = StubRunner()
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "字" * 301})
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "bad_request"
    assert runner.calls == []


def test_non_json_frame_is_bad_request(web_api, make_client):
    client = make_client(StubRunner())
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_text("这不是 JSON")
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "bad_request"


def test_unknown_frame_type_is_bad_request(web_api, make_client):
    client = make_client(StubRunner())
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "shout", "text": "嘿"})
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "bad_request"


def test_second_turn_waits_for_first(web_api, make_client):
    runner = StubRunner(delay=0.05)
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "第一句"})
        socket.send_json({"type": "say", "text": "第二句"})
        frames = [socket.receive_json() for _ in range(6)]
    assert [frame["type"] for frame in frames] == [
        "ack",
        "thinking",
        "reply",
        "ack",
        "thinking",
        "reply",
    ]
    assert frames[2]["turnId"] != frames[5]["turnId"]
    assert runner.calls == ["第一句", "第二句"]


def test_rate_limited_after_five_turns(web_api, make_client):
    runner = StubRunner()
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        for index in range(5):
            socket.send_json({"type": "say", "text": f"第{index}句"})
            assert socket.receive_json()["type"] == "ack"
            assert socket.receive_json()["type"] == "thinking"
            assert socket.receive_json()["type"] == "reply"
        socket.send_json({"type": "say", "text": "第六句"})
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "rate_limited"
    assert len(runner.calls) == 5


def test_turn_timeout_reports_timeout(web_api, make_client):
    runner = StubRunner(delay=0.3)
    client = make_client(runner, turn_timeout_s=0.05)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "慢慢来"})
        socket.receive_json()  # ack
        socket.receive_json()  # thinking
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "timeout"


def test_dispatch_error_maps_to_bridge_down(web_api, make_client, plugin):
    runner = StubRunner(error=plugin.adapter.TurnDispatchError("管线没接管"))
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "在吗"})
        socket.receive_json()
        socket.receive_json()
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "bridge_down"
    assert "管线" in frame["message"]


def test_unexpected_error_maps_to_model_error(web_api, make_client):
    client = make_client(StubRunner(error=ValueError("provider 挂了")))
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "在吗"})
        socket.receive_json()
        socket.receive_json()
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "model_error"


def test_blank_reply_maps_to_model_error(web_api, make_client):
    client = make_client(StubRunner(reply="   "))
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "在吗"})
        socket.receive_json()
        socket.receive_json()
        frame = socket.receive_json()
    assert frame["type"] == "error" and frame["code"] == "model_error"


def test_token_file_created_once(plugin, tmp_path):
    token_mod = importlib.import_module("astrbot_plugin_mas3d_game.token")
    first = token_mod.load_token(tmp_path)
    second = token_mod.load_token(tmp_path)
    path = token_mod.token_path(tmp_path)
    assert first == second
    assert len(first) == 64
    assert path.read_text(encoding="utf-8").strip() == first


def test_short_token_file_is_regenerated(plugin, tmp_path):
    token_mod = importlib.import_module("astrbot_plugin_mas3d_game.token")
    path = token_mod.token_path(tmp_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("broken", encoding="utf-8")
    token = token_mod.load_token(tmp_path)
    assert len(token) == 64
    assert path.read_text(encoding="utf-8").strip() == token


def test_is_local_host(web_api):
    assert web_api.is_local_host("127.0.0.1")
    assert web_api.is_local_host("::1")
    assert not web_api.is_local_host("192.168.1.10")
    assert not web_api.is_local_host(None)


COMMAND_PLAN = {
    "base": {"change": False, "name": "坐姿待机", "motion": 0.0, "rest": False},
    "operation": "command",
    "timing": "同时",
    "intents": [{"key": "右臂上抬", "speed": 1.0, "strength": 0.6, "hold": 2.0, "delay": 0.0}],
}
CHAT_PLAN = {**COMMAND_PLAN, "operation": "chat", "intents": []}


class PlannerRunner(StubRunner):
    """带动作后端的 runner；plan 由构造参数决定。"""

    def __init__(self, plan: dict | None = None, *, error: Exception | None = None, **kwargs):
        super().__init__(**kwargs)
        self.plan = plan
        self.plan_error = error
        self.plan_calls: list[tuple[str, list[dict]]] = []

    async def handle_plan(self, text: str, context=()):
        self.plan_calls.append((text, list(context)))
        if self.plan_error is not None:
            raise self.plan_error
        return self.plan


def test_motion_plan_without_token_401(web_api, make_client):
    client = make_client(PlannerRunner(COMMAND_PLAN))
    assert client.post("/mas3d/api/motion/plan", json={"text": "抬起右手"}).status_code == 401


def test_motion_plan_503_without_backend(web_api, make_client):
    client = make_client(StubRunner())
    response = client.post(
        "/mas3d/api/motion/plan",
        json={"text": "抬起右手"},
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 503
    assert response.json()["code"] == "bridge_down"


def test_motion_plan_returns_validated_plan(web_api, make_client):
    runner = PlannerRunner(COMMAND_PLAN)
    client = make_client(runner)
    response = client.post(
        "/mas3d/api/motion/plan",
        json={"text": "  抬起右手  "},
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 200
    assert response.json() == {"ok": True, "plan": COMMAND_PLAN}
    assert runner.plan_calls[0][0] == "抬起右手"


def test_motion_plan_rejects_blank_text(web_api, make_client):
    client = make_client(PlannerRunner(COMMAND_PLAN))
    response = client.post(
        "/mas3d/api/motion/plan",
        json={"text": "   "},
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 400
    assert response.json()["code"] == "bad_request"


def test_motion_plan_maps_backend_unavailable_503(plugin, web_api, make_client):
    hastsec = importlib.import_module("astrbot_plugin_mas3d_game.hastsec")
    runner = PlannerRunner(COMMAND_PLAN, error=hastsec.BackendUnavailable("HAST-SEC 连不上"))
    client = make_client(runner)
    response = client.post(
        "/mas3d/api/motion/plan",
        json={"text": "抬起右手"},
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 503
    assert response.json()["code"] == "bridge_down"


def test_ws_emits_intent_for_command_plan(web_api, make_client):
    runner = PlannerRunner(COMMAND_PLAN)
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "抬起右手"})
        ack = socket.receive_json()
        thinking = socket.receive_json()
        reply = socket.receive_json()
        intent = socket.receive_json()
    assert [ack["type"], thinking["type"], reply["type"], intent["type"]] == ["ack", "thinking", "reply", "intent"]
    assert intent["plan"]["operation"] == "command"
    assert intent["turnId"] == reply["turnId"]


def test_ws_skips_intent_for_chat_plan(web_api, make_client):
    runner = PlannerRunner(CHAT_PLAN)
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "今天好累"})
        assert socket.receive_json()["type"] == "ack"
        assert socket.receive_json()["type"] == "thinking"
        assert socket.receive_json()["type"] == "reply"
        # 没有 intent 帧：下一句的 ack 紧接着到来
        socket.send_json({"type": "say", "text": "陪我坐一会儿"})
        assert socket.receive_json()["type"] == "ack"
    assert [call[0] for call in runner.plan_calls] == ["今天好累", "陪我坐一会儿"]


def test_ws_intent_failure_is_silent(plugin, web_api, make_client):
    hastsec = importlib.import_module("astrbot_plugin_mas3d_game.hastsec")
    runner = PlannerRunner(COMMAND_PLAN, error=hastsec.BackendUnavailable("没起"))
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "抬起右手"})
        assert socket.receive_json()["type"] == "ack"
        assert socket.receive_json()["type"] == "thinking"
        assert socket.receive_json()["type"] == "reply"
        socket.send_json({"type": "say", "text": "还在吗"})
        assert socket.receive_json()["type"] == "ack"


def test_slow_planner_does_not_block_the_next_turn(web_api, make_client):
    """动作后端最多 9 秒，绝不能顶住下一句话——否则前端会看到「秒回却被判超时」。"""

    class SlowPlannerRunner(PlannerRunner):
        async def handle_plan(self, text, context=()):
            await asyncio.sleep(0.6)
            return await super().handle_plan(text, context)

    runner = SlowPlannerRunner(COMMAND_PLAN)
    client = make_client(runner)
    with client.websocket_connect(f"/mas3d/ws?token={TOKEN}") as socket:
        socket.send_json({"type": "say", "text": "抬起右手"})
        assert socket.receive_json()["type"] == "ack"
        assert socket.receive_json()["type"] == "thinking"
        assert socket.receive_json()["type"] == "reply"
        started = time.monotonic()
        socket.send_json({"type": "say", "text": "还在吗"})
        frame = socket.receive_json()
        elapsed = time.monotonic() - started
    assert frame["type"] == "ack", frame
    assert elapsed < 0.5, elapsed


VOICE_ID = "a" * 32
VOICE = {
    "id": VOICE_ID,
    "durationMs": 1200,
    "sentenceStartsMs": [0, 600],
    "englishText": "Hello. Bye.",
    "audioUrl": f"/mas3d/api/voice/{VOICE_ID}.wav",
    "cached": False,
}


class VoiceRunner(StubRunner):
    """带语音后端的 runner；voice 与缓存目录都可注入。"""

    def __init__(self, voice: dict | None = None, *, error: Exception | None = None, wav: bool = True, **kwargs):
        super().__init__(**kwargs)
        self.voice = voice if voice is not None else dict(VOICE)
        self.voice_error = error
        self.voice_calls: list[tuple[str, int | None]] = []
        self.wav = wav

    async def handle_voice(self, text: str, sentence: int | None = None) -> dict:
        self.voice_calls.append((text, sentence))
        if self.voice_error is not None:
            raise self.voice_error
        return self.voice

    def voice_path(self, voice_id: str):
        if not self.wav or voice_id != VOICE_ID:
            return None
        path = self._wav_dir / f"{voice_id}.wav"
        return path if path.is_file() else None

    _wav_dir = None


def _voice_client(web_api, make_client, runner: VoiceRunner, tmp_path):
    runner._wav_dir = tmp_path
    if runner.wav:
        (tmp_path / f"{VOICE_ID}.wav").write_bytes(b"RIFF....WAVEfmt ")
    return make_client(runner)


def test_voice_synth_without_token_401(web_api, make_client, tmp_path):
    runner = VoiceRunner()
    client = _voice_client(web_api, make_client, runner, tmp_path)
    assert client.post("/mas3d/api/voice/synth", json={"text": "你好"}).status_code == 401


def test_voice_synth_503_without_backend(web_api, make_client):
    client = make_client(StubRunner())
    response = client.post(
        "/mas3d/api/voice/synth", json={"text": "你好"}, headers={"Authorization": f"Bearer {TOKEN}"}
    )
    assert response.status_code == 503
    assert response.json()["code"] == "bridge_down"


def test_voice_synth_returns_voice(web_api, make_client, tmp_path):
    runner = VoiceRunner()
    client = _voice_client(web_api, make_client, runner, tmp_path)
    response = client.post(
        "/mas3d/api/voice/synth",
        json={"text": "  你好。再见。  ", "sentence": 1},
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 200
    assert response.json() == {"ok": True, "voice": VOICE}
    assert runner.voice_calls == [("你好。再见。", 1)]


def test_voice_synth_rejects_blank_text(web_api, make_client, tmp_path):
    runner = VoiceRunner()
    client = _voice_client(web_api, make_client, runner, tmp_path)
    response = client.post(
        "/mas3d/api/voice/synth", json={"text": "   "}, headers={"Authorization": f"Bearer {TOKEN}"}
    )
    assert response.status_code == 400
    assert response.json()["code"] == "bad_request"
    assert runner.voice_calls == []


def test_voice_synth_rejects_bad_sentence(web_api, make_client, tmp_path):
    runner = VoiceRunner()
    client = _voice_client(web_api, make_client, runner, tmp_path)
    for bad in ("1", -1, True):
        response = client.post(
            "/mas3d/api/voice/synth",
            json={"text": "你好", "sentence": bad},
            headers={"Authorization": f"Bearer {TOKEN}"},
        )
        assert response.status_code == 400, bad
    assert runner.voice_calls == []


def test_voice_synth_maps_unavailable_503(plugin, web_api, make_client, tmp_path):
    tts = importlib.import_module("astrbot_plugin_mas3d_game.tts")
    runner = VoiceRunner(error=tts.TtsUnavailable("GPT-SoVITS 连不上"))
    client = _voice_client(web_api, make_client, runner, tmp_path)
    response = client.post(
        "/mas3d/api/voice/synth", json={"text": "你好"}, headers={"Authorization": f"Bearer {TOKEN}"}
    )
    assert response.status_code == 503
    assert response.json()["code"] == "bridge_down"


def test_voice_synth_maps_index_error_400(web_api, make_client, tmp_path):
    runner = VoiceRunner(error=IndexError("list index out of range"))
    client = _voice_client(web_api, make_client, runner, tmp_path)
    response = client.post(
        "/mas3d/api/voice/synth",
        json={"text": "你好", "sentence": 8},
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 400
    assert response.json()["code"] == "bad_request"


def test_voice_audio_serves_wav(web_api, make_client, tmp_path):
    runner = VoiceRunner()
    client = _voice_client(web_api, make_client, runner, tmp_path)
    response = client.get(
        f"/mas3d/api/voice/{VOICE_ID}.wav?token={TOKEN}", headers={"Authorization": f"Bearer {TOKEN}"}
    )
    assert response.status_code == 200
    assert response.headers["content-type"] == "audio/wav"
    assert response.content.startswith(b"RIFF")


def test_voice_audio_missing_404(web_api, make_client, tmp_path):
    runner = VoiceRunner(wav=False)
    client = _voice_client(web_api, make_client, runner, tmp_path)
    for voice_id in ("0" * 32, VOICE_ID):
        response = client.get(
            f"/mas3d/api/voice/{voice_id}.wav", headers={"Authorization": f"Bearer {TOKEN}"}
        )
        assert response.status_code == 404


def test_cors_preflight_allows_local_page(make_client):
    client = make_client(StubRunner())
    resp = client.options(
        "/mas3d/api/health",
        headers={
            "Origin": "http://localhost:5173",
            "Access-Control-Request-Method": "GET",
            "Access-Control-Request-Headers": "authorization",
        },
    )

    assert resp.status_code == 200
    assert resp.headers["access-control-allow-origin"] == "http://localhost:5173"


def test_cors_ignores_foreign_origin(make_client):
    client = make_client(StubRunner())
    resp = client.get(
        "/mas3d/api/health",
        headers={"Origin": "http://evil.example", "Authorization": f"Bearer {TOKEN}"},
    )

    assert "access-control-allow-origin" not in resp.headers

def test_get_session_returns_snapshot(web_api, make_client):
    client = make_client(SessionRunner(shared_turns=12))
    response = client.get("/mas3d/api/session", headers={"Authorization": f"Bearer {TOKEN}"})
    assert response.status_code == 200
    assert response.json()["session"]["sharedTurns"] == 12
    assert response.json()["session"]["mode"] == "shared"


def test_get_session_without_token_401(web_api, make_client):
    client = make_client(SessionRunner())
    assert client.get("/mas3d/api/session").status_code == 401


def test_post_session_switches_mode(web_api, make_client):
    runner = SessionRunner()
    client = make_client(runner)
    response = client.post(
        "/mas3d/api/session",
        headers={"Authorization": f"Bearer {TOKEN}"},
        json={"mode": "dedicated"},
    )
    assert response.status_code == 200
    assert response.json()["session"]["mode"] == "dedicated"
    assert runner.mode_calls == ["dedicated"]


def test_post_session_rejects_unknown_mode(web_api, make_client):
    runner = SessionRunner()
    client = make_client(runner)
    response = client.post(
        "/mas3d/api/session",
        headers={"Authorization": f"Bearer {TOKEN}"},
        json={"mode": "cloud"},
    )
    assert response.status_code == 400
    assert response.json()["code"] == "bad_request"
    assert runner.mode_calls == []


def test_post_session_without_mode_400(web_api, make_client):
    client = make_client(SessionRunner())
    response = client.post(
        "/mas3d/api/session",
        headers={"Authorization": f"Bearer {TOKEN}"},
        json={},
    )
    assert response.status_code == 400


def test_post_session_without_support_503(web_api, make_client):
    client = make_client(StubRunner())
    response = client.post(
        "/mas3d/api/session",
        headers={"Authorization": f"Bearer {TOKEN}"},
        json={"mode": "dedicated"},
    )
    assert response.status_code == 503
    assert response.json()["code"] == "bridge_down"


def test_post_session_runtime_error_503(web_api, make_client):
    client = make_client(SessionRunner(reject=True))
    response = client.post(
        "/mas3d/api/session",
        headers={"Authorization": f"Bearer {TOKEN}"},
        json={"mode": "shared"},
    )
    assert response.status_code == 503


ASR_RESULT = {"text": "你好呀。", "language": "zh", "durationMs": 1500, "model": "small"}


class AsrRunner(StubRunner):
    """带语音识别后端的 runner：记录收到的原始字节，可注入异常。"""

    def __init__(self, result: dict | None = None, *, error: Exception | None = None, **kwargs):
        super().__init__(**kwargs)
        self.asr_result = result if result is not None else dict(ASR_RESULT)
        self.asr_error = error
        self.asr_calls: list[bytes] = []

    async def handle_asr(self, raw: bytes) -> dict:
        self.asr_calls.append(raw)
        if self.asr_error is not None:
            raise self.asr_error
        return self.asr_result


@pytest.fixture(scope="module")
def asr_module(plugin):
    return importlib.import_module("astrbot_plugin_mas3d_game.asr")


def test_asr_timeout_budget_is_45s(web_api):
    assert web_api.ASR_TIMEOUT_S == 45.0


def test_asr_without_token_401(web_api, make_client):
    client = make_client(AsrRunner())
    assert client.post("/mas3d/api/asr", content=b"RIFF....WAVE").status_code == 401


def test_asr_from_remote_host_403(web_api):
    app = web_api.create_app(AsrRunner(), TOKEN)
    client = TestClient(app, client=("10.1.2.3", 5000))
    try:
        response = client.post(
            "/mas3d/api/asr",
            content=b"RIFF....WAVE",
            headers={"Authorization": f"Bearer {TOKEN}"},
        )
    finally:
        client.close()
    assert response.status_code == 403
    assert response.json() == {"ok": False, "error": "只接受本机请求"}


def test_asr_bad_wav_400_with_original_message(plugin, web_api, make_client):
    """坏音频走真实 AsrClient：decode_wav 的 ValueError 原文要透给前端。"""
    asr = importlib.import_module("astrbot_plugin_mas3d_game.asr")

    class RealAsrRunner(StubRunner):
        def __init__(self):
            super().__init__()
            self.client = asr.AsrClient(loader=lambda name: pytest.fail("坏音频不该加载模型"))

        async def handle_asr(self, raw: bytes) -> dict:
            return await self.client.transcribe(raw)

    response = make_client(RealAsrRunner()).post(
        "/mas3d/api/asr",
        content=b"not a wav at all",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 400
    assert response.json() == {"ok": False, "error": "不是 RIFF/WAVE 数据"}


def test_asr_oversized_content_length_413(web_api, make_client, asr_module):
    runner = AsrRunner()
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=b"\0" * (asr_module.MAX_WAV_BYTES + 1),
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 413
    assert response.json() == {"ok": False, "error": "音频太大（上限 8 MiB）"}
    # 声明超限就该在读过 body 之前拒绝，别把 8 MiB 搬进内存
    assert runner.asr_calls == []


def test_asr_oversized_content_length_rejects_before_reading_body(web_api, make_client, asr_module, monkeypatch):
    """声明的 Content-Length 超限、实际只发 4 字节：只有预检能给 413。

    读完之后的那道体积检查在这里放行（4 字节远小于上限），所以删掉预检这条用例
    会变成 200——它单独钉住「读 body 之前就拒」，不再和 chunked 用例互相掩护。
    """
    import starlette.requests

    reads: list[int] = []
    original_body = starlette.requests.Request.body

    async def recording_body(self):
        reads.append(1)
        return await original_body(self)

    monkeypatch.setattr(starlette.requests.Request, "body", recording_body)

    runner = AsrRunner()
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=b"RIFF",  # 真正发出的 body 只有 4 字节
        headers={
            "Authorization": f"Bearer {TOKEN}",
            "Content-Length": str(asr_module.MAX_WAV_BYTES + 1),
        },
    )
    assert response.status_code == 413
    assert response.json() == {"ok": False, "error": "音频太大（上限 8 MiB）"}
    assert reads == []  # body 一次都没被读
    assert runner.asr_calls == []


def test_asr_oversized_chunked_body_413(web_api, make_client, asr_module):
    """分块上传没有 Content-Length：读完 body 之后仍要按体积再拒一次。"""
    runner = AsrRunner()
    chunk = b"\0" * (1024 * 1024)
    body = [chunk] * 8 + [b"\0" * 1024]
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=iter(body),
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 413
    assert response.json() == {"ok": False, "error": "音频太大（上限 8 MiB）"}
    assert runner.asr_calls == []


def test_asr_503_without_backend(web_api, make_client):
    """runner 没有 handle_asr：503，绝不能让 AttributeError 变成 500。"""
    response = make_client(StubRunner()).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 503
    assert response.json() == {"ok": False, "error": "语音识别不可用"}


def test_asr_503_when_backend_is_not_callable(web_api, make_client):
    runner = StubRunner()
    runner.handle_asr = "不是函数"
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 503
    assert response.json() == {"ok": False, "error": "语音识别不可用"}


def test_asr_maps_unavailable_503(plugin, web_api, make_client, asr_module):
    runner = AsrRunner(error=asr_module.AsrUnavailable("small 与 tiny 都加载失败"))
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 503
    assert response.json() == {"ok": False, "error": "语音识别不可用"}


def test_asr_maps_timeout_504(web_api, make_client):
    runner = AsrRunner(error=asyncio.TimeoutError())
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 504
    assert response.json() == {"ok": False, "error": "语音识别超时"}


def test_asr_maps_busy_503(plugin, web_api, make_client, asr_module):
    """等锁超时是「忙」，不是「超时」：503 + 让前端稍后再试的文案。"""
    runner = AsrRunner(error=asr_module.AsrBusy("语音识别忙"))
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 503
    assert response.json() == {"ok": False, "error": "语音识别忙，稍后再试"}


def test_asr_busy_while_lock_is_held_returns_503(web_api, make_client, asr_module, monkeypatch):
    """锁被另一段合成占着：真实 AsrClient 按 LOCK_WAIT_S 快速失败，路由回 503。"""
    monkeypatch.setattr(asr_module, "LOCK_WAIT_S", 0.05)
    lock = asyncio.Lock()

    class BusyRunner(StubRunner):
        def __init__(self):
            super().__init__()
            self.client = asr_module.AsrClient(
                lock=lock, loader=lambda name: pytest.fail("没排上号不该加载模型")
            )

        async def _hold_lock(self) -> None:
            async with lock:
                await asyncio.sleep(30.0)

        async def handle_asr(self, raw: bytes) -> dict:
            holder = asyncio.create_task(self._hold_lock())
            try:
                await asyncio.sleep(0.01)  # 让 holder 先把锁拿走
                return await self.client.transcribe(raw)
            finally:
                holder.cancel()

    started = time.monotonic()
    response = make_client(BusyRunner()).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 503
    assert response.json() == {"ok": False, "error": "语音识别忙，稍后再试"}
    assert time.monotonic() - started < 5.0  # 不是等满 45 s 推理预算


def test_asr_busy_with_another_small_wait_budget_still_returns_503(web_api, make_client, asr_module, monkeypatch):
    """换一个非字面量的小预算（0.2 s）再走一遍：等待时长真按 LOCK_WAIT_S 走，不是写死 0.05。"""
    monkeypatch.setattr(asr_module, "LOCK_WAIT_S", 0.2)
    lock = asyncio.Lock()
    loaded: list[str] = []

    class QueuedBusyRunner(StubRunner):
        def __init__(self):
            super().__init__()
            self.client = asr_module.AsrClient(lock=lock, loader=loaded.append)

        async def _hold_lock(self) -> None:
            async with lock:
                await asyncio.sleep(30.0)

        async def handle_asr(self, raw: bytes) -> dict:
            holder = asyncio.create_task(self._hold_lock())
            try:
                await asyncio.sleep(0.01)  # 让 holder 先把锁拿走
                return await self.client.transcribe(raw)
            finally:
                holder.cancel()

    started = time.monotonic()
    response = make_client(QueuedBusyRunner()).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    elapsed = time.monotonic() - started
    assert response.status_code == 503
    assert response.json() == {"ok": False, "error": "语音识别忙，稍后再试"}
    assert loaded == []  # 等锁超时等于本次识别没开始：loader 一次都不该被调用
    assert 0.15 <= elapsed < 5.0  # 真的等了这个常量（既不是 15 s 默认值，也不是写死的 0.05）


def test_asr_slow_backend_hits_timeout_budget_504(web_api, make_client, monkeypatch):
    class SlowAsrRunner(AsrRunner):
        async def handle_asr(self, raw: bytes) -> dict:
            await asyncio.sleep(1.0)
            return await super().handle_asr(raw)

    monkeypatch.setattr(web_api, "ASR_TIMEOUT_S", 0.05)
    response = make_client(SlowAsrRunner()).post(
        "/mas3d/api/asr",
        content=b"RIFF....WAVE",
        headers={"Authorization": f"Bearer {TOKEN}"},
    )
    assert response.status_code == 504
    assert response.json() == {"ok": False, "error": "语音识别超时"}


def test_asr_returns_transcript_200(web_api, make_client):
    runner = AsrRunner()
    audio = b"RIFF....WAVEfmt " + b"\0" * 32
    response = make_client(runner).post(
        "/mas3d/api/asr",
        content=audio,
        headers={"Authorization": f"Bearer {TOKEN}", "Content-Type": "audio/wav"},
    )
    assert response.status_code == 200
    assert response.json() == {"ok": True, "asr": ASR_RESULT}
    assert runner.asr_calls == [audio]
