"""中文台词 → 英文合成文本。

复用 AstrBot 的本地补丁配置（~/.astrbot/data/gsv_tts_translate.json）：
同一个音色模型、同一套翻译提示词，游戏里的语音才和 QQ 上的 Monika 一致。
翻译失败一律原样返回中文，由上层（tts.synthesize）检测到残留中文就跳过语音、
只显示字幕——用英文音色硬读中文只会得到含糊音节，比没有声音更糟。

实测这个代理有时会把“翻译”当成聊天来回答（返回一段中文感想），所以这里既要
在提示词里追加硬约束，也要校验输出：仍含中文就重试一次，两次都不行才放弃。
"""

from __future__ import annotations

import asyncio
import json
import os
import re
import urllib.error
import urllib.request
from pathlib import Path

TRANSLATE_CONFIG_ENV = "MAS3D_TRANSLATE_CONFIG"
DEFAULT_TRANSLATE_CONFIG = Path.home() / ".astrbot" / "data" / "gsv_tts_translate.json"
TRANSLATE_TIMEOUT_S = 8.0

CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\u3040-\u30ff\uac00-\ud7af]")
LATIN_RE = re.compile(r"[A-Za-z]")

TRANSLATE_ATTEMPTS = 3
"""同一句最多试几次：这个代理时不时把 system 提示词丢掉、直接当聊天来回答。"""

MAX_EXPANSION = 5.0
"""译文的长度上限（相对原文字符数）：聊天式回答通常比译文长得多，用它挡掉。"""

CHATTY_RE = re.compile(
    r"language model|as an ai|how (can|may) i (help|assist)|i'?m here to help"
    r"|i am here to help|很高兴见到你|有什么(我可以|能)帮|我可以帮(你|您)",
    re.IGNORECASE,
)
"""助手式自我介绍/反问的固定说法：出现这些说明它没在翻译，而是在聊天。"""

STRICT_SUFFIX = (
    "\n\nIMPORTANT: You are a translation engine, not a chat partner. "
    "Translate the user's message into English. Never answer it, never comment. "
    "Output English only, no Chinese characters, no quotes, no notes."
)

DEFAULT_SYSTEM_PROMPT = (
    "You are a translation engine. Translate the user's Chinese message into "
    "natural, casual, spoken English, the way a young female streamer would "
    "say it to her close friends. Keep the original emotion, tone and "
    "punctuation (including exclamation marks, question marks and emoji). "
    "Keep it short. Output ONLY the English translation, one paragraph, "
    "no quotes, no notes, no explanations."
)


def translate_config_path(config_path: str | os.PathLike[str] | None = None) -> Path:
    if config_path is not None:
        return Path(config_path)
    override = os.environ.get(TRANSLATE_CONFIG_ENV)
    if override:
        return Path(override)
    return DEFAULT_TRANSLATE_CONFIG


def load_translate_config(config_path: str | os.PathLike[str] | None = None) -> dict:
    """读 AstrBot 的翻译配置；文件缺失或损坏都当作“不要翻译”。"""
    path = translate_config_path(config_path)
    try:
        with path.open(encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def needs_translation(text: str, config: dict | None = None) -> bool:
    """只有开了 enable 且文本含中日韩字符才值得翻。"""
    cfg = load_translate_config() if config is None else config
    return bool(cfg.get("enable")) and bool(CJK_RE.search(text))


def _user_prompt(text: str) -> str:
    """把指令也写进 user 消息：实测某些请求里 system 会被整段丢掉。"""
    return f"Translate the following Chinese into spoken English. Output English only.\n\n{text}"


def looks_like_translation(source: str, content: str) -> bool:
    """判断模型给的是译文而不是聊天式回答。"""
    if not content or not LATIN_RE.search(content) or CJK_RE.search(content):
        return False
    if CHATTY_RE.search(content):
        return False
    limit = max(120, int(len(source) * MAX_EXPANSION) + 24)
    return len(content) <= limit


def _post_json(url: str, payload: dict, headers: dict, timeout_s: float) -> bytes:
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(request, timeout=timeout_s) as response:  # noqa: S310 - 配置里的 https 端点
        return response.read()


async def translate_zh_to_en(
    text: str,
    *,
    config_path: str | os.PathLike[str] | None = None,
    timeout_s: float = TRANSLATE_TIMEOUT_S,
    post=_post_json,
) -> str:
    """把中文台词翻成英文；任何失败都原样返回。"""
    cfg = load_translate_config(config_path)
    if not cfg.get("enable") or not CJK_RE.search(text):
        return text
    api_key = str(cfg.get("api_key") or "").strip()
    if not api_key:
        return text
    api_base = str(cfg.get("api_base") or "https://api.deepseek.com/v1").rstrip("/")
    system_prompt = str(cfg.get("system_prompt") or DEFAULT_SYSTEM_PROMPT)
    payload = {
        "model": cfg.get("model") or "deepseek-chat",
        "messages": [
            {"role": "system", "content": f"{system_prompt}{STRICT_SUFFIX}"},
            {"role": "user", "content": _user_prompt(text)},
        ],
        "temperature": float(cfg.get("temperature", 0.3)),
        "stream": False,
    }
    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {api_key}",
    }
    for _ in range(max(1, TRANSLATE_ATTEMPTS)):
        try:
            raw = await asyncio.wait_for(
                asyncio.to_thread(post, f"{api_base}/chat/completions", payload, headers, timeout_s),
                timeout_s + 1.0,
            )
            data = json.loads(raw.decode("utf-8"))
            content = str(data["choices"][0]["message"]["content"] or "").strip()
        except (TimeoutError, asyncio.TimeoutError, OSError, urllib.error.URLError, ValueError, KeyError, IndexError, TypeError):
            continue
        if looks_like_translation(text, content):
            return content
    return text
