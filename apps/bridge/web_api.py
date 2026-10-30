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
from pathlib import Path
from typing import Any

import uvicorn
from fastapi import Depends, FastAPI, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse

from .adapter import TurnDispatchError
from .hastsec import BackendUnavailable, PlanRejected
from .tts import MAX_TEXT_CHARS as MAX_VOICE_CHARS
from .tts import TtsUnavailable

MAX_INPUT_CHARS = 300
"""与 packages/protocol/src/frames.ts:30 的 MAX_INPUT_CHARS 保持一致。"""

RATE_LIMIT_TURNS = 5
"""一个连接在窗口内最多能发几回合。"""

RATE_LIMIT_WINDOW_S = 10.0

PLAN_TIMEOUT_S = 9.0
# 与前端 nextId("p") 生成的长度上限一致；超长的 id 视为没带，回落到服务端自造
MAX_TURN_ID_CHARS = 64
"""POST /mas3d/api/motion/plan 的整体预算；HAST-SEC 客户端自己还有更短的超时。"""

MAX_HISTORY = 8
# 跨连接共用一个回合锁：所有标签页写的是同一个 QQ 会话
_TURN_LOCK = asyncio.Lock()
"""给动作后端看的最近上下文条数（hastsec.build_context 会再收窄到 4 条 / 1200 字）。"""

VOICE_TIMEOUT_S = 70.0
"""POST /mas3d/api/voice/synth 的整体预算；客户端自己还有单句 30s / 整轮 60s 两层超时。"""

LOCAL_HOSTS = frozenset({"127.0.0.1", "::1", "localhost", "testclient"})
CORS_ORIGIN_RE = r"^http://(localhost|127\.0\.0\.1)(:\d{1,5})?$"
"""放行的浏览器来源：只认本机页面，避免任意网页驱动 QQ bot。"""
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
    # 页面从 Vite 开发服务器（localhost:5173）或静态目录打开，和 8790 不同源；
    # 只放行本机来源，令牌仍走 Authorization 头。
    app.add_middleware(
        CORSMiddleware,
        allow_origin_regex=CORS_ORIGIN_RE,
        allow_credentials=False,
        allow_methods=["GET", "POST", "OPTIONS"],
        allow_headers=["authorization", "content-type"],
    )

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

    @app.post("/mas3d/api/motion/plan")
    async def motion_plan(request: Request, _: None = Depends(require_token)) -> JSONResponse:
        """把玩家的原话交给 HAST-SEC 换一份动作计划（design.md D8）。

        降级语义：HAST-SEC 没起来就 503 + bridge_down，前端照常用 Tier0/Tier1。
        """
        try:
            payload = await request.json()
        except (ValueError, UnicodeDecodeError):
            return JSONResponse({"ok": False, "code": "bad_request", "message": "body 必须是 JSON"}, status_code=400)
        text = payload.get("text") if isinstance(payload, dict) else None
        if not isinstance(text, str):
            return JSONResponse({"ok": False, "code": "bad_request", "message": "text 必须是字符串"}, status_code=400)
        cleaned = " ".join(text.split()).strip()
        if not cleaned:
            return JSONResponse({"ok": False, "code": "bad_request", "message": "这句话是空的"}, status_code=400)
        if len(cleaned) > MAX_INPUT_CHARS:
            return JSONResponse(
                {"ok": False, "code": "bad_request", "message": f"一次最多 {MAX_INPUT_CHARS} 个字"},
                status_code=400,
            )
        planner = getattr(runner, "handle_plan", None)
        if planner is None:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": "动作后端未接入"}, status_code=503)
        try:
            plan = await asyncio.wait_for(planner(cleaned, []), timeout=PLAN_TIMEOUT_S)
        except asyncio.TimeoutError:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": "动作后端超时"}, status_code=503)
        except BackendUnavailable as exc:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": str(exc)}, status_code=503)
        except PlanRejected as exc:
            return JSONResponse({"ok": False, "code": "model_error", "message": str(exc)}, status_code=502)
        except RuntimeError as exc:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": str(exc)}, status_code=503)
        if not isinstance(plan, dict):
            return JSONResponse({"ok": False, "code": "bridge_down", "message": "动作后端没有计划"}, status_code=503)
        return JSONResponse({"ok": True, "plan": plan})

    @app.post("/mas3d/api/voice/synth")
    async def voice_synth(request: Request, _: None = Depends(require_token)) -> JSONResponse:
        """把一句台词合成成 WAV（design.md D16 / D17）。

        body: {"text": "...", "sentence"?: n}。不带 sentence 合成整轮并合并成一段；
        带 sentence 只合成第 n 句，前端据此「边播第 n 句、边合成第 n+1 句」。
        降级语义：GPT-SoVITS 没起来就 503 + bridge_down，前端只显示字幕。
        """
        try:
            payload = await request.json()
        except (ValueError, UnicodeDecodeError):
            return JSONResponse({"ok": False, "code": "bad_request", "message": "body 必须是 JSON"}, status_code=400)
        if not isinstance(payload, dict):
            return JSONResponse({"ok": False, "code": "bad_request", "message": "body 必须是 JSON 对象"}, status_code=400)
        text = payload.get("text")
        if not isinstance(text, str):
            return JSONResponse({"ok": False, "code": "bad_request", "message": "text 必须是字符串"}, status_code=400)
        cleaned = " ".join(text.split()).strip()
        if not cleaned:
            return JSONResponse({"ok": False, "code": "bad_request", "message": "这句话是空的"}, status_code=400)
        if len(cleaned) > MAX_VOICE_CHARS:
            return JSONResponse(
                {"ok": False, "code": "bad_request", "message": f"一次最多 {MAX_VOICE_CHARS} 个字"},
                status_code=400,
            )
        sentence = payload.get("sentence")
        if sentence is not None and (isinstance(sentence, bool) or not isinstance(sentence, int)):
            return JSONResponse({"ok": False, "code": "bad_request", "message": "sentence 必须是整数"}, status_code=400)
        if isinstance(sentence, int) and sentence < 0:
            return JSONResponse({"ok": False, "code": "bad_request", "message": "sentence 不能是负数"}, status_code=400)

        synth = getattr(runner, "handle_voice", None)
        if synth is None:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": "语音后端未接入"}, status_code=503)
        try:
            voice = await asyncio.wait_for(synth(cleaned, sentence), timeout=VOICE_TIMEOUT_S)
        except asyncio.TimeoutError:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": "语音合成超时"}, status_code=503)
        except TtsUnavailable as exc:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": str(exc)}, status_code=503)
        except IndexError:
            return JSONResponse({"ok": False, "code": "bad_request", "message": "句子下标越界"}, status_code=400)
        except ValueError as exc:
            return JSONResponse({"ok": False, "code": "bad_request", "message": str(exc) or "台词不合法"}, status_code=400)
        except RuntimeError as exc:
            return JSONResponse({"ok": False, "code": "bridge_down", "message": str(exc)}, status_code=503)
        if not isinstance(voice, dict):
            return JSONResponse({"ok": False, "code": "bridge_down", "message": "语音后端没有返回音频"}, status_code=503)
        return JSONResponse({"ok": True, "voice": voice})

    @app.get("/mas3d/api/voice/{voice_id}.wav")
    async def voice_audio(voice_id: str, _: None = Depends(require_token)) -> FileResponse:
        """读取合成好的 WAV。voice_id 只接受 32 位小写十六进制（tts.wav_path 已校验）。"""
        resolver = getattr(runner, "voice_path", None)
        path = resolver(voice_id) if callable(resolver) else None
        if path is None or not Path(path).is_file():
            raise HTTPException(status_code=404, detail="没有这段语音")
        return FileResponse(path, media_type="audio/wav")

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
        self._history: deque[dict[str, str]] = deque(maxlen=MAX_HISTORY)
        self._tasks: set[asyncio.Task[None]] = set()

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
        # 客户端自造 id（前端 nextId("p")，形如 p-3-lx2k）：只当回显用，限长避免被塞长字符串
        if isinstance(raw_turn_id, str) and 0 < len(raw_turn_id) <= MAX_TURN_ID_CHARS:
            turn_id = raw_turn_id
        else:
            turn_id = uuid.uuid4().hex[:12]

        await websocket.send_json({"type": "ack", "turnId": turn_id})
        await websocket.send_json({"type": "thinking", "turnId": turn_id})

        try:
            # 跨连接串行：同一个 QQ 会话只有一条时间线，两个标签页同时说话会让回合交错写进历史
            async with _TURN_LOCK:
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
        context = list(self._history)
        self._history.append({"role": "user", "content": text})
        self._history.append({"role": "assistant", "content": reply})
        # 动作后端最多 9 秒，不能顶住下一条消息；回合文本已经落 history，帧带 turnId 可乱序到达
        self._schedule_intent(websocket, turn_id, cleaned, str(reply), context)

    def _schedule_intent(
        self,
        websocket: WebSocket,
        turn_id: str,
        text: str,
        reply: str,
        context: list[dict[str, str]],
    ) -> None:
        task = asyncio.create_task(self._maybe_intent(websocket, turn_id, text, reply, context))
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    async def _maybe_intent(
        self,
        websocket: WebSocket,
        turn_id: str,
        text: str,
        reply: str,
        context: list[dict[str, str]],
    ) -> None:
        """回复之后问一句动作后端；只有 command 计划才发 intent 帧，失败静默降级。"""
        planner = getattr(self._runner, "handle_plan", None)
        if planner is not None:
            plan: Any = None
            try:
                plan = await asyncio.wait_for(planner(text, context), timeout=PLAN_TIMEOUT_S)
            except (asyncio.TimeoutError, BackendUnavailable, PlanRejected, RuntimeError, OSError):
                plan = None
            except Exception:  # noqa: BLE001 - 动作后端再怎么坏都不该打断对话
                plan = None
            if (
                isinstance(plan, dict)
                and plan.get("operation") == "command"
                and isinstance(plan.get("intents"), list)
                and plan["intents"]
            ):
                try:
                    await websocket.send_json({"type": "intent", "turnId": turn_id, "plan": plan})
                except (RuntimeError, WebSocketDisconnect):
                    pass  # 玩家已经关掉页面，动作计划没有接收方


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
    "MAX_HISTORY",
    "MAX_INPUT_CHARS",
    "PLAN_TIMEOUT_S",
    "RATE_LIMIT_TURNS",
    "RATE_LIMIT_WINDOW_S",
    "VOICE_TIMEOUT_S",
    "TurnSession",
    "bearer_token",
    "create_app",
    "is_local_host",
    "token_matches",
]