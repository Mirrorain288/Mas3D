using System;
using System.Text;
using Mas3D.Voice;
using NUnit.Framework;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 3 的 WAV 编码器用例：头字段逐个断言、样本往返与 clamp、空输入仍然是合法 WAV、
    /// <see cref="WavEncoder.Trim"/> 只截断不补齐。
    ///
    /// 头布局按 44 字节走（RIFF/WAVE/fmt 16/data），字节序 little-endian；断言直接读偏移，
    /// 不借第三方解码器，免得“编码器和解码器一起错”时用例还是绿的。
    /// </summary>
    public class WavEncoderTests
    {
        const int SampleRate = 16000;

        [Test]
        public void WritesThe44ByteMonoHeader()
        {
            var samples = new float[8];
            var wav = WavEncoder.Encode(samples, SampleRate);

            Assert.AreEqual(44 + samples.Length * 2, wav.Length, "总长 = 44 字节头 + 每样本 2 字节");
            Assert.AreEqual("RIFF", Ascii(wav, 0, 4));
            Assert.AreEqual(36 + samples.Length * 2, Int32At(wav, 4), "RIFF 块大小 = 36 + data 字节数");
            Assert.AreEqual("WAVE", Ascii(wav, 8, 4));
            Assert.AreEqual("fmt ", Ascii(wav, 12, 4));
            Assert.AreEqual(16, Int32At(wav, 16), "PCM 的 fmt 块固定 16 字节");
            Assert.AreEqual(1, Int16At(wav, 20), "audioFormat = PCM(1)");
            Assert.AreEqual(1, Int16At(wav, 22), "声道数");
            Assert.AreEqual(SampleRate, Int32At(wav, 24), "采样率");
            Assert.AreEqual(SampleRate * 2, Int32At(wav, 28), "byteRate = sampleRate * blockAlign");
            Assert.AreEqual(2, Int16At(wav, 32), "blockAlign = channels * 2");
            Assert.AreEqual(16, Int16At(wav, 34), "bits = 16");
            Assert.AreEqual("data", Ascii(wav, 36, 4));
            Assert.AreEqual(samples.Length * 2, Int32At(wav, 40), "data 块大小");
        }

        [Test]
        public void HeaderFollowsTheRequestedChannelLayout()
        {
            // 多声道时输入按已交织处理：样本数不变，只是 blockAlign/byteRate 跟着声道数走。
            var samples = new float[4];
            var wav = WavEncoder.Encode(samples, 48000, 2);

            Assert.AreEqual(44 + samples.Length * 2, wav.Length);
            Assert.AreEqual(2, Int16At(wav, 22), "声道数照 channels 写");
            Assert.AreEqual(48000, Int32At(wav, 24));
            Assert.AreEqual(4, Int16At(wav, 32), "blockAlign = 2 声道 * 2 字节");
            Assert.AreEqual(48000 * 4, Int32At(wav, 28), "byteRate = sampleRate * blockAlign");
            Assert.AreEqual(samples.Length * 2, Int32At(wav, 40), "交织数据仍是每样本 2 字节");
        }

        [TestCase(0f, 0)]
        [TestCase(0.5f, 16384)]
        [TestCase(-0.5f, -16384)]
        [TestCase(0.25f, 8192)]
        [TestCase(-0.25f, -8192)]
        [TestCase(1f, 32767)]
        [TestCase(-1f, -32767)]
        public void WritesSamplesAsLittleEndian16Bit(float sample, int expected)
        {
            var wav = WavEncoder.Encode(new[] { sample }, SampleRate);

            Assert.AreEqual(expected, SampleAt(wav, 0));
            Assert.AreEqual((byte)(expected & 0xFF), wav[44], "低字节在前");
            Assert.AreEqual((byte)((expected >> 8) & 0xFF), wav[45], "高字节在后");
        }

        [TestCase(1.5f, 32767)]
        [TestCase(-1.5f, -32767)]
        [TestCase(2f, 32767)]
        [TestCase(-2f, -32767)]
        public void ClampsSamplesOutsideTheUnitRange(float sample, int expected)
        {
            var wav = WavEncoder.Encode(new[] { sample }, SampleRate);
            Assert.AreEqual(expected, SampleAt(wav, 0), "超出 [-1,1] 的样本必须夹住，不能回绕");
        }

        [Test]
        public void EmptyInputStillProducesAValidHeader()
        {
            var wav = WavEncoder.Encode(Array.Empty<float>(), SampleRate);

            Assert.AreEqual(44, wav.Length, "空输入也要有完整头");
            Assert.AreEqual("RIFF", Ascii(wav, 0, 4));
            Assert.AreEqual(36, Int32At(wav, 4), "空 data 的 RIFF 块大小");
            Assert.AreEqual("WAVE", Ascii(wav, 8, 4));
            Assert.AreEqual("data", Ascii(wav, 36, 4));
            Assert.AreEqual(0, Int32At(wav, 40), "data 长度为 0");
        }

        [Test]
        public void TrimCutsWhatIsLongerThanTheWindow()
        {
            var samples = new float[SampleRate]; // 整整 1 秒
            for (var i = 0; i < samples.Length; i++) samples[i] = i;

            var trimmed = WavEncoder.Trim(samples, SampleRate, 0.5f);

            Assert.AreEqual(SampleRate / 2, trimmed.Length, "0.5 秒 = 8000 个样本");
            Assert.AreEqual(0f, trimmed[0]);
            Assert.AreEqual(7999f, trimmed[trimmed.Length - 1], "截断保头不保尾");
        }

        [Test]
        public void TrimLeavesShorterInputAlone()
        {
            var samples = new float[100];
            var trimmed = WavEncoder.Trim(samples, SampleRate, 1f);

            Assert.AreEqual(100, trimmed.Length, "短于窗口不补零");
            Assert.AreSame(samples, trimmed, "没有可截的内容时原样返回，不白白复制一份");
        }

        [Test]
        public void TrimOfAZeroWindowIsEmpty()
        {
            var samples = new float[100];

            Assert.AreEqual(0, WavEncoder.Trim(samples, SampleRate, 0f).Length);
            Assert.AreEqual(0, WavEncoder.Trim(samples, SampleRate, -1f).Length);
            Assert.AreEqual(0, WavEncoder.Trim(null, SampleRate, 1f).Length, "null 按空处理");
        }

        static string Ascii(byte[] wav, int offset, int count)
        {
            return Encoding.ASCII.GetString(wav, offset, count);
        }

        static int Int32At(byte[] wav, int offset)
        {
            return wav[offset] | (wav[offset + 1] << 8) | (wav[offset + 2] << 16) | (wav[offset + 3] << 24);
        }

        static short Int16At(byte[] wav, int offset)
        {
            return (short)(wav[offset] | (wav[offset + 1] << 8));
        }

        static short SampleAt(byte[] wav, int index)
        {
            return Int16At(wav, 44 + index * 2);
        }
    }
}
