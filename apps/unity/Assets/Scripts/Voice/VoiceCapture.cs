using System;
using UnityEngine;

namespace Mas3D.Voice
{
    /// <summary>
    /// 录音器的唯一接口。VoiceInput 只认它，于是 PlayMode 用例可以塞一个假实现，
    /// 在没有麦克风的机器上也能跑“按住 → 松开 → 识别”的整条链。
    /// </summary>
    public interface IRecorder
    {
        /// <summary>正在录。</summary>
        bool Recording { get; }

        /// <summary>上一次 Start 失败时的人话原因；成功时为空串。</summary>
        string Error { get; }

        /// <summary>开始录；拿不到设备/启动失败返回 false，原因写进 <see cref="Error"/>。</summary>
        bool Start();

        /// <summary>停止并把这次录到的样本交出来；<paramref name="sampleRate"/> 是这些样本的采样率。</summary>
        float[] Stop(out int sampleRate);
    }

    /// <summary>
    /// 真麦克风录音：向设备申请 16000 Hz、最长 30 秒、不循环，松手时用 <c>Microphone.GetPosition</c>
    /// 取实际录到的长度（<c>AudioClip.samples</c> 永远是申请的长度，直接用会把尾部静音也发上去），
    /// 并交出 <c>AudioClip.frequency</c> 报的真实采样率 —— 申请的 16 kHz 不保证就是拿到的。
    ///
    /// 这是全工程唯一碰 <c>UnityEngine.Microphone</c> 的地方。没有麦克风的机器上这里不做任何事，
    /// Start 留下 <see cref="NoDeviceCopy"/> 并返回 false —— 不进自动化测试（CI 不该因为没声卡而红）。
    /// </summary>
    public sealed class MicrophoneRecorder : IRecorder
    {
        /// <summary>与桥接侧 ASR 的 16 kHz 约定一致。</summary>
        public const int SampleRate = 16000;

        /// <summary>单次最长录音；再长也没人愿意等 whisper 跑完。</summary>
        public const int MaxSeconds = 30;

        /// <summary>没有输入设备时的原因。</summary>
        public const string NoDeviceCopy = "没有可用的麦克风";

        /// <summary>有设备但 <c>Microphone.Start</c> 没给出片段时的原因。</summary>
        public const string StartFailedCopy = "麦克风启动失败";

        string _device;
        AudioClip _clip;

        public bool Recording { get; private set; }

        public string Error { get; private set; } = string.Empty;

        public bool Start()
        {
            Error = string.Empty;
            if (Recording) return true;

            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                Error = NoDeviceCopy;
                return false;
            }

            _device = devices[0];
            _clip = Microphone.Start(_device, false, MaxSeconds, SampleRate);
            if (_clip == null)
            {
                Error = StartFailedCopy;
                return false;
            }

            Recording = true;
            return true;
        }

        public float[] Stop(out int sampleRate)
        {
            sampleRate = SampleRate;
            if (!Recording) return Array.Empty<float>();
            Recording = false;

            // 先读位置再 End：Microphone.End 只停设备，缓冲要到 Stop 末尾 Destroy(clip) 才释放。
            var position = Microphone.GetPosition(_device);
            Microphone.End(_device);

            var clip = _clip;
            _clip = null;
            if (clip == null) return Array.Empty<float>();

            try
            {
                return ReadMono(clip, position, out sampleRate);
            }
            finally
            {
                UnityEngine.Object.Destroy(clip);
            }
        }

        /// <summary>
        /// 把 <paramref name="clip"/> 从 0 起的前 <paramref name="frames"/> 帧读成单声道样本，
        /// 并报出这段音频**真实**的采样率（<see cref="AudioClip.frequency"/>）。
        ///
        /// 采样率不能返回调用 <c>Microphone.Start</c> 时的请求值：驱动可能给 44.1/48 kHz，
        /// 那时 WAV 头里写 16000 会让 whisper 把 2 秒听成 6 秒（短片段变调），
        /// 而 48 kHz 的 12 秒会被桥接按声明率算成 36 秒直接 400。
        /// </summary>
        public static float[] ReadMono(AudioClip clip, int frames, out int sampleRate)
        {
            sampleRate = SampleRate;
            if (clip == null) return Array.Empty<float>();

            sampleRate = clip.frequency > 0 ? clip.frequency : SampleRate;
            frames = Mathf.Clamp(frames, 0, clip.samples);
            if (frames <= 0) return Array.Empty<float>();

            var channels = Mathf.Max(1, clip.channels);
            var raw = new float[frames * channels];
            if (!clip.GetData(raw, 0)) return Array.Empty<float>();
            if (channels == 1) return raw;

            // 麦克风在部分平台上会给多声道：WavEncoder 按单声道写头，这里先降混。
            var mono = new float[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++) sum += raw[frame * channels + channel];
                mono[frame] = sum / channels;
            }
            return mono;
        }
    }
}
