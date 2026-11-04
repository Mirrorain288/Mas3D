using Mas3D.Voice;
using NUnit.Framework;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// <see cref="MicrophoneRecorder"/> 的读数层。真麦克风（<c>UnityEngine.Microphone</c>）在无头机器上
    /// 不存在，但"从 AudioClip 里读出样本、报出它真实的采样率"是纯数据操作，可以用
    /// <c>AudioClip.Create</c> 造一段出来测。
    ///
    /// 为什么值得钉：申请的 16000 Hz 不保证是拿到的（驱动可能给 44.1/48 kHz）。头里写错采样率时，
    /// 短片段会被 whisper 听成 3 倍速，48 kHz 的 12 秒还会被桥接按声明率算成 36 秒直接 400。
    /// </summary>
    public class MicrophoneRecorderTests
    {
        [Test]
        public void ReportsTheClipsRealRateNotTheRequestedOne()
        {
            var clip = AudioClip.Create("mic-44100", 4800, 1, 44100, false);
            try
            {
                var samples = new float[4800];
                for (var i = 0; i < samples.Length; i++) samples[i] = 0.25f;
                clip.SetData(samples, 0);

                int rate;
                var read = MicrophoneRecorder.ReadMono(clip, 4800, out rate);

                Assert.AreEqual(44100, rate,
                    "采样率必须是 AudioClip.frequency：驱动给的可能是 44.1k，声明成 16k 会让 whisper 听成 3 倍速");
                Assert.AreEqual(4800, read.Length);
                Assert.AreEqual(0.25f, read[0], 0.0001f, "样本要原样读出来");
            }
            finally
            {
                Object.Destroy(clip);
            }
        }

        [Test]
        public void AveragesChannelsDownToMono()
        {
            var clip = AudioClip.Create("mic-stereo", 100, 2, 48000, false);
            try
            {
                var interleaved = new float[200];
                for (var i = 0; i < 100; i++)
                {
                    interleaved[i * 2] = 1f;
                    interleaved[i * 2 + 1] = 0f;
                }
                clip.SetData(interleaved, 0);

                int rate;
                var read = MicrophoneRecorder.ReadMono(clip, 100, out rate);

                Assert.AreEqual(48000, rate, "多声道也要报出真实采样率");
                Assert.AreEqual(100, read.Length, "降混不能把帧数翻倍");
                Assert.AreEqual(0.5f, read[0], 0.0001f, "L=1 R=0 的均值是 0.5");
            }
            finally
            {
                Object.Destroy(clip);
            }
        }

        [Test]
        public void ClampsToTheClipAndToleratesNothingToRead()
        {
            var clip = AudioClip.Create("mic-clamp", 100, 1, 16000, false);
            try
            {
                int rate;
                Assert.AreEqual(100, MicrophoneRecorder.ReadMono(clip, 500, out rate).Length,
                    "GetPosition 可能超过 clip.samples：要夹住，别把尾部静音也读进来");
                Assert.AreEqual(16000, rate);

                Assert.AreEqual(0, MicrophoneRecorder.ReadMono(clip, 0, out rate).Length, "0 帧就是没录到东西");
                Assert.AreEqual(16000, rate, "空读也要报出采样率，调用方靠它判断 take 长度");

                Assert.AreEqual(0, MicrophoneRecorder.ReadMono(null, 10, out rate).Length, "null clip 不能炸");
            }
            finally
            {
                Object.Destroy(clip);
            }
        }
    }
}
