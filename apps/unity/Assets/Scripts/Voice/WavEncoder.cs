using System;
using UnityEngine;

namespace Mas3D.Voice
{
    /// <summary>
    /// 把麦克风采样编成 16-bit PCM 的 WAV：固定 44 字节头（RIFF/WAVE/fmt 16/data），
    /// 样本 little-endian。桥接的 <c>POST /mas3d/api/asr</c> 只吃这种裸字节 —— 不套 multipart，
    /// 也就能让 AsrClient 直接把 <see cref="Encode"/> 的结果当 body 发出去。
    ///
    /// 输出的声道布局跟 <c>channels</c> 走，但样本不再重排：调用方给的若是多声道数据，
    /// 视为已经交织好的那一段（每个样本仍是 2 字节），头里的 blockAlign/byteRate 负责描述它。
    /// </summary>
    public static class WavEncoder
    {
        /// <summary>WAV 头固定长度：多出来的只有 data 块里的样本。</summary>
        public const int HeaderBytes = 44;

        /// <summary>满量程：正负对称落在 ±32767，避免 -1f 比 +1f 少一格。</summary>
        const float FullScale = 32767f;

        /// <summary>编码成 WAV 字节；空数组也给一个合法的 44 字节头（data 长度为 0）。</summary>
        public static byte[] Encode(float[] samples, int sampleRate, int channels = 1)
        {
            if (samples == null) samples = Array.Empty<float>();
            if (channels < 1) channels = 1;
            if (sampleRate < 1) sampleRate = 1;

            var dataBytes = samples.Length * 2;
            var wav = new byte[HeaderBytes + dataBytes];

            var blockAlign = channels * 2;
            var byteRate = sampleRate * blockAlign;

            WriteAscii(wav, 0, "RIFF");
            WriteInt32(wav, 4, 36 + dataBytes);   // 整个文件去掉 "RIFF" 与长度字段
            WriteAscii(wav, 8, "WAVE");
            WriteAscii(wav, 12, "fmt ");
            WriteInt32(wav, 16, 16);              // PCM 的 fmt 块长度
            WriteInt16(wav, 20, 1);               // audioFormat = PCM
            WriteInt16(wav, 22, channels);
            WriteInt32(wav, 24, sampleRate);
            WriteInt32(wav, 28, byteRate);
            WriteInt16(wav, 32, blockAlign);
            WriteInt16(wav, 34, 16);              // bits per sample
            WriteAscii(wav, 36, "data");
            WriteInt32(wav, 40, dataBytes);

            for (var i = 0; i < samples.Length; i++)
            {
                // 先夹再乘满量程：超范围的值会削顶，而不是回绕成反相的爆音。
                var value = Mathf.Clamp(samples[i], -1f, 1f);
                var pcm = (short)Mathf.RoundToInt(value * FullScale);
                wav[HeaderBytes + i * 2] = (byte)(pcm & 0xFF);
                wav[HeaderBytes + i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
            }

            return wav;
        }

        /// <summary>
        /// 只保留前 <paramref name="seconds"/> 秒；短于窗口原样返回（不补零），
        /// 这样“按了 2 秒就松手”的录音不会被补成一条长静音。
        /// </summary>
        public static float[] Trim(float[] samples, int sampleRate, float seconds)
        {
            if (samples == null || samples.Length == 0) return Array.Empty<float>();
            if (sampleRate <= 0 || seconds <= 0f) return Array.Empty<float>();

            var keep = (long)Math.Floor((double)seconds * sampleRate);
            if (keep >= samples.Length) return samples;

            var trimmed = new float[keep];
            Array.Copy(samples, trimmed, (int)keep);
            return trimmed;
        }

        static void WriteAscii(byte[] buffer, int offset, string text)
        {
            for (var i = 0; i < text.Length; i++) buffer[offset + i] = (byte)text[i];
        }

        static void WriteInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
