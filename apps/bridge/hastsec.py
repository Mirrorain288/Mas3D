"""HAST-SEC 动作意图代理（design.md D8）。

HAST-SEC（D:\\HAST-SEC-v0.1.1）的 v13/v15 接口有一条硬约束：

    if (req.headers.origin !== 'http://' + req.headers.host) throw ...

也就是它只接受"同源"的本机页面请求。浏览器里做不到（页面在 5173），
所以由桥接在服务端代发，并显式把 Host / Origin 都写成它自己的地址。

为什么不用 httpx：httpx 会把 Host 头按 URL 重写，http.client 可以用
putrequest(..., skip_host=True) 精确控制，代价是同步调用 —— 放进线程池即可。

上游只用它的高层意图 JSON（0.8B 模型输出的 base + intents），轨迹解码发生在
HAST-SEC 自己的网页里，我们不引入它那 1.1GB runtime。
"""

from __future__ import annotations

import asyncio
import http.client
import json
import re
import time
import uuid
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

HAST_HOST = "127.0.0.1"
HAST_PORT = 8769

PROBE_TIMEOUT_S = 1.5
"""GET /v13-api/status 的预算，对齐计划里的"探活 1.5s"。"""

REQUEST_TIMEOUT_S = 6.0
"""POST /v13-api/plan 的预算。实测冷启动（模型未驻留）2.62s，热态 0.7–1.0s；
HAST-SEC 自己 10s 读超时，我们留出余量但更短，失败即降级。"""

COOLDOWN_S = 60.0
"""失败后多久不再尝试，避免每回合都白等超时。"""

MAX_TEXT_CHARS = 300
"""HAST-SEC 侧要求 text 1..300 字。"""

MAX_CONTEXT_TURNS = 4
MAX_CONTEXT_TOTAL_CHARS = 1200
MAX_CONTEXT_ITEM_CHARS = 600

SESSION_FILE = "session.json"
_SESSION_RE = re.compile(r"^\w{8}-\w{4}-\w{4}-\w{4}-\w{12}$")

OPERATIONS = ("chat", "command", "release", "unsupported")
TIMINGS = ("同时", "依次")
MAX_INTENTS = 4
MAX_BASE_NAME = 40
MAX_REPLY = 600


class BackendUnavailable(RuntimeError):
    """HAST-SEC 没在跑、超时、或者回了 5xx。降级到 Tier0/Tier1 即可。"""


class PlanRejected(ValueError):
    """上游回了 400，或者回来的 JSON 不符合 motion 协议。"""


def _fail(message: str) -> None:
    raise PlanRejected(message)


def _is_record(value: Any) -> bool:
    return isinstance(value, dict)


def _num(obj: Mapping[str, Any], key: str, low: float, high: float, where: str) -> float:
    value = obj.get(key)
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        _fail(f"{where}.{key} 必须是数字")
    number = float(value)
    if number != number or number in (float("inf"), float("-inf")):
        _fail(f"{where}.{key} 必须是有限数字")
    if number < low or number > high:
        _fail(f"{where}.{key} 必须落在 {low}..{high}")
    return number


def _bool(obj: Mapping[str, Any], key: str, where: str) -> bool:
    value = obj.get(key)
    if not isinstance(value, bool):
        _fail(f"{where}.{key} 必须是布尔值")
    return value


def _validate_base(raw: Any) -> dict[str, Any]:
    if not _is_record(raw):
        _fail("base 必须是对象")
    name = raw.get("name")
    if not isinstance(name, str) or not name:
        _fail("base.name 必须是非空字符串")
    if len(name) > MAX_BASE_NAME:
        _fail(f"base.name 最多 {MAX_BASE_NAME} 个字符")
    return {
        "change": _bool(raw, "change", "base"),
        "name": name,
        "motion": _num(raw, "motion", 0.0, 1.0, "base"),
        "rest": _bool(raw, "rest", "base"),
    }


def _validate_intent(raw: Any, index: int) -> dict[str, Any]:
    where = f"intents[{index}]"
    if not _is_record(raw):
        _fail(f"{where} 必须是对象")
    key = raw.get("key")
    if not isinstance(key, str) or not key:
        _fail(f"{where}.key 必须是非空字符串")
    return {
        "key": key,
        "speed": _num(raw, "speed", 0.4, 2.0, where),
        "strength": _num(raw, "strength", 0.0, 1.0, where),
        "hold": _num(raw, "hold", 0.0, 20.0, where),
        "delay": _num(raw, "delay", 0.0, 30.0, where),
    }


def validate_plan(raw: Any) -> dict[str, Any]:
    """按 packages/protocol/src/plan.ts 的规则校验，供前端直接消费。"""
    if not _is_record(raw):
        _fail("plan 必须是对象")
    operation = raw.get("operation")
    if not isinstance(operation, str) or operation not in OPERATIONS:
        _fail("plan.operation 不合法")
    timing = raw.get("timing")
    if not isinstance(timing, str) or timing not in TIMINGS:
        _fail("plan.timing 必须是 同时 或 依次")
    intents_raw = raw.get("intents")
    if not isinstance(intents_raw, list):
        _fail("plan.intents 必须是数组")
    if len(intents_raw) > MAX_INTENTS:
        _fail(f"plan.intents 最多 {MAX_INTENTS} 条")
    base = _validate_base(raw.get("base"))
    intents = [_validate_intent(item, i) for i, item in enumerate(intents_raw)]
    if operation == "command":
        if not intents and not base["change"]:
            _fail("command 计划至少要有一条 intent 或 base.change")
    elif intents:
        _fail(f"{operation} 计划不能带 intents")

    plan: dict[str, Any] = {
        "base": base,
        "operation": operation,
        "timing": timing,
        "intents": intents,
    }
    reply = raw.get("reply")
    if reply is not None:
        if not isinstance(reply, str):
            _fail("plan.reply 必须是字符串")
        if len(reply) > MAX_REPLY:
            _fail(f"plan.reply 最多 {MAX_REPLY} 个字符")
        plan["reply"] = reply
    return plan


def build_context(turns: Iterable[Mapping[str, Any]]) -> list[dict[str, str]]:
    """最近 ≤4 条、单条 ≤600 字、总长 ≤1200 字，超出从最旧开始丢。"""
    rows: list[dict[str, str]] = []
    for turn in turns:
        role = str(turn.get("role", ""))
        content = turn.get("content")
        if role not in ("user", "assistant") or not isinstance(content, str):
            continue
        text = content.strip()
        if not text:
            continue
        rows.append({"role": role, "content": text[:MAX_CONTEXT_ITEM_CHARS]})
    rows = rows[-MAX_CONTEXT_TURNS:]
    while rows and sum(len(row["content"]) for row in rows) > MAX_CONTEXT_TOTAL_CHARS:
        rows.pop(0)
    return rows


def load_session(session_path: Path) -> str:
    """读持久 GUID；缺失/损坏就新造一个写回去。"""
    try:
        raw = json.loads(session_path.read_text(encoding="utf-8"))
        value = raw.get("session") if isinstance(raw, dict) else None
        if isinstance(value, str) and _SESSION_RE.match(value):
            return value
    except (OSError, ValueError):
        pass
    value = str(uuid.uuid4())
    try:
        session_path.parent.mkdir(parents=True, exist_ok=True)
        session_path.write_text(json.dumps({"session": value}, ensure_ascii=False), encoding="utf-8")
    except OSError:
        pass
    return value


class HastSecClient:
    """HAST-SEC 的高层意图客户端：探活 → 要计划 → 校验 → 冷却。"""

    def __init__(
        self,
        *,
        host: str = HAST_HOST,
        port: int = HAST_PORT,
        session_path: Path,
        probe_timeout_s: float = PROBE_TIMEOUT_S,
        request_timeout_s: float = REQUEST_TIMEOUT_S,
        cooldown_s: float = COOLDOWN_S,
        clock: Any = time.monotonic,
    ) -> None:
        self.host = host
        self.port = port
        self.session_id = load_session(session_path)
        self._probe_timeout_s = probe_timeout_s
        self._request_timeout_s = request_timeout_s
        self._cooldown_s = cooldown_s
        self._clock = clock
        self._cooldown_until = 0.0
        self._lock = asyncio.Lock()

    @property
    def origin(self) -> str:
        return f"http://{self.host}:{self.port}"

    @property
    def host_header(self) -> str:
        return f"{self.host}:{self.port}"

    @property
    def cooling_down(self) -> bool:
        return self._clock() < self._cooldown_until

    @property
    def cooldown_remaining(self) -> float:
        return max(0.0, self._cooldown_until - self._clock())

    def _cool(self) -> None:
        self._cooldown_until = self._clock() + self._cooldown_s

    def _request(self, method: str, path: str, payload: Any = None, *, timeout: float) -> tuple[int, bytes]:
        body = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        conn = http.client.HTTPConnection(self.host, self.port, timeout=timeout)
        try:
            conn.putrequest(method, path, skip_host=True, skip_accept_encoding=True)
            conn.putheader("Host", self.host_header)
            conn.putheader("Origin", self.origin)
            conn.putheader("Accept", "application/json")
            if body is not None:
                conn.putheader("content-type", "application/json")
                conn.putheader("Content-Length", str(len(body)))
            conn.endheaders(body)
            response = conn.getresponse()
            return response.status, response.read()
        finally:
            conn.close()

    async def status(self) -> dict[str, Any]:
        """探活。任何失败都进冷却并抛 BackendUnavailable。"""
        if self.cooling_down:
            raise BackendUnavailable("HAST-SEC 正在冷却")
        try:
            code, raw = await asyncio.to_thread(
                self._request, "GET", "/v13-api/status", None, timeout=self._probe_timeout_s
            )
        except (OSError, http.client.HTTPException) as exc:
            self._cool()
            raise BackendUnavailable(f"HAST-SEC 连不上：{exc}") from exc
        if code != 200:
            self._cool()
            raise BackendUnavailable(f"HAST-SEC 探活返回 {code}")
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as exc:
            self._cool()
            raise BackendUnavailable("HAST-SEC 探活响应不是 JSON") from exc
        if not isinstance(payload, dict):
            self._cool()
            raise BackendUnavailable("HAST-SEC 探活响应格式不对")
        return payload

    async def plan(self, text: str, context: Iterable[Mapping[str, Any]] = ()) -> dict[str, Any]:
        """要一份动作计划。上游不可用 → BackendUnavailable；返回不合法 → PlanRejected。"""
        cleaned = " ".join(str(text).split()).strip()
        if not cleaned:
            raise PlanRejected("text 不能为空")
        async with self._lock:
            await self.status()
            payload = {
                "text": cleaned[:MAX_TEXT_CHARS],
                "context": build_context(context),
                "session": self.session_id,
                "generation": "v15",
            }
            try:
                code, raw = await asyncio.to_thread(
                    self._request, "POST", "/v13-api/plan", payload, timeout=self._request_timeout_s
                )
            except TimeoutError as exc:
                # 超时多半是模型正在冷启动：不进冷却，下一回合再试
                raise BackendUnavailable(f"HAST-SEC 这次太慢（{self._request_timeout_s:.0f}s）") from exc
            except (OSError, http.client.HTTPException) as exc:
                self._cool()
                raise BackendUnavailable(f"HAST-SEC 请求失败：{exc}") from exc
        if code == 400:
            raise PlanRejected(_error_text(raw) or "HAST-SEC 拒绝了这段文本")
        if code != 200:
            self._cool()
            raise BackendUnavailable(f"HAST-SEC 返回 {code}")
        try:
            parsed = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as exc:
            raise PlanRejected("HAST-SEC 返回的不是 JSON") from exc
        return validate_plan(parsed)


def _error_text(raw: bytes) -> str:
    try:
        payload = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return ""
    if isinstance(payload, dict) and isinstance(payload.get("error"), str):
        return payload["error"]
    return ""


__all__ = [
    "BackendUnavailable",
    "COOLDOWN_S",
    "HastSecClient",
    "MAX_CONTEXT_ITEM_CHARS",
    "MAX_CONTEXT_TOTAL_CHARS",
    "MAX_CONTEXT_TURNS",
    "MAX_TEXT_CHARS",
    "PROBE_TIMEOUT_S",
    "REQUEST_TIMEOUT_S",
    "PlanRejected",
    "build_context",
    "load_session",
    "validate_plan",
]
