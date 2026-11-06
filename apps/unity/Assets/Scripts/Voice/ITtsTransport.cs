using System;
using System.Collections;
using Mas3D.Net;
using UnityEngine;

namespace Mas3D.Voice
{
    /// <summary>一次合成请求的两种结局：拿到这一段语音 / 语音暂时不可用。</summary>
    public enum TtsStatus
    {
        Voice = 0,
        Unavailable = 1,
    }

    /// <summary>
    /// 合成结果。<see cref="Message"/> 只在 Unavailable 时有意义：
    /// 降级时把服务端那句人话留下（"TTS 没起来"比"HTTP 503"有用得多）。
    /// </summary>
    public struct TtsReply
    {
        public TtsStatus Status;
        public VoiceRef Voice;
        public string Message;

        public static TtsReply VoiceOf(VoiceRef voice)
        {
            return new TtsReply { Status = TtsStatus.Voice, Voice = voice, Message = string.Empty };
        }

        public static TtsReply Unavailable(string message)
        {
            return new TtsReply { Status = TtsStatus.Unavailable, Message = message ?? string.Empty };
        }
    }

    /// <summary>
    /// 合成与取音频的最小接口。做成接口是为了 PlayMode 能塞一个假实现：
    /// Task 1 要验的是"回复到了之后字幕怎么逐句出现"，能不能连上 TTS 服务不该由测试决定。
    /// </summary>
    public interface ITtsTransport
    {
        /// <summary>
        /// 合成当前这一句。<paramref name="text"/> 就是这一句本身，不带句子下标。
        ///
        /// 为什么不照 web 的 synth(text, index)：桥接先把中文翻成英文、再按英文句切分取下标
        /// （apps/bridge/tts.py 的 split_sentences）。中文句数与英文句数不等时，大的一方会少播尾巴，
        /// 小的一方会拿到 400「句子下标越界」。一次只送一句，服务端就只会合成这一句，
        /// 返回的 durationMs 也就是这句的真时长——不需要任何下标。
        /// </summary>
        IEnumerator Synth(string text, Action<TtsReply> done);

        /// <summary>
        /// 取音频并解码成 clip。成功交出的 clip 所有权随即转给调用方（由它销毁），
        /// 实现方不许再持有或在后续回调里使用它。
        /// </summary>
        IEnumerator LoadAudio(string url, Action<AudioClip> onOk, Action<string> onFail);
    }
}
