"""TTS 客户端与翻译：权重锁定、缓存、逐句时间线、降级。"""

from __future__ import annotations

import asyncio
import importlib
import json
import struct
import time

import pytest

PLUGIN = "astrbot_plugin_mas3d_game"


def _wav(ms: int, sample: int = 0) -> bytes:
    """造一段 32kHz 单声道 16-bit WAV，data 长度按毫秒算。"""
    frames = int(32000 * ms / 1000)
    body = struct.pack("<h", sample) * frames
    fmt = struct.pack("<HHIIHH", 1, 1, 32000, 32000 * 2, 2, 16)
    fmt_chunk = b"fmt " + struct.pack("<I", len(fmt)) + fmt
    data_chunk = b"data" + struct.pack("<I", len(body)) + body
    riff = b"RIFF" + struct.pack("<I", 4 + len(fmt_chunk) + len(data_chunk)) + b"WAVE"
    return riff + fmt_chunk + data_chunk


class FakeTransport:
    """假的 GPT-SoVITS：记录调用顺序，按句长返回递增毫秒的 WAV。"""

    def __init__(self, ms_per_call: int = 200, delay_s: float = 0.0):
        self.calls: list[tuple[str, dict]] = []
        self.ms_per_call = ms_per_call
        self.delay_s = delay_s

    def __call__(self, path: str, params: dict, timeout_s: float) -> bytes:
        self.calls.append((path, dict(params)))
        if self.delay_s:
            time.sleep(self.delay_s)
        if path.startswith("/set_"):
            return b"ok"
        self.ms_per_call += 100
        return _wav(self.ms_per_call)

    def tts_calls(self) -> list[dict]:
        return [params for path, params in self.calls if path == "/tts"]


def _client(tmp_path, plugin, transport=None, translator=None, **kwargs):
    tts = importlib.import_module(f"{PLUGIN}.tts")
    return tts.TtsClient(
        tmp_path,
        transport=transport or FakeTransport(),
        translator=translator or (lambda text: _identity(text)),
        **kwargs,
    )


async def _identity(text: str) -> str:
    return text


@pytest.fixture(scope="module")
def tts_module(plugin):
    return importlib.import_module(f"{PLUGIN}.tts")


@pytest.fixture(scope="module")
def translate_module(plugin):
    return importlib.import_module(f"{PLUGIN}.translate")


def test_sets_weights_before_synth(tmp_path, plugin):
    transport = FakeTransport()
    client = _client(tmp_path, plugin, transport)
    voice = asyncio.run(client.synthesize("Hello there."))
    paths = [path for path, _ in transport.calls]
    assert paths[0] == "/set_gpt_weights"
    assert paths[1] == "/set_sovits_weights"
    assert paths.count("/tts") == 1
    assert transport.calls[0][1]["weights_path"] == client.gpt_weights
    assert transport.calls[1][1]["weights_path"] == client.sovits_weights
    assert voice["durationMs"] == 300
    assert voice["sentenceStartsMs"] == [0]
    assert voice["audioUrl"] == f"/mas3d/api/voice/{voice['id']}.wav"


def test_weights_are_not_reset_per_turn(tmp_path, plugin):
    transport = FakeTransport()
    client = _client(tmp_path, plugin, transport)
    asyncio.run(client.synthesize("First."))
    asyncio.run(client.synthesize("Second."))
    assert [path for path, _ in transport.calls].count("/set_gpt_weights") == 1


def test_caches_by_hash(tmp_path, plugin):
    transport = FakeTransport()
    client = _client(tmp_path, plugin, transport)
    first = asyncio.run(client.synthesize("Hello there."))
    calls_after_first = len(transport.calls)
    second = asyncio.run(client.synthesize("Hello there."))
    assert len(transport.calls) == calls_after_first
    assert second["id"] == first["id"]
    assert second["cached"] is True
    assert first["cached"] is False


def test_split_sentences_keeps_punctuation(tts_module):
    assert tts_module.split_sentences("你好。外面很安静！真的吗？") == ["你好。", "外面很安静！", "真的吗？"]
    assert tts_module.split_sentences("Hi. Are you there?") == ["Hi.", "Are you there?"]
    assert tts_module.split_sentences("...") == []
    assert tts_module.split_sentences("no punctuation") == ["no punctuation"]


def test_merged_timeline_across_sentences(tmp_path, plugin):
    transport = FakeTransport(ms_per_call=200)
    client = _client(tmp_path, plugin, transport, translator=_multi_sentence)
    voice = asyncio.run(client.synthesize("你好。再见。"))
    # FakeTransport 每次 +100ms：第一句 300ms，第二句 400ms
    assert voice["durationMs"] == 700
    assert voice["sentenceStartsMs"] == [0, 300]
    assert voice["englishText"] == "Hello. Bye."
    assert len(transport.tts_calls()) == 2


async def _multi_sentence(text: str) -> str:
    return "Hello. Bye."


def test_single_sentence_slice(tmp_path, plugin):
    transport = FakeTransport()
    client = _client(tmp_path, plugin, transport, translator=_multi_sentence)
    voice = asyncio.run(client.synthesize("你好。再见。", sentence=1))
    assert voice["englishText"] == "Bye."
    assert voice["sentenceStartsMs"] == [0]
    assert voice["sentenceIndex"] == 1
    with pytest.raises(IndexError):
        asyncio.run(client.synthesize("你好。再见。", sentence=5))


def test_timeout_raises_and_returns_no_audio(tmp_path, plugin):
    transport = FakeTransport(delay_s=0.4)
    client = _client(tmp_path, plugin, transport, sentence_timeout_s=0.05, turn_timeout_s=0.2)
    with pytest.raises(Exception) as excinfo:
        asyncio.run(client.synthesize("Hello there."))
    assert type(excinfo.value).__name__ in {"TtsUnavailable", "TimeoutError"}
    assert list(tmp_path.glob("*.wav")) == []


def test_connection_refused_becomes_unavailable(tmp_path, plugin):
    tts = importlib.import_module(f"{PLUGIN}.tts")
    client = tts.TtsClient(tmp_path, base_url="http://127.0.0.1:9", translator=_identity)
    with pytest.raises(tts.TtsUnavailable):
        asyncio.run(client.synthesize("Hello there."))


def test_wav_path_rejects_traversal(tmp_path, plugin):
    client = _client(tmp_path, plugin)
    assert client.wav_path("../etc/passwd") is None
    assert client.wav_path("0" * 32) is None


def test_translation_is_cached_per_source_text(tmp_path, plugin):
    transport = FakeTransport()
    calls: list[str] = []

    async def translator(text: str) -> str:
        calls.append(text)
        # 故意每次措辞都不同：模拟真实翻译模型的非确定性
        return "Hello there." if len(calls) == 1 else "Hi, hello there."

    client = _client(tmp_path, plugin, transport, translator=translator)
    first = asyncio.run(client.synthesize("你好呀。"))
    second = asyncio.run(client.synthesize("你好呀。"))
    assert calls == ["你好呀。"]
    assert second["cached"] is True
    assert second["id"] == first["id"]


def test_failed_translation_skips_voice(tmp_path, plugin):
    """翻译失败时不要拿英文音色硬读中文：抛 TtsUnavailable，前端只显示字幕。"""
    tts = importlib.import_module(f"{PLUGIN}.tts")
    transport = FakeTransport()
    calls: list[str] = []

    async def translator(text: str) -> str:
        calls.append(text)
        return text  # translate 失败时原样返回中文

    client = _client(tmp_path, plugin, transport, translator=translator)
    with pytest.raises(tts.TtsUnavailable):
        asyncio.run(client.synthesize("你好呀。"))
    assert calls == ["你好呀。"]
    assert transport.tts_calls() == []
    assert list(tmp_path.glob("*.wav")) == []


def test_prunes_cache(tmp_path, plugin):
    transport = FakeTransport()
    client = _client(tmp_path, plugin, transport, cache_limit=2)
    for index in range(4):
        asyncio.run(client.synthesize(f"Sentence number {index}."))
    assert len(list(tmp_path.glob("*.wav"))) == 2


def test_translate_reads_astrbot_config(translate_module, tmp_path):
    config = tmp_path / "gsv_tts_translate.json"
    config.write_text(
        json.dumps(
            {
                "enable": True,
                "api_base": "https://example.invalid/v1",
                "api_key": "sk-test",
                "model": "glm-test",
                "temperature": 0.3,
            }
        ),
        encoding="utf-8-sig",
    )
    captured = {}

    def fake_post(url, payload, headers, timeout_s):
        captured["url"] = url
        captured["payload"] = payload
        captured["headers"] = headers
        return json.dumps({"choices": [{"message": {"content": "  Hello there.  "}}]}).encode()

    result = asyncio.run(
        translate_module.translate_zh_to_en("你好呀", config_path=config, post=fake_post)
    )
    assert result == "Hello there."
    assert captured["url"] == "https://example.invalid/v1/chat/completions"
    assert captured["payload"]["model"] == "glm-test"
    assert captured["headers"]["Authorization"] == "Bearer sk-test"


def test_translate_failure_falls_back(translate_module, tmp_path):
    config = tmp_path / "gsv_tts_translate.json"
    config.write_text(
        json.dumps({"enable": True, "api_base": "https://example.invalid/v1", "api_key": "sk-test"}),
        encoding="utf-8",
    )

    def boom(url, payload, headers, timeout_s):
        raise OSError("network down")

    assert (
        asyncio.run(translate_module.translate_zh_to_en("你好呀", config_path=config, post=boom))
        == "你好呀"
    )


def test_translate_retries_when_model_chats_back(translate_module, tmp_path):
    """实测代理会把翻译当聊天回答（返回中文感想）→ 必须重试并拒绝中文输出。"""
    config = tmp_path / "gsv_tts_translate.json"
    config.write_text(
        json.dumps({"enable": True, "api_base": "https://example.invalid/v1", "api_key": "sk-test"}),
        encoding="utf-8",
    )
    replies = [
        "今晚这个开场很美——安静的星空确实容易让人想说点平时不太说的话。",
        "What do you wanna chat about tonight? The quiet stars make people honest.",
    ]
    seen: list[str] = []

    def fake_post(url, payload, headers, timeout_s):
        seen.append(payload["messages"][0]["content"])
        content = replies[len(seen) - 1]
        return json.dumps({"choices": [{"message": {"content": content}}]}).encode()

    result = asyncio.run(translate_module.translate_zh_to_en("今天想聊点什么？", config_path=config, post=fake_post))
    assert result.startswith("What do you wanna chat about tonight?")
    assert len(seen) == 2
    assert "translation engine" in seen[0]


def test_translate_rejects_chatty_english(translate_module, tmp_path):
    """模型也可能用英文聊天作答：长度明显超出译文范围就重试。"""
    config = tmp_path / "gsv_tts_translate.json"
    config.write_text(
        json.dumps({"enable": True, "api_base": "https://example.invalid/v1", "api_key": "sk-test"}),
        encoding="utf-8",
    )
    replies = [
        "Hello! I'm a large language model trained by Z.ai, always here to help you with anything you need today.",
        "Hello.",
    ]
    seen: list[str] = []

    def fake_post(url, payload, headers, timeout_s):
        seen.append(payload["messages"][1]["content"])
        return json.dumps({"choices": [{"message": {"content": replies[len(seen) - 1]}}]}).encode()

    assert asyncio.run(translate_module.translate_zh_to_en("你好。", config_path=config, post=fake_post)) == "Hello."
    assert len(seen) == 2
    assert seen[0].endswith("你好。")
    assert "Translate the following Chinese" in seen[0]


def test_translate_gives_up_after_three_chatty_replies(translate_module, tmp_path):
    config = tmp_path / "gsv_tts_translate.json"
    config.write_text(
        json.dumps({"enable": True, "api_base": "https://example.invalid/v1", "api_key": "sk-test"}),
        encoding="utf-8",
    )
    calls: list[int] = []

    def fake_post(url, payload, headers, timeout_s):
        calls.append(1)
        return json.dumps({"choices": [{"message": {"content": "我还是想说中文。"}}]}).encode()

    assert asyncio.run(translate_module.translate_zh_to_en("你好", config_path=config, post=fake_post)) == "你好"
    assert len(calls) == 3


def test_translate_skips_english_and_disabled(translate_module, tmp_path):
    config = tmp_path / "gsv_tts_translate.json"
    config.write_text(json.dumps({"enable": True, "api_key": "sk-test"}), encoding="utf-8")
    called = []

    def fake_post(url, payload, headers, timeout_s):
        called.append(url)
        return json.dumps({"choices": [{"message": {"content": "x"}}]}).encode()

    assert asyncio.run(translate_module.translate_zh_to_en("hello", config_path=config, post=fake_post)) == "hello"
    assert called == []

    disabled = tmp_path / "off.json"
    disabled.write_text(json.dumps({"enable": False, "api_key": "sk-test"}), encoding="utf-8")
    assert asyncio.run(translate_module.translate_zh_to_en("你好", config_path=disabled, post=fake_post)) == "你好"
    assert called == []


def test_sanitize_strips_emoji_and_collapses_whitespace(tts_module):
    assert tts_module.sanitize_for_tts("好呀🐷💤！\n\n 走\n") == "好呀！ 走"
    assert tts_module.sanitize_for_tts("🐷🐷") == ""
    assert tts_module.sanitize_for_tts("hello  world") == "hello world"


async def _emoji_translator(text: str) -> str:
    return "Okay 🐷 let us go!"


def test_synthesize_drops_characters_the_api_cannot_take(tmp_path, plugin):
    transport = FakeTransport()
    client = _client(tmp_path, plugin, transport, translator=_emoji_translator)

    voice = asyncio.run(client.synthesize("好呀🐷，我们走！"))

    assert voice["durationMs"] > 0
    sent = transport.tts_calls()[0]["text"]
    assert "🐷" not in sent
    assert sent == "Okay let us go!"
