"""ASR 客户端：WAV 解码、文本归一化、模型降级、fp16 与串行锁。

所有用例都注入假的 loader / 假模型：绝不加载真 whisper、不碰 GPU、不出网。
"""

from __future__ import annotations

import asyncio
import importlib
import io
import struct
import sys
import threading
import time
import types
import wave

import numpy as np
import pytest

PLUGIN = "astrbot_plugin_mas3d_game"


@pytest.fixture(scope="session")
def asr_mod(plugin):
    from astrbot_plugin_mas3d_game import asr as mod

    return mod


# --- WAV 造数据 ---------------------------------------------------------------


def _pcm(samples) -> bytes:
    return struct.pack(f"<{len(samples)}h", *samples)


def _wav_bytes(frames: bytes, *, channels: int = 1, rate: int = 16000, sampwidth: int = 2) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(sampwidth)
        handle.setframerate(rate)
        handle.writeframes(frames)
    return buf.getvalue()


def _wav(samples, *, rate: int = 16000) -> bytes:
    return _wav_bytes(_pcm(samples), rate=rate)


WAV_HALF_SECOND = _wav([0] * 8000)


# --- 假模型 -------------------------------------------------------------------


class RecordingModel:
    """记录 transcribe 收到的样本与 kwargs；device 决定 fp16。"""

    def __init__(self, *, device: str = "cpu", text: str = " 你好，世界。 "):
        self.device = device
        self.text = text
        self.calls: list[dict] = []

    def transcribe(self, samples, **kwargs):
        self.calls.append({"samples": samples, "kwargs": kwargs})
        return {"text": self.text, "language": "zh"}


class ClockedModel(RecordingModel):
    """慢模型：统计同时进入 transcribe 的峰值并发。"""

    def __init__(self, active: dict, delay_s: float = 0.15):
        super().__init__(text="锁内")
        self.active = active
        self.delay_s = delay_s

    def transcribe(self, samples, **kwargs):
        self.active["now"] += 1
        self.active["peak"] = max(self.active["peak"], self.active["now"])
        try:
            time.sleep(self.delay_s)
            return {"text": self.text}
        finally:
            self.active["now"] -= 1


class BlockingModel(RecordingModel):
    """transcribe 卡在 threading.Event 上，模拟「调用方走了、GPU 线程还在跑」。"""

    def __init__(self) -> None:
        super().__init__(text="跑完了")
        self.started = threading.Event()
        self.release = threading.Event()

    def transcribe(self, samples, **kwargs):
        self.calls.append({"samples": samples, "kwargs": kwargs})
        self.started.set()
        self.release.wait(5.0)
        return {"text": self.text}


# --- 常量与导入 ---------------------------------------------------------------


def test_constants_match_brief(asr_mod):
    assert asr_mod.MAX_WAV_BYTES == 8 * 1024 * 1024
    assert asr_mod.MAX_SECONDS == 30.0
    assert asr_mod.ASR_LANGUAGE == "zh"
    assert asr_mod.ASR_PROMPT == "以下是一段普通话简体中文字幕。"


def test_rate_constants_pin_resample_contract(asr_mod):
    # whisper 的 ndarray 入口默认 16 kHz；声明采样率另有可用区间。
    assert asr_mod.TARGET_RATE == 16000
    assert asr_mod.MIN_RATE == 8000
    assert asr_mod.MAX_RATE == 192000


def test_module_import_does_not_pull_whisper_or_torch(asr_mod):
    # 模块导入阶段不能加载 whisper / torch，否则测试与面板启动都会拖上 CUDA。
    assert "whisper" not in sys.modules
    assert "torch" not in sys.modules


# --- decode_wav：拒绝 ---------------------------------------------------------


def test_rejects_non_riff_data(asr_mod):
    for bad in (b"", b"RIFF", b"\x00" * 60):
        with pytest.raises(ValueError, match="不是 RIFF/WAVE 数据"):
            asr_mod.decode_wav(bad)


def test_rejects_truncated_chunk(asr_mod):
    truncated = _wav([0, 1000, -1000])[:-4]
    with pytest.raises(ValueError, match="只支持 16-bit PCM"):
        asr_mod.decode_wav(truncated)


def test_rejects_8bit_pcm(asr_mod):
    data = _wav_bytes(b"\x80" * 200, sampwidth=1)
    with pytest.raises(ValueError, match="只支持 16-bit PCM"):
        asr_mod.decode_wav(data)


def test_rejects_stereo(asr_mod):
    data = _wav_bytes(_pcm([0, 0, 1000, 1000]), channels=2)
    with pytest.raises(ValueError, match="只支持单声道"):
        asr_mod.decode_wav(data)


def test_rejects_empty_audio(asr_mod):
    with pytest.raises(ValueError, match="空音频"):
        asr_mod.decode_wav(_wav_bytes(b""))


def test_rejects_audio_longer_than_30_seconds(asr_mod):
    data = _wav_bytes(b"\x00\x00" * 244_000, rate=8000)  # 30.5 s
    with pytest.raises(ValueError, match="音频超过 30 秒"):
        asr_mod.decode_wav(data)


def test_rejects_declared_rate_outside_supported_range(asr_mod):
    # 声明 1 MHz：frames / rate 会把「几分钟音频」算成不到 1 秒，必须先按范围拒绝。
    with pytest.raises(ValueError, match="采样率超出范围"):
        asr_mod.decode_wav(_wav_bytes(b"\x00\x00" * 4, rate=1_000_000))
    with pytest.raises(ValueError, match="采样率超出范围"):
        asr_mod.decode_wav(_wav_bytes(b"\x00\x00" * 4, rate=4000))
    # 同时越界且超时长（50 s @ 4000 Hz）：范围检查必须排在时长检查之前。
    with pytest.raises(ValueError, match="采样率超出范围"):
        asr_mod.decode_wav(_wav_bytes(b"\x00\x00" * 200_000, rate=4000))


# --- decode_wav：成功 ---------------------------------------------------------


def test_decodes_pcm16_mono_to_float32(asr_mod):
    data = _wav([0, 16384, -16384, 32767, -32768])
    samples, rate = asr_mod.decode_wav(data)
    assert rate == 16000
    assert samples.dtype == np.float32
    assert samples.shape == (5,)
    np.testing.assert_allclose(
        samples,
        np.array([0.0, 0.5, -0.5, 32767 / 32768, -1.0], dtype=np.float32),
        atol=1e-6,
    )


# --- normalize_text -----------------------------------------------------------


def test_normalize_text_collapses_whitespace_and_unwraps_quotes(asr_mod):
    assert asr_mod.normalize_text("  你好   世界  ") == "你好 世界"
    assert asr_mod.normalize_text("\n 第一行\t第二行 \r\n") == "第一行 第二行"
    assert asr_mod.normalize_text('"你好，世界。"') == "你好，世界。"
    assert asr_mod.normalize_text("“你好，世界。”") == "你好，世界。"
    assert asr_mod.normalize_text("‘你好’") == "你好"
    assert asr_mod.normalize_text("  “ 你好 ”  ") == "你好"
    assert asr_mod.normalize_text("") == ""
    assert asr_mod.normalize_text("   ") == ""
    # 只去掉最外层一对引号，内层引号保留
    assert asr_mod.normalize_text("“他说‘好’”") == "他说‘好’"


# --- AsrClient.transcribe -----------------------------------------------------


def test_transcribe_returns_text_duration_and_model(asr_mod):
    model = RecordingModel(text="  你好，   世界。 ")
    client = asr_mod.AsrClient(loader=lambda name: model)
    result = asyncio.run(client.transcribe(WAV_HALF_SECOND))
    assert result == {"text": "你好， 世界。", "language": "zh", "durationMs": 500, "model": "small"}


def test_transcribe_passes_fixed_prompt_and_language(asr_mod):
    model = RecordingModel()
    client = asr_mod.AsrClient(loader=lambda name: model)
    asyncio.run(client.transcribe(WAV_HALF_SECOND))
    kwargs = model.calls[0]["kwargs"]
    assert kwargs["language"] == asr_mod.ASR_LANGUAGE
    assert kwargs["initial_prompt"] == asr_mod.ASR_PROMPT
    assert kwargs["verbose"] is False
    assert np.allclose(model.calls[0]["samples"], np.zeros(8000, dtype=np.float32))


def test_enables_fp16_on_cuda(asr_mod):
    model = RecordingModel(device="cuda:0")
    asyncio.run(asr_mod.AsrClient(loader=lambda name: model).transcribe(WAV_HALF_SECOND))
    assert model.calls[0]["kwargs"]["fp16"] is True


def test_disables_fp16_on_cpu(asr_mod):
    model = RecordingModel(device="cpu")
    asyncio.run(asr_mod.AsrClient(loader=lambda name: model).transcribe(WAV_HALF_SECOND))
    assert model.calls[0]["kwargs"]["fp16"] is False


def test_duration_ms_comes_from_samples(asr_mod):
    model = RecordingModel()
    client = asr_mod.AsrClient(loader=lambda name: model)
    result = asyncio.run(client.transcribe(_wav([0] * 1200, rate=8000)))  # 150 ms
    assert result["durationMs"] == 150


def test_transcribe_resamples_44k1_to_16k_and_reports_true_duration(asr_mod):
    # 44.1 kHz 直接喂 whisper 会被当成 16 kHz 解读（快 2.75 倍）；先重采样再推理。
    model = RecordingModel()
    client = asr_mod.AsrClient(loader=lambda name: model)
    result = asyncio.run(client.transcribe(_wav([0] * 44100, rate=44100)))  # 1.0 s @ 44.1 kHz

    received = model.calls[0]["samples"]
    assert received.dtype == np.float32
    assert abs(len(received) - 16000) <= 1
    assert result["durationMs"] == 1000


def test_transcribe_upsamples_8khz_two_seconds(asr_mod):
    # 8 kHz 仍在支持范围内：重采样到 16 kHz 后时长仍按真实音频报告。
    model = RecordingModel()
    client = asr_mod.AsrClient(loader=lambda name: model)
    result = asyncio.run(client.transcribe(_wav([1000] * 16000, rate=8000)))  # 2.0 s @ 8 kHz

    received = model.calls[0]["samples"]
    assert abs(len(received) - 32000) <= 2
    assert result["durationMs"] == 2000


def test_post_resample_guard_rejects_over_30s_that_declared_rate_cannot_catch(asr_mod, monkeypatch):
    # 只由「重采样后时长」守卫拦得住的用例：声明 16000 Hz 的 30 s + 1 帧样本。
    # decode_wav 自己会按声明采样率查时长，故 monkeypatch 它返回超长样本，
    # 证明 transcribe 在重采样后又独立查了一次，而不是信任 decode 的结论。
    too_long = np.zeros(int(asr_mod.MAX_SECONDS * asr_mod.TARGET_RATE) + 1, np.float32)
    monkeypatch.setattr(asr_mod, "decode_wav", lambda _data: (too_long, asr_mod.TARGET_RATE))

    model = RecordingModel()
    client = asr_mod.AsrClient(loader=lambda name: model)
    with pytest.raises(ValueError, match="音频超过 30 秒"):
        asyncio.run(client.transcribe(b"ignored"))
    assert model.calls == []


def test_silence_returns_empty_text(asr_mod):
    model = RecordingModel(text="   ")
    result = asyncio.run(asr_mod.AsrClient(loader=lambda name: model).transcribe(WAV_HALF_SECOND))
    assert result["text"] == ""


def test_model_is_loaded_only_once(asr_mod):
    names: list[str] = []
    model = RecordingModel()

    def loader(name):
        names.append(name)
        return model

    client = asr_mod.AsrClient(loader=loader)
    asyncio.run(client.transcribe(WAV_HALF_SECOND))
    asyncio.run(client.transcribe(WAV_HALF_SECOND))
    assert names == ["small"]


def test_default_loader_calls_whisper_with_client_device(asr_mod, monkeypatch):
    calls: list[tuple] = []
    model = RecordingModel()

    def load_model(name, device=None):
        calls.append((name, device))
        return model

    fake = types.ModuleType("whisper")
    fake.load_model = load_model
    monkeypatch.setitem(sys.modules, "whisper", fake)

    client = asr_mod.AsrClient(device=None)
    asyncio.run(client.transcribe(WAV_HALF_SECOND))
    assert calls == [("small", None)]


# --- 降级 ---------------------------------------------------------------------


def test_falls_back_to_tiny_when_small_fails(asr_mod):
    attempts: list[str] = []
    tiny = RecordingModel(text="兜底")

    def loader(name):
        attempts.append(name)
        if name == "small":
            raise RuntimeError("CUDA out of memory")
        return tiny

    client = asr_mod.AsrClient(loader=loader)
    assert client.model_name == "small"
    result = asyncio.run(client.transcribe(WAV_HALF_SECOND))
    assert attempts == ["small", "tiny"]
    assert client.model_name == "tiny"
    assert result["model"] == "tiny"
    assert result["text"] == "兜底"


def test_raises_asr_unavailable_when_both_models_fail(asr_mod):
    def loader(name):
        raise RuntimeError(f"{name} 权重损坏")

    client = asr_mod.AsrClient(loader=loader)
    with pytest.raises(asr_mod.AsrUnavailable) as excinfo:
        asyncio.run(client.transcribe(WAV_HALF_SECOND))
    message = str(excinfo.value)
    assert "small" in message
    assert "tiny" in message


# --- 串行锁 -------------------------------------------------------------------


def test_lock_serializes_concurrent_transcribe(asr_mod):
    active = {"now": 0, "peak": 0}
    model = ClockedModel(active)
    lock = asyncio.Lock()
    client = asr_mod.AsrClient(lock=lock, loader=lambda name: model)

    async def both():
        await asyncio.gather(client.transcribe(WAV_HALF_SECOND), client.transcribe(WAV_HALF_SECOND))

    asyncio.run(both())
    assert active["peak"] == 1


# --- 锁的归属与等待预算 -------------------------------------------------------


def test_lock_wait_budget_is_15s_and_separate_from_inference(asr_mod):
    """等锁的预算必须独立于推理预算，而且短得多：TTS 一轮就可能占 70 s。"""
    assert asr_mod.LOCK_WAIT_S == 15.0


def test_lock_wait_budget_stays_below_the_route_timeout(asr_mod):
    """等锁预算必须严格小于路由的推理预算（跨模块常量，两个文件不能各改各的）。

    回归守卫：一旦 LOCK_WAIT_S >= ASR_TIMEOUT_S，忙锁会先被路由的 wait_for 砍掉，
    504「语音识别超时」重新盖住 503「语音识别忙，稍后再试」——前端又拿不到可重试的语义。
    """
    web_api = importlib.import_module("astrbot_plugin_mas3d_game.web_api")
    assert asr_mod.LOCK_WAIT_S < web_api.ASR_TIMEOUT_S


def test_cancelled_transcribe_keeps_lock_until_gpu_thread_finishes(asr_mod):
    """取消调用方（HTTP 超时 / 客户端断开）不能让锁先于 GPU 线程释放。

    钉住 I1：锁由 worker 任务持有，shield 只挡下调用方那一层的取消。
    在修复前这条用例会失败——`async with` 在 __aexit__ 里直接释放锁。
    """
    model = BlockingModel()
    lock = asyncio.Lock()
    client = asr_mod.AsrClient(lock=lock, loader=lambda name: model)

    async def scenario() -> None:
        caller = asyncio.create_task(client.transcribe(WAV_HALF_SECOND))
        try:
            deadline = time.monotonic() + 5.0
            while not model.started.is_set() and time.monotonic() < deadline:
                await asyncio.sleep(0.01)
            assert model.started.is_set(), "假模型没有进入 transcribe"

            caller.cancel()
            with pytest.raises(asyncio.CancelledError):
                await caller
            # GPU 线程还在跑：锁必须仍然被 worker 占着
            assert lock.locked() is True

            model.release.set()
            deadline = time.monotonic() + 2.0
            while lock.locked() and time.monotonic() < deadline:
                await asyncio.sleep(0.01)
            assert lock.locked() is False
        finally:
            model.release.set()

    asyncio.run(scenario())
    assert len(model.calls) == 1


def test_lock_wait_has_its_own_budget_and_reports_asr_busy(asr_mod, monkeypatch):
    """锁被占着时按 LOCK_WAIT_S 失败成 AsrBusy，而不是耗掉整段推理预算。

    钉住 I2：15 s 等锁 → AsrBusy("语音识别忙")，模型一次都没被调用；
    而且等待超时的那个 acquire 不能把锁偷走——持有者释放后锁必须真的空出来。
    """
    monkeypatch.setattr(asr_mod, "LOCK_WAIT_S", 0.05)
    model = RecordingModel()
    lock = asyncio.Lock()
    client = asr_mod.AsrClient(lock=lock, loader=lambda name: model)

    async def scenario() -> tuple[float, str, bool]:
        held = asyncio.Event()

        async def holder() -> None:
            async with lock:
                held.set()
                await asyncio.sleep(30.0)

        holder_task = asyncio.create_task(holder())
        await held.wait()
        started = time.monotonic()
        with pytest.raises(asr_mod.AsrBusy) as excinfo:
            await client.transcribe(WAV_HALF_SECOND)
        elapsed = time.monotonic() - started
        holder_task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await holder_task
        return elapsed, str(excinfo.value), lock.locked()

    elapsed, message, still_locked = asyncio.run(scenario())
    assert message == "语音识别忙"
    assert elapsed < 1.0  # 只等了 LOCK_WAIT_S，不是 45 s 推理预算
    assert model.calls == []
    assert still_locked is False
