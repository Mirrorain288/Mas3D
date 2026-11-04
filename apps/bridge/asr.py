"""本地 Whisper 语音识别客户端。

为什么用 openai-whisper 而不是 faster-whisper：AstrBot 的 Python 环境已经装了
openai-whisper + torch(cu128)，small/tiny 权重也已缓存；faster-whisper 需要额外的
ctranslate2，为一个端点不值得。

四条硬约束：
- 模块导入阶段不 import whisper / torch：测试与 AstrBot 面板启动都不该被 CUDA 拖住，
  模型只在第一次 transcribe 时按需加载（loader 里延迟 import）。
- 音频只收 RIFF/WAVE + PCM + 单声道 + 16-bit，声明采样率限定在
  [MIN_RATE, MAX_RATE]，30 秒上限；拒绝信息直接给前端显示。
- whisper 的 ndarray 入口默认输入已是 16 kHz（whisper/audio.py 不再按采样率换算），
  所以 transcribe 里先把样本线性重采样到 TARGET_RATE 再推理，并按重采样后的长度
  重新查一次 30 秒上限。
- 共享锁归 worker 任务所有：调用方被取消也不提前放锁（否则 GPU 上会并行两份推理）；
  等锁有自己的 LOCK_WAIT_S 预算，超时抛 AsrBusy，不吃推理那 45 s。
"""

from __future__ import annotations

import asyncio
import logging
import struct
from collections.abc import Callable
from typing import Any

import numpy as np

MAX_WAV_BYTES = 8 * 1024 * 1024
MAX_SECONDS = 30.0
TARGET_RATE = 16000  # whisper 对 ndarray 输入的隐含采样率
MIN_RATE = 8000
MAX_RATE = 192000
ASR_LANGUAGE = "zh"
ASR_PROMPT = "以下是一段普通话简体中文字幕。"
# 等锁预算单独算：TTS 一轮翻译 + HTTP + 写缓存最长 70 s，ASR 不该陪着一起等。
LOCK_WAIT_S = 15.0

logger = logging.getLogger(__name__)


class AsrUnavailable(Exception):
    """识别模型不可用（主模型与兜底模型都加载失败）。"""


class AsrBusy(Exception):
    """等锁超过 LOCK_WAIT_S：GPU 正被另一段语音占着，本次识别根本没开始。"""


_FMT_CHUNK = b"fmt "
_DATA_CHUNK = b"data"
_PCM_FORMAT = 1
_QUOTE_PAIRS = (('"', '"'), ("“", "”"), ("‘", "’"))


def decode_wav(data: bytes) -> tuple[np.ndarray, int]:
    """把 16-bit PCM 单声道 WAV 解成 [-1, 1] 的 float32 样本，返回文件原生采样率。

    这里不做重采样：时长按声明采样率校验，重采样与二次校验都在 transcribe 里做。
    """
    if len(data) < 44 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError("不是 RIFF/WAVE 数据")

    fmt: bytes | None = None
    body: bytes | None = None
    pos = 12
    while pos + 8 <= len(data):
        chunk_id = data[pos : pos + 4]
        size = int.from_bytes(data[pos + 4 : pos + 8], "little")
        end = pos + 8 + size
        if end > len(data):
            raise ValueError("只支持 16-bit PCM")
        payload = data[pos + 8 : end]
        if chunk_id == _FMT_CHUNK:
            fmt = payload
        elif chunk_id == _DATA_CHUNK:
            body = payload
        if fmt is not None and body is not None:
            break
        pos = end + (size % 2)

    if fmt is None or len(fmt) < 16:
        raise ValueError("只支持 16-bit PCM")
    audio_format, channels, rate, _byte_rate, _block_align, bits = struct.unpack("<HHIIHH", fmt[:16])
    if audio_format != _PCM_FORMAT or bits != 16 or rate <= 0:
        raise ValueError("只支持 16-bit PCM")
    if not MIN_RATE <= rate <= MAX_RATE:
        # 放在时长检查之前：声明 1 MHz 时 frames / rate 会把几分钟音频算成不到 1 秒。
        raise ValueError("采样率超出范围")
    if channels != 1:
        raise ValueError("只支持单声道")
    if body is None:
        raise ValueError("只支持 16-bit PCM")
    usable = body[: len(body) - (len(body) % 2)]
    frames = len(usable) // 2
    if frames == 0:
        raise ValueError("空音频")
    if frames / rate > MAX_SECONDS:
        raise ValueError("音频超过 30 秒")

    samples = np.frombuffer(usable, dtype="<i2").astype(np.float32) / 32768.0
    return samples, rate


def _resample(samples: np.ndarray, rate: int) -> np.ndarray:
    """线性插值重采样到 TARGET_RATE（只用 numpy，不引 scipy / torch）。"""
    if rate == TARGET_RATE:
        return samples
    target_len = max(1, int(round(len(samples) * TARGET_RATE / rate)))
    positions = np.arange(target_len, dtype=np.float64) * (rate / TARGET_RATE)
    source = np.arange(len(samples), dtype=np.float64)
    return np.interp(positions, source, samples).astype(np.float32)


def normalize_text(text: str) -> str:
    """去首尾空白、把连续空白压成一个空格、剥掉最外层一对引号。"""
    cleaned = " ".join((text or "").split())
    for left, right in _QUOTE_PAIRS:
        if len(cleaned) >= 2 and cleaned.startswith(left) and cleaned.endswith(right):
            cleaned = cleaned[1:-1].strip()
            break
    return cleaned

class AsrClient:
    """常驻进程的 Whisper 客户端：首次 transcribe 才加载模型。"""

    def __init__(
        self,
        model_name: str = "small",
        fallback: str = "tiny",
        device: str | None = None,
        lock: asyncio.Lock | None = None,
        loader: Callable[[str], Any] | None = None,
    ) -> None:
        self.model_name = model_name
        self.fallback = fallback
        self.device = device
        self._lock = lock
        self._loader = loader
        self._primary = model_name
        self._model: Any | None = None
        self._fp16 = False
        # asyncio 只持任务弱引用，不自己留一份，取消后就再也收不到异常了。
        self._workers: set[asyncio.Task] = set()

    def _load(self, name: str) -> Any:
        if self._loader is not None:
            return self._loader(name)
        import whisper  # 延迟导入：模块导入阶段不碰 torch / CUDA

        return whisper.load_model(name, device=self.device)

    def _ensure_model(self) -> Any:
        if self._model is not None:
            return self._model
        failures: list[str] = []
        for name in (self._primary, self.fallback):
            if not name:
                continue
            try:
                model = self._load(name)
            except Exception as exc:  # noqa: BLE001 - 主模型任何加载失败都该退到兜底
                logger.warning("ASR 模型 %s 加载失败：%s", name, exc)
                failures.append(f"{name}: {exc}")
                continue
            self._model = model
            self.model_name = name
            # 不 import torch：fp16 只能跟已加载模型的实际 device 走。
            self._fp16 = str(getattr(model, "device", "cpu")).startswith("cuda")
            return model
        raise AsrUnavailable(
            f"ASR 模型不可用（{self._primary} 与 {self.fallback} 都加载失败）：" + "；".join(failures)
        )

    async def transcribe(self, wav: bytes) -> dict:
        # 锁的唯一目的是串行化模型访问；decode 很便宜，索性整段都包进去。
        if self._lock is None:
            return await self._run(wav)
        # 锁归 worker 任务所有，不归调用方：调用方被取消（HTTP 超时、客户端断开）时
        # async with 会在 __aexit__ 里立刻放锁，而 to_thread 里的 GPU 推理还在跑，
        # 下一段语音就能把第二份推理塞进同一块显卡。shield 只挡下这一层的取消。
        # 代价：还在排队等锁的请求被取消，worker 拿到锁后仍会把这一轮推理跑完
        # （实测模型调用次数——取消时 0 次，持有者放锁后 1 次），被取消的回合照样占着 GPU。
        work = asyncio.create_task(self._locked_run(wav), name="mas3d-asr-transcribe")
        self._workers.add(work)
        work.add_done_callback(self._forget_worker)
        return await asyncio.shield(work)

    def _forget_worker(self, work: asyncio.Task) -> None:
        """worker 的结局必须有人收：调用方早走了，异常只能在这里取出来。"""
        self._workers.discard(work)
        if work.cancelled():
            return
        try:
            work.exception()
        except Exception:  # noqa: BLE001 - 只是取出异常，避免 "never retrieved" 噪音
            pass

    async def _locked_run(self, wav: bytes) -> dict:
        try:
            await asyncio.wait_for(self._lock.acquire(), LOCK_WAIT_S)
        except asyncio.TimeoutError:
            raise AsrBusy("语音识别忙") from None
        try:
            return await self._run(wav)
        finally:
            self._lock.release()

    async def _run(self, wav: bytes) -> dict:
        samples, rate = decode_wav(wav)
        resampled = _resample(samples, rate)
        # decode 的时长检查用的是声明采样率；这里按重采样后的真实长度再查一次。
        if len(resampled) / TARGET_RATE > MAX_SECONDS:
            raise ValueError("音频超过 30 秒")
        duration_ms = int(round(len(resampled) * 1000 / TARGET_RATE))
        model = await asyncio.to_thread(self._ensure_model)
        result = await asyncio.to_thread(
            model.transcribe,
            resampled,
            language=ASR_LANGUAGE,
            initial_prompt=ASR_PROMPT,
            fp16=self._fp16,
            verbose=False,
        )
        return {
            "text": normalize_text(str((result or {}).get("text") or "")),
            "language": ASR_LANGUAGE,
            "durationMs": duration_ms,
            "model": self.model_name,
        }
