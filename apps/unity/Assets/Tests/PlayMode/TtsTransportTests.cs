using System;
using System.Collections;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D.Voice;

namespace Mas3D.Tests
{
    /// <summary>
    /// 传输层的一条真用例：假 transport 验不出"交出去的 clip 归谁"，只有真的
    /// DownloadHandlerAudioClip 走一遍才知道。这里用 file:// 喂一个自己写的小 WAV，
    /// 不依赖桥接、不依赖声卡。
    /// </summary>
    public class TtsTransportTests
    {
        [UnityTest]
        public IEnumerator TheClipHandedUpIsAnOwnedCopyThatOutlivesTheRequest()
        {
            var path = Path.Combine(Path.GetTempPath(), "mas3d-tts-probe-" + Guid.NewGuid().ToString("N") + ".wav");
            AudioClip got = null;
            string failed = null;
            try
            {
                File.WriteAllBytes(path, Wave(2400));

                var transport = new UnityWebRequestTtsTransport { TimeoutSeconds = 10 };
                yield return transport.LoadAudio(new Uri(path).AbsoluteUri, c => got = c, e => failed = e);

                Assert.IsNull(failed, "本地 WAV 都读不进来，谈不到拷不拷：" + failed);
                Assert.IsNotNull(got, "成功回调必须给一个 clip");
                Assert.IsTrue(got.name.EndsWith("-owned", StringComparison.Ordinal),
                    "交给上层的应该是传输层自己拷的那份（名字带 -owned）；拿到的是 " + got.name
                    + " ——那就是下载 handler 的对象，它所在的 request 已经在 using 里 Dispose 了");
                Assert.Greater(got.samples, 0, "拷出来的样本数不为零");

                var buffer = new float[got.samples * got.channels];
                Assert.IsTrue(got.GetData(buffer, 0), "request 已销毁，这份 clip 仍要取得到样本");
                Assert.IsTrue(Array.Exists(buffer, s => Mathf.Abs(s) > 0.01f), "样本不是全静音");
            }
            finally
            {
                if (got != null) UnityEngine.Object.DestroyImmediate(got);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>16-bit PCM 单声道 24 kHz 的正弦：够 Unity 解成 AudioClip，也够验样本非零。</summary>
        static byte[] Wave(int samples, int rate = 24000)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                var dataBytes = samples * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);          // PCM
                writer.Write((short)1);          // mono
                writer.Write(rate);
                writer.Write(rate * 2);          // byte rate
                writer.Write((short)2);          // block align
                writer.Write((short)16);         // bits
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);
                for (var i = 0; i < samples; i++)
                {
                    var v = (short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * 8000);
                    writer.Write(v);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
