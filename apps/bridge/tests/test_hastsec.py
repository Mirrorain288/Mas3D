"""Task 13 的验收：HAST-SEC 代理的头部伪造、入参整形、返回校验与冷却。"""

from __future__ import annotations

import asyncio
import importlib
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

import pytest

GOOD_PLAN = {
    "base": {"change": False, "name": "坐姿待机", "motion": 0.0, "rest": False},
    "operation": "command",
    "timing": "同时",
    "intents": [{"key": "右臂上抬", "speed": 1.0, "strength": 0.6, "hold": 2.0, "delay": 0.0}],
}


@pytest.fixture(scope="module")
def hastsec(plugin):
    return importlib.import_module("astrbot_plugin_mas3d_game.hastsec")


class _Handler(BaseHTTPRequestHandler):
    """记录请求并回放固定响应的最小上游。"""

    server_version = "FakeHAST/0.0"
    plan_payload: object = GOOD_PLAN
    plan_status = 200
    status_status = 200
    recorded: list[dict] = []

    def log_message(self, *args):  # noqa: D102 - 测试里不需要访问日志
        return

    def _reply(self, code: int, payload: object) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _record(self, body: bytes) -> None:
        type(self).recorded.append(
            {
                "method": self.command,
                "path": urlparse(self.path).path,
                "host": self.headers.get("Host"),
                "origin": self.headers.get("Origin"),
                "content_type": self.headers.get("content-type"),
                "body": json.loads(body.decode("utf-8")) if body else None,
            }
        )

    def do_GET(self):  # noqa: N802 - BaseHTTPRequestHandler 的约定
        self._record(b"")
        if self.path.startswith("/v13-api/status"):
            self._reply(type(self).status_status, {"runtime": "idle", "training": False})
        else:
            self._reply(404, {"error": "not found"})

    def do_POST(self):  # noqa: N802
        length = int(self.headers.get("content-length") or 0)
        body = self.rfile.read(length)
        self._record(body)
        if self.path.startswith("/v13-api/plan"):
            self._reply(type(self).plan_status, type(self).plan_payload)
        else:
            self._reply(404, {"error": "not found"})


@pytest.fixture()
def upstream():
    handler = type("Handler", (_Handler,), {"recorded": [], "plan_payload": GOOD_PLAN, "plan_status": 200, "status_status": 200})
    server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    class Upstream:
        port = server.server_address[1]
        recorded = handler.recorded

        @staticmethod
        def set_plan(payload, status_code: int = 200) -> None:
            handler.plan_payload = payload
            handler.plan_status = status_code

        @staticmethod
        def set_status(status_code: int) -> None:
            handler.status_status = status_code

    yield Upstream
    server.shutdown()
    server.server_close()
    thread.join(timeout=2)


def make_client(hastsec, tmp_path: Path, port: int, **kwargs):
    return hastsec.HastSecClient(port=port, session_path=tmp_path / "mas3d" / "session.json", **kwargs)


def test_forwards_origin_and_host(hastsec, upstream, tmp_path):
    client = make_client(hastsec, tmp_path, upstream.port)
    asyncio.run(client.plan("抬起右手"))

    plan_call = [row for row in upstream.recorded if row["path"] == "/v13-api/plan"][0]
    assert plan_call["host"] == f"127.0.0.1:{upstream.port}"
    assert plan_call["origin"] == f"http://127.0.0.1:{upstream.port}"
    assert plan_call["content_type"] == "application/json"
    assert plan_call["body"]["generation"] == "v15"


def test_truncates_text_to_300(hastsec, upstream, tmp_path):
    client = make_client(hastsec, tmp_path, upstream.port)
    asyncio.run(client.plan("说" * 400))
    plan_call = [row for row in upstream.recorded if row["path"] == "/v13-api/plan"][0]
    assert len(plan_call["body"]["text"]) == hastsec.MAX_TEXT_CHARS


def test_context_capped_to_4_and_1200(hastsec, upstream, tmp_path):
    client = make_client(hastsec, tmp_path, upstream.port)
    turns = [{"role": "user" if i % 2 == 0 else "assistant", "content": "记" * 700} for i in range(6)]
    asyncio.run(client.plan("继续", turns))
    plan_call = [row for row in upstream.recorded if row["path"] == "/v13-api/plan"][0]
    context = plan_call["body"]["context"]
    assert 0 < len(context) <= hastsec.MAX_CONTEXT_TURNS
    assert all(len(row["content"]) <= hastsec.MAX_CONTEXT_ITEM_CHARS for row in context)
    assert sum(len(row["content"]) for row in context) <= hastsec.MAX_CONTEXT_TOTAL_CHARS


def test_rejects_invalid_plan(hastsec, upstream, tmp_path):
    upstream.set_plan({**GOOD_PLAN, "operation": "chat"})  # chat 不能带 intents
    client = make_client(hastsec, tmp_path, upstream.port)
    with pytest.raises(hastsec.PlanRejected):
        asyncio.run(client.plan("抬起右手"))


def test_cooldown_after_failure(hastsec, tmp_path):
    client = make_client(hastsec, tmp_path, 9)  # 9 号端口必然连不上
    with pytest.raises(hastsec.BackendUnavailable):
        asyncio.run(client.plan("抬起右手"))
    assert client.cooling_down

    calls: list[str] = []
    original = client._request

    def spy(*args, **kwargs):
        calls.append(args[1])
        return original(*args, **kwargs)

    client._request = spy  # type: ignore[method-assign]
    with pytest.raises(hastsec.BackendUnavailable):
        asyncio.run(client.plan("再抬一次"))
    assert calls == []


def test_session_guid_persisted(hastsec, tmp_path):
    path = tmp_path / "mas3d" / "session.json"
    first = hastsec.HastSecClient(port=8769, session_path=path)
    assert path.exists()
    stored = json.loads(path.read_text(encoding="utf-8"))["session"]
    assert stored == first.session_id
    second = hastsec.HastSecClient(port=8769, session_path=path)
    assert second.session_id == first.session_id


def test_status_probe_failure_sets_cooldown(hastsec, upstream, tmp_path):
    upstream.set_status(500)
    client = make_client(hastsec, tmp_path, upstream.port)
    with pytest.raises(hastsec.BackendUnavailable):
        asyncio.run(client.status())
    assert client.cooling_down


def test_status_ok_returns_payload(hastsec, upstream, tmp_path):
    client = make_client(hastsec, tmp_path, upstream.port)
    payload = asyncio.run(client.status())
    assert payload["runtime"] == "idle"
    assert client.cooling_down is False
