"""本机（127.0.0.1）HTTP / WebSocket 端点。

为什么不用 AstrBot 的 register_web_api：
- core/star/context.py:705 的注册表只接受 HTTP 方法，WS 完全没入口；
- 注册出来的路由只能经面板 /api/plug/<plugin_path> 转发，带的是面板账号的登录态，
  浏览器里的 3D 页面既不该拿面板 JWT，也不该让面板成为对话主通道。

所以这里在插件进程里自起一个只绑 127.0.0.1 的 uvicorn（AstrBot 自带 fastapi /
uvicorn / websockets），用插件自己的令牌鉴权。对应 design.md D3 / D4。

协议（packages/protocol/src/frames.ts）：
- 客户端：{"type": "say", "text": "..."}，text ≤ 300 字
- 服务端：ack{turnId} → thinking{turnId} → reply{turnId,text}（voice 由 Task 14 补）
- 错误：{"type": "error", "code", "message", "turnId"?}，连接不断
"""

from __future__ import annotations

import asyncio
import contextlib
import hmac
import json
import time
import uuid
from collections import deque
from typing import Any

import uvicorn
from fastapi import Depends, FastAPI, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse

from .adapter import TurnDispatchError

MAX_INPUT_CHARS = 300
"""与 packages/protocol/src/frames.ts:30 的 MAX_INPUT_CHARS 保持一致。"""

RATE_LIMIT_TURNS = 5
"""一个连接在窗口内最多能发几回合。"""

RATE_LIMIT_WINDOW_S = 10.0

LOCAL_HOSTS = frozenset({"127.0.0.1", "::1", "localhost", "testclient"})
"""允许的来源主机。testclient 只会出现在进程内 ASGI 测试传输里，不可能来自真实 socket。"""

UNAUTHORIZED = "令牌不正确（%s/mas3d/api/health 需要 Authorization: Bearer <token>）"


def bearer_token(headers: Any) -> str:
    """从 Authorization 头里取 Bearer 令牌。"""
    raw = headers.get("authorization") if headers is not None else None
    if not isinstance(raw, str):
        return ""
    scheme, _, value = raw.partition(" ")
    return value.strip() if scheme.lower() == "bearer" else ""


def token_matches(expected: str, supplied: str) -> bool:
    """定长比较，避免用比较耗时泄露令牌前缀。"""
    if not expected or not supplied:
        return False
    return hmac.compare_digest(expected, supplied)


def is_local_host(host: str | None) -> bool:
    return bool(host) and host in LOCAL_HOSTS


def create_app(
    runner: Any,
    token: str,
    *,
    plugin_version: str = "0.1.0",
    turn_timeout_s: float = 120.0,
) -> FastAPI:
    """造出桥接服务。runner 需要提供 umo / degraded / async handle_turn(text)。"""
    app = FastAPI(title="Mas3D bridge", docs_url=None, redoc_url=None, openapi_url=None)

    async def require_token(request: Request) -> None:
        supplied = bearer_token(request.headers) or request.query_params.get("token", "")
        if not token_matches(token, supplied):
            raise HTTPException(status_code=401, detail=UNAUTHORIZED % "http://127.0.0.1:8790")

    @app.middleware("http")
    async def only_local(request: Request, call_next: Any):
        host = request.client.host if request.client else None
        if not is_local_host(host):
            return JSONResponse({"ok": False, "error": "只接受本机请求"}, status_code=403)
        return await call_next(request)

    @app.get("/mas3d/api/health")
    async def health(_: None = Depends(require_token)) -> dict[str, Any]:
        return {
            "ok": True,
            "plugin": plugin_version,
            "umo": str(getattr(runner, "umo", "")),
            "degraded": bool(getattr(runner, "degraded", False)),
        }

    @app.websocket("/mas3d/ws")
    async def conversation(websocket: WebSocket) -> None:
        host = websocket.client.host if websocket.client else None
        if not is_local_host(host):
            await websocket.close(code=1008)
            return
        supplied = websocket.query_params.get("token", "") or bearer_token(websocket.headers)
        if not token_matches(token, supplied):
            await websocket.close(code=1008)
            return
        await websocket.accept()
        session = TurnSession(runner, turn_timeout_s=turn_timeout_s)
        try:
            while True:
                raw = await websocket.receive_text()
                await session.handle(websocket, raw)
        except (WebSocketDisconnect, RuntimeError):
            return

    return app


class TurnSession:
    """单条连接的回合循环：串行处理、限流、把异常翻译成 error 帧。

    回合之间串行是刻意的：一条连接代表一个玩家，前端在等待期间本来就禁用输入
    （packages 的 Phase=BUSY），这里再串行一次可以把「晚到的第二句」排到第一句之后，
    而不是让两条回合在 AstrBot 的同一会话里交错。
    """

    def __init__(self, runner: Any, *, turn_timeout_s: float = 120.0) -> None:
        self._runner = runner
        self._timeout_s = turn_timeout_s
        self._sent: deque[float] = deque()

    def _prune(self, now: float) -> None:
        window = now - RATE_LIMIT_WINDOW_S
        while self._sent and self._sent[0] < window:
            self._sent.popleft()

    def _rate_limited(self, now: float) -> bool:
        self._prune(now)
        return len(self._sent) >= RATE_LIMIT_TURNS

    async def handle(self, websocket: WebSocket, raw: str) -> None:
        try:
            payload = json.loads(raw)
        except (TypeError, ValueError):
            await _send_error(websocket, "bad_request", "帧必须是 JSON 对象")
            return
        if not isinstance(payload, dict) or payload.get("type") != "say":
            await _send_error(websocket, "bad_request", "只支持 say 帧")
            return

        text = payload.get("text")
        if not isinstance(text, str):
            await _send_error(websocket, "bad_request", "text 必须是字符串")
            return
        cleaned = " ".join(text.split()).strip()
        if not cleaned:
            await _send_error(websocket, "bad_request", "这句话是空的")
            return
        if len(cleaned) > MAX_INPUT_CHARS:
            await _send_error(websocket, "bad_request", f"一次最多 {MAX_INPUT_CHARS} 个字")
            return

        now = time.monotonic()
        if self._rate_limited(now):
            await _send_error(websocket, "rate_limited", "说得太快了，让我缓一下")
            return
        self._sent.append(now)

        raw_turn_id = payload.get("turnId")
        turn_id = raw_turn_id if isinstance(raw_turn_id, str) and raw_turn_id else uuid.uuid4().hex[:12]

        await websocket.send_json({"type": "ack", "turnId": turn_id})
        await websocket.send_json({"type": "thinking", "turnId": turn_id})

        try:
            reply = await asyncio.wait_for(self._runner.handle_turn(cleaned), timeout=self._timeout_s)
        except asyncio.TimeoutError:
            await _send_error(websocket, "timeout", "模型这次没有回应", turn_id)
            return
        except TurnDispatchError as exc:
            await _send_error(websocket, "bridge_down", str(exc) or "AstrBot 管线没有接管这条消息", turn_id)
            return
        except RuntimeError as exc:
            await _send_error(websocket, "bridge_down", str(exc) or "桥接未初始化", turn_id)
            return
        except Exception:  # noqa: BLE001 - 第三方模型/pipeline 的异常一律按模型失败回报
            await _send_error(websocket, "model_error", "她这次没有说出话来", turn_id)
            return

        if not str(reply).strip():
            await _send_error(websocket, "model_error", "她这次没有说出话来", turn_id)
            return
        # 不带 cue：表情由前端 deriveCue 推导（design.md D6），桥接只回原文
        await websocket.send_json({"type": "reply", "turnId": turn_id, "text": str(reply)})


async def _send_error(websocket: WebSocket, code: str, message: str, turn_id: str | None = None) -> None:
    frame: dict[str, Any] = {"type": "error", "code": code, "message": message}
    if turn_id:
        frame["turnId"] = turn_id
    await websocket.send_json(frame)


class LocalServer:
    """把 FastAPI app 挂到 127.0.0.1 上的 uvicorn，并跟随插件生命周期启停。"""

    def __init__(
        self,
        app: FastAPI,
        *,
        host: str = "127.0.0.1",
        port: int = 8790,
        startup_timeout_s: float = 5.0,
    ) -> None:
        self._app = app
        self.host = host
        self.port = port
        self._startup_timeout_s = startup_timeout_s
        self._server: uvicorn.Server | None = None
        self._task: asyncio.Task[Any] | None = None
        self.error: str | None = None

    @property
    def url(self) -> str:
        return f"http://{self.host}:{self.port}"

    @property
    def running(self) -> bool:
        return bool(self._server is not None and getattr(self._server, "started", False))

    async def start(self) -> bool:
        """启动监听；端口占用等失败只记录 error，不让插件加载失败。"""
        if self.running:
            return True
        config = uvicorn.Config(
            self._app,
            host=self.host,
            port=self.port,
            log_level="warning",
            access_log=False,
            lifespan="off",
        )
        server = uvicorn.Server(config)
        # 这里跑在 AstrBot 的进程里：不能让它接管 Ctrl+C 的信号处理
        server.install_signal_handlers = lambda: None  # type: ignore[method-assign]
        server.capture_signals = contextlib.nullcontext  # type: ignore[method-assign]
        self._server = server
        self._task = asyncio.create_task(server.serve(), name="mas3d-bridge-server")

        deadline = time.monotonic() + self._startup_timeout_s
        while time.monotonic() < deadline:
            if server.started:
                return True
            if self._task.done():
                break
            await asyncio.sleep(0.02)
        self.error = f"无法监听 {self.host}:{self.port}（端口被占用？）"
        await self.stop()
        return False

    async def stop(self) -> None:
        server, task = self._server, self._task
        self._server = None
        self._task = None
        if server is None:
            return
        server.should_exit = True
        if task is None:
            return
        try:
            await asyncio.wait_for(task, timeout=5.0)
        except (asyncio.TimeoutError, asyncio.CancelledError):
            task.cancel()
        except Exception:  # noqa: BLE001 - 关闭失败不该影响插件卸载
            pass


__all__ = [
    "LOCAL_HOSTS",
    "LocalServer",
    "MAX_INPUT_CHARS",
    "RATE_LIMIT_TURNS",
    "RATE_LIMIT_WINDOW_S",
    "TurnSession",
    "bearer_token",
    "create_app",
    "is_local_host",
    "token_matches",
]
