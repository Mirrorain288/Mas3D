"""GPT-SoVITS 语音合成客户端（本地 api_v2）。

为什么不复用 AstrBot 的 TTS provider：
- 需要把**真实送合成的文本**（翻译后的英文）交回前端做 viseme 字幕对齐；
- 需要逐句时间线（sentenceStartsMs）驱动口型；
- 需要显式锁定权重，避免 AstrBot 面板里换模型后游戏音色静默变化。

权重、参考音频、提示词都抄自 ~/.astrbot/data/cmd_config.json 的 gsv_tts-zh provider，
保证游戏与 QQ 使用同一条声线。
"""

from __future__ import annotations

import asyncio
import hashlib
import http.client
import json
import time
from pathlib import Path
from urllib.parse import urlencode, urlparse

from .translate import CJK_RE, translate_zh_to_en

GSV_BASE_URL = "http://127.0.0.1:9880"
GPT_WEIGHTS = "D:/GPT-SoVITS-v2pro-20250604/GPT_weights_v2ProPlus/Moniar_v2_1-e15.ckpt"
SOVITS_WEIGHTS = "D:/GPT-SoVITS-v2pro-20250604/SoVITS_weights_v2ProPlus/Moniar_v2_1_e12_s984.pth"
REF_AUDIO_PATH = (
    "D:/merged_dataset/output/ref_main/"
    "emo_emo_ax_48k_stereo.wav.reformatted_vocals.flac_0000357440_0000616960.wav"
)
REF_TEXT = (
    "I've been going since 2016 and I never thought this day would come. "
    "It'll be from July 4th to July 7th of 2024."
)
AUX_REF_AUDIO_PATH = (
    "D:/MonikaVoice/reference/"
    "emo3_emo_sj_48k_stereo.wav.reformatted_vocals.flac_0002713280_0002900800.wav"
)
PROMPT_LANG = "en"

SAMPLE_RATE = 32000
BYTES_PER_SAMPLE = 2
SENTENCE_TIMEOUT_S = 30.0
TURN_TIMEOUT_S = 60.0
CACHE_LIMIT = 500
MAX_TEXT_CHARS = 600
AUDIO_URL_TEMPLATE = "/mas3d/api/voice/{voice_id}.wav"
TERMINATORS = "。！？.!?"

# 与 AstrBot gsv_default_parms 对齐；streaming_mode=false 因为我们一次要完整 WAV 字节。
# parallel_infer=false：本机 GPU 还要给 AstrBot 的 QQ 语音用，宁可慢一点也不要抢显存。
BASE_PARAMS: dict[str, str] = {
    "text_lang": "en",
    "prompt_lang": PROMPT_LANG,
    "ref_audio_path": REF_AUDIO_PATH,
    "prompt_text": REF_TEXT,
    "top_k": "10",
    "top_p": "1",
    "temperature": "1",
    "text_split_method": "cut5",
    "repetition_penalty": "1.35",
    "speed_factor": "1.0",
    "streaming_mode": "false",
    "media_type": "wav",
    "parallel_infer": "false",
}


class TtsUnavailable(RuntimeError):
    """合成服务不可用（没启动、超时、返回非 200）。"""


TTS_ENCODING = "gbk"
"""本地 api_v2 跑在 Windows 上，非 GBK 字符会让它直接 400 掉整句。"""


def sanitize_for_tts(text: str) -> str:
    """剔除合成链路吃不下的字符（emoji、生僻符号），并把空白压成单空格。"""
    kept: list[str] = []
    for ch in text:
        if ch in "\r\n\t":
            kept.append(" ")
            continue
        try:
            ch.encode(TTS_ENCODING)
        except UnicodeEncodeError:
            continue
        kept.append(ch)
    return " ".join("".join(kept).split())


def split_sentences(text: str) -> list[str]:
    """按中英文句末标点切句并保留标点；纯标点/空片段丢弃。"""
    sentences: list[str] = []
    buffer: list[str] = []
    for char in text:
        buffer.append(char)
        if char in TERMINATORS:
            sentence = "".join(buffer).strip()
            if any(c.isalnum() or "\u4e00" <= c <= "\u9fff" for c in sentence):
                sentences.append(sentence)
            buffer = []
    tail = "".join(buffer).strip()
    if tail and any(c.isalnum() or "\u4e00" <= c <= "\u9fff" for c in tail):
        sentences.append(tail)
    return sentences


def _wav_chunks(data: bytes) -> list[tuple[bytes, bytes]]:
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise TtsUnavailable("合成结果不是 RIFF/WAVE")
    chunks: list[tuple[bytes, bytes]] = []
    pos = 12
    while pos + 8 <= len(data):
        chunk_id = data[pos : pos + 4]
        size = int.from_bytes(data[pos + 4 : pos + 8], "little")
        chunks.append((chunk_id, data[pos + 8 : pos + 8 + size]))
        pos += 8 + size + (size % 2)
    return chunks


def wav_duration_ms(data: bytes) -> int:
    for chunk_id, payload in _wav_chunks(data):
        if chunk_id == b"data":
            return round(len(payload) / (SAMPLE_RATE * BYTES_PER_SAMPLE) * 1000)
    raise TtsUnavailable("WAV 里没有 data 段")


def merge_wav(parts: list[bytes]) -> bytes:
    """把多句 WAV 拼成一条：格式取第一段，data 段顺序拼接。"""
    if len(parts) == 1:
        return parts[0]
    fmt = b""
    bodies: list[bytes] = []
    for part in parts:
        chunks = _wav_chunks(part)
        fmt = next((payload for cid, payload in chunks if cid == b"fmt "), fmt)
        bodies.append(b"".join(payload for cid, payload in chunks if cid == b"data"))
    if not fmt:
        raise TtsUnavailable("WAV 里没有 fmt 段")
    body = b"".join(bodies)
    fmt_chunk = b"fmt " + len(fmt).to_bytes(4, "little") + fmt
    data_chunk = b"data" + len(body).to_bytes(4, "little") + body
    riff_size = 4 + len(fmt_chunk) + len(data_chunk)
    return b"RIFF" + riff_size.to_bytes(4, "little") + b"WAVE" + fmt_chunk + data_chunk


def _http_get(host: str, port: int, path: str, params: dict[str, str], timeout_s: float) -> bytes:
    query = urlencode(list(params.items()), doseq=True)
    target = f"{path}?{query}" if query else path
    conn = http.client.HTTPConnection(host, port, timeout=timeout_s)
    try:
        conn.request("GET", target)
        response = conn.getresponse()
        body = response.read()
        if response.status != 200:
            raise TtsUnavailable(f"GPT-SoVITS 返回 {response.status}: {body[:200]!r}")
        return body
    except (TimeoutError, OSError) as exc:
        raise TtsUnavailable(f"GPT-SoVITS 连接失败：{exc}") from exc
    finally:
        conn.close()


def parse_base_url(base_url: str) -> tuple[str, int]:
    parsed = urlparse(base_url)
    return parsed.hostname or "127.0.0.1", parsed.port or 80


class TtsClient:
    """逐句合成 + 内容哈希缓存 + 权重显式锁定。"""

    def __init__(
        self,
        cache_dir: Path | str,
        *,
        base_url: str = GSV_BASE_URL,
        gpt_weights: str = GPT_WEIGHTS,
        sovits_weights: str = SOVITS_WEIGHTS,
        translate_config_path: Path | str | None = None,
        sentence_timeout_s: float = SENTENCE_TIMEOUT_S,
        turn_timeout_s: float = TURN_TIMEOUT_S,
        cache_limit: int = CACHE_LIMIT,
        transport=None,
        translator=None,
        clock=time.monotonic,
    ) -> None:
        self.cache_dir = Path(cache_dir)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self.base_url = base_url.rstrip("/")
        self.host, self.port = parse_base_url(self.base_url)
        self.gpt_weights = gpt_weights
        self.sovits_weights = sovits_weights
        self.translate_config_path = translate_config_path
        self.sentence_timeout_s = sentence_timeout_s
        self.turn_timeout_s = turn_timeout_s
        self.cache_limit = cache_limit
        self._transport = transport
        self._translator = translator
        self._clock = clock
        self._semaphore = asyncio.Semaphore(1)
        self._weights_lock = asyncio.Lock()
        self._weights_ready = False
        self.last_weights_at = 0.0

    # --- 传输层 ---------------------------------------------------------------
    def _get(self, path: str, params: dict[str, str], timeout_s: float) -> bytes:
        if self._transport is not None:
            return self._transport(path, params, timeout_s)
        return _http_get(self.host, self.port, path, params, timeout_s)

    async def _ensure_weights(self) -> None:
        if self._weights_ready:
            return
        async with self._weights_lock:
            if self._weights_ready:
                return
            if self.gpt_weights:
                await asyncio.wait_for(
                    asyncio.to_thread(self._get, "/set_gpt_weights", {"weights_path": self.gpt_weights}, self.sentence_timeout_s),
                    self.sentence_timeout_s,
                )
            if self.sovits_weights:
                await asyncio.wait_for(
                    asyncio.to_thread(self._get, "/set_sovits_weights", {"weights_path": self.sovits_weights}, self.sentence_timeout_s),
                    self.sentence_timeout_s,
                )
            self._weights_ready = True
            self.last_weights_at = self._clock()

    # --- 缓存 -----------------------------------------------------------------
    def _params(self, sentence: str) -> dict[str, str]:
        params = dict(BASE_PARAMS)
        params["text"] = sentence
        return params

    def _cache_key(self, sentences: list[str], params_list: list[dict[str, str]]) -> str:
        material = json.dumps(
            {
                "text": "|".join(sentences),
                "gpt": self.gpt_weights,
                "sovits": self.sovits_weights,
                "params": params_list,
            },
            ensure_ascii=False,
            sort_keys=True,
        )
        return hashlib.sha256(material.encode("utf-8")).hexdigest()[:32]

    def _translation_key(self, text: str) -> str:
        """译文缓存键：同一句中文不必每回合都让翻译模型重写一遍。

        翻译是非确定性的，重译会让英文断句、语气词变化，进而让音频缓存永远命中不了
        （实测同一句中文两次合成的英文措辞不同）。把译文按原文钉住，既省一次网络
        往返，也让单句切片与整轮合成的英文完全一致。
        """
        material = json.dumps(
            {"text": text, "config": str(self.translate_config_path or ""), "model": "translate"},
            ensure_ascii=False,
            sort_keys=True,
        )
        return "tr-" + hashlib.sha256(material.encode("utf-8")).hexdigest()[:32]

    def _read_translation(self, text: str) -> str | None:
        path = self.cache_dir / f"{self._translation_key(text)}.en"
        try:
            cached = path.read_text(encoding="utf-8").strip()
        except OSError:
            return None
        return cached or None

    def _write_translation(self, text: str, english: str) -> None:
        path = self.cache_dir / f"{self._translation_key(text)}.en"
        try:
            path.write_text(english, encoding="utf-8")
        except OSError:
            return
        self._prune()

    def _paths(self, key: str) -> tuple[Path, Path]:
        return self.cache_dir / f"{key}.wav", self.cache_dir / f"{key}.json"

    def _read_cache(self, key: str) -> dict | None:
        wav_path, meta_path = self._paths(key)
        if not (wav_path.exists() and meta_path.exists()):
            return None
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        if not isinstance(meta, dict):
            return None
        meta["cached"] = True
        return self._voice(meta)

    def _write_cache(self, key: str, wav: bytes, meta: dict) -> None:
        wav_path, meta_path = self._paths(key)
        wav_path.write_bytes(wav)
        meta_path.write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")
        self._prune()

    def _prune(self) -> None:
        wavs = sorted(self.cache_dir.glob("*.wav"), key=lambda p: p.stat().st_mtime, reverse=True)
        for stale in wavs[self.cache_limit :]:
            for path in (stale, stale.with_suffix(".json")):
                try:
                    path.unlink()
                except OSError:
                    pass
        translations = sorted(self.cache_dir.glob("*.en"), key=lambda p: p.stat().st_mtime, reverse=True)
        for stale in translations[max(1, self.cache_limit) :]:
            try:
                stale.unlink()
            except OSError:
                pass

    @staticmethod
    def _voice(meta: dict) -> dict:
        voice_id = str(meta["id"])
        return {
            "id": voice_id,
            "audioUrl": AUDIO_URL_TEMPLATE.format(voice_id=voice_id),
            "durationMs": int(meta["durationMs"]),
            "sentenceStartsMs": [int(v) for v in meta.get("sentenceStartsMs", [0])],
            "englishText": str(meta.get("englishText", "")),
            "sentenceIndex": meta.get("sentenceIndex"),
            "cached": bool(meta.get("cached", False)),
        }

    # --- 合成 -----------------------------------------------------------------
    async def synthesize(self, text: str, sentence: int | None = None) -> dict:
        cleaned = sanitize_for_tts(text or "")
        if not cleaned:
            raise TtsUnavailable("台词里没有可合成的字符")
        cleaned = cleaned[:MAX_TEXT_CHARS]

        english = self._read_translation(cleaned)
        if english is None:
            if self._translator is not None:
                translated = await self._translator(cleaned)
            else:
                translated = await translate_zh_to_en(cleaned, config_path=self.translate_config_path)
            english = sanitize_for_tts(translated or cleaned)
            # 只缓存真的翻译过的结果：翻译失败会原样返回中文，缓存住就再也翻不回来了。
            if english and english != cleaned:
                self._write_translation(cleaned, english)

        if CJK_RE.search(english):
            # 翻译没成功：英文音色硬读中文只会得到含糊音节，宁可这轮没有声音（只显示字幕）。
            raise TtsUnavailable("翻译不可用，跳过语音")
        sentences = split_sentences(english)
        if not sentences:
            raise ValueError("没有可合成的句子")
        if sentence is not None and not (0 <= sentence < len(sentences)):
            raise IndexError(f"句子下标越界：{sentence} / {len(sentences)}")
        targets = [sentences[sentence]] if sentence is not None else sentences

        params_list = [self._params(target) for target in targets]
        key = self._cache_key(targets, params_list)
        cached = self._read_cache(key)
        if cached is not None:
            return cached

        async with self._semaphore:
            cached = self._read_cache(key)
            if cached is not None:
                return cached
            try:
                async with asyncio.timeout(self.turn_timeout_s):
                    await self._ensure_weights()
                    parts: list[bytes] = []
                    for target, params in zip(targets, params_list):
                        parts.append(
                            await asyncio.wait_for(
                                asyncio.to_thread(self._get, "/tts", params, self.sentence_timeout_s),
                                self.sentence_timeout_s,
                            )
                        )
            except (TimeoutError, asyncio.TimeoutError) as exc:
                raise TtsUnavailable(f"合成超时（{self.turn_timeout_s:.0f}s 上限）") from exc

            durations = [wav_duration_ms(part) for part in parts]
            starts: list[int] = []
            elapsed = 0
            for duration in durations:
                starts.append(elapsed)
                elapsed += duration
            meta = {
                "id": key,
                "durationMs": elapsed,
                "sentenceStartsMs": starts,
                "englishText": " ".join(targets),
                "sentenceIndex": sentence,
                "weights": {"gpt": Path(self.gpt_weights).name, "sovits": Path(self.sovits_weights).name},
                "createdAt": int(time.time()),
            }
            self._write_cache(key, merge_wav(parts), meta)
            return self._voice(meta)

    def wav_path(self, voice_id: str) -> Path | None:
        """给下载端点用：只认 32 位十六进制 id，避免路径穿越。"""
        if len(voice_id) != 32 or any(c not in "0123456789abcdef" for c in voice_id):
            return None
        path = self.cache_dir / f"{voice_id}.wav"
        return path if path.exists() else None
