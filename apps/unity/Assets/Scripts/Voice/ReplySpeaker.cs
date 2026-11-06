using System;
using System.Collections;
using System.Text;
using Mas3D.Net;
using Mas3D.UI;
using UnityEngine;

namespace Mas3D.Voice
{
    /// <summary>
    /// 一轮回复的播报：切句 → 逐句合成（一句一次请求，不带下标）→ 放音频 → 字幕跟着一句一句出现。
    ///
    /// 四条硬规则（P7 Task 1）：
    /// 1) 语音是附加品。任何一环失败（400/401/503/连不上/解码失败）都只做一件事：把整段字幕显示出来、
    ///    在状态栏说一句"语音暂时不可用"，然后让位。不抛异常、不锁输入、不影响下一轮。
    /// 2) 同一轮只播一次。控制器可能因为重连/重放把同一段文本再交一次，这里按 (turnId, text) 认人。
    /// 3) 新一轮开始立刻停上一轮。停音频也要停协程，否则两段声音会叠在一起；停的时候连字幕一起收。
    /// 4) 每句一个 clip，clip 的所有权在 transport 交出来的那一刻转到本组件：换句、停播、销毁都要还回去。
    ///
    /// 为什么字幕不等音频：音频可能比字幕慢，也可能永远不来。字幕是玩家真正在读的东西，先给。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ReplySpeaker : MonoBehaviour
    {
        /// <summary>合成上限，对齐 apps/web/src/chat/voice.ts 的 VOICE_MAX_SENTENCES。</summary>
        public const int MaxSentences = 40;

        /// <summary>降级时状态栏那句话。测试与面板共用这一份文案，别在两处各写一遍。</summary>
        public const string FallbackCopy = "语音暂时不可用，先只看字幕。";

        /// <summary>
        /// 字幕最多保留最近几句。40 句全铺在 #input-box 上面会把输入区挤没；
        /// 前面的用一行省略号提示"还有内容"，历史本身仍在消息流里。
        /// </summary>
        public const int MaxSubtitleLines = 6;

        /// <summary>至少让出一帧。时长未知时若不等，整段字幕会在同一帧一起冒出来。</summary>
        const double MinimumWaitMs = 1000.0 / 60.0;

        /// <summary>字幕落在哪块面板上（必须有：没有面板就没有地方显示字幕）。</summary>
        public DialoguePanel Panel;

        /// <summary>地址与令牌跟桥接共用同一块：场景里由 RoomBootstrap 交过来。</summary>
        public BridgeSettings Settings;

        /// <summary>合成/取音频的实现。留空则用真的 UnityWebRequest 实现（PlayMode 测试塞假的）。</summary>
        public ITtsTransport Transport;

        /// <summary>扬声器。留空则在第一次真正播放时补一个（无头跑批下不建，见 Play）。</summary>
        public AudioSource Audio;

        [Range(0f, 1f)] public float Volume = 1f;

        string _turnId = string.Empty;
        string _text = string.Empty;
        string[] _sentences = new string[0];
        int _revealed;
        string _subtitle = string.Empty;
        string _status = string.Empty;
        bool _speaking;
        bool _hasSpoken;
        Coroutine _routine;
        int _generation;

        /// <summary>当前手上那个 clip（所有权已从 transport 转过来）。</summary>
        AudioClip _clip;

        /// <summary>累计销毁过多少个 clip。给用例一个可观察的口子：40 句不该留 40 个常驻 clip。</summary>
        public int ClipsDestroyed { get; private set; }

        /// <summary>最近一次交给播报的轮次/原文：用来判断"这轮是不是已经播过了"。</summary>
        public string LastTurnId { get { return _turnId; } }

        public string LastText { get { return _text; } }

        /// <summary>切好的句子（完整回复，不截断：40 句只管合成，字幕要全）。</summary>
        public string[] Sentences { get { return _sentences; } }

        /// <summary>已经露出几句。</summary>
        public int Revealed { get { return _revealed; } }

        /// <summary>当前字幕文本（已露出的句子以换行相连，最多 <see cref="MaxSubtitleLines"/> 句 + 一行省略号）。</summary>
        public string SubtitleText { get { return _subtitle; } }

        /// <summary>降级时写入 <see cref="FallbackCopy"/>；正常播完保持空。</summary>
        public string StatusMessage { get { return _status; } }

        public bool Speaking { get { return _speaking; } }

        /// <summary>控制器在 reply 落地时调用。同一轮重复交同一段文本直接忽略。</summary>
        public void Speak(string turnId, string text)
        {
            var id = turnId ?? string.Empty;
            var body = text ?? string.Empty;
            if (_hasSpoken && id == _turnId && body == _text) return;

            Stop();

            _hasSpoken = true;
            _turnId = id;
            _text = body;
            _sentences = TtsSentences.Split(body);
            _revealed = 0;
            _subtitle = string.Empty;
            _status = string.Empty;
            if (Panel != null) Panel.ShowSubtitle(string.Empty);

            if (_sentences.Length == 0) return;   // 没有句子就没有声音，也没有字幕可露

            _speaking = true;
            _generation++;
            _routine = StartCoroutine(Run(_generation));
        }

        /// <summary>
        /// 停掉正在播的这一轮（新一轮开始、控制器被禁用、error 帧、组件销毁都走这里）。
        /// 字幕也一起收：停播之后还挂着上一轮的句子，玩家会以为她还在说。
        /// </summary>
        public void Stop()
        {
            // 只 StopCoroutine 不够：它停的是当前这一层，嵌套的 transport 协程仍可能在下一帧回调。
            // 加一代号让所有还在跑的实例自己在检查点退出。
            _generation++;
            if (_routine != null && isActiveAndEnabled) StopCoroutine(_routine);
            _routine = null;
            _speaking = false;
            // 停播之后这一轮就算没说过：同一个 turn/同一段文本再来（重连补发、手动重播）要能重新出声。
            // 重复 frame 的去重靠 Speak 入口那一次判断，不靠这里记住"播过了"。
            _hasSpoken = false;
            StopAudio();
            ReleaseClip();
            _revealed = 0;
            _subtitle = string.Empty;
            if (Panel != null) Panel.ShowSubtitle(string.Empty);
        }

        void OnDisable()
        {
            Stop();
        }

        void OnDestroy()
        {
            // 销毁组件时 AudioSource 会跟着走，但 clip 是我们自己 new 出来的，得自己还。
            ReleaseClip();
        }

        IEnumerator Run(int generation)
        {
            var transport = ResolveTransport();
            var baseUrl = TtsUrls.BaseFrom(Settings != null ? Settings.Url : BridgeSettings.DefaultUrl);

            // 真实现每次开播前对齐地址与令牌：场景里的 Settings 可能在连接后才拿到磁盘令牌。
            var real = transport as UnityWebRequestTtsTransport;
            if (real != null)
            {
                real.BaseUrl = baseUrl;
                real.Token = ResolveToken();
            }

            var count = Mathf.Min(_sentences.Length, MaxSentences);
            var next = 0;

            while (next < count)
            {
                if (generation != _generation) yield break;

                // 一句一次请求，送的就是这一句：桥接的 sentence 下标是英文句的，中文句数一对不上就少播或 400。
                var reply = default(TtsReply);
                yield return transport.Synth(_sentences[next], r => reply = r);
                if (generation != _generation) yield break;

                if (reply.Status != TtsStatus.Voice || reply.Voice == null)
                {
                    Degrade(reply.Message);
                    yield break;
                }

                AudioClip clip = null;
                var failure = string.Empty;
                yield return transport.LoadAudio(
                    TtsUrls.AudioUrl(baseUrl, reply.Voice.AudioUrl),
                    c =>
                    {
                        // 停播可能就发生在这一次回调里：Stop 顺手 StopCoroutine 之后，本协程不会再恢复，
                        // 这个 clip 既进不了 Audio.clip，也等不到下面那行检查 —— 所有权已经在我们手上，当场还回去。
                        if (c != null && generation != _generation)
                        {
                            DestroyClip(c);
                            return;
                        }
                        clip = c;
                    },
                    e => failure = e);
                if (generation != _generation)
                {
                    // 停播发生在回调之后、协程恢复之前：局部变量里的这一份同样没人管，一并归还。
                    // 已经播出去的那一份在 _clip 里，Stop 的 ReleaseClip 已经收走，这里不会碰到它。
                    DestroyClip(clip);
                    yield break;
                }

                if (clip == null)
                {
                    Degrade(string.IsNullOrEmpty(failure) ? "音频没取到" : failure);
                    yield break;
                }

                Play(clip);
                Reveal(next);   // 音频起播的同一帧露字幕：先给可读的东西，音频只是伴奏

                var elapsed = 0.0;
                var waitMs = WaitMsFor(reply.Voice, clip);
                if (waitMs < MinimumWaitMs) waitMs = MinimumWaitMs;
                while (elapsed < waitMs)
                {
                    if (generation != _generation) yield break;
                    elapsed += Time.deltaTime * 1000.0;
                    yield return null;
                }
                next++;
            }

            // 合成停在第 N 句，字幕仍然是完整回复：剩下的句子一次性铺出来（读全文比读到一半强）。
            RevealAll();
            _speaking = false;
            _routine = null;
        }

        /// <summary>这一句该等多久（毫秒）：优先桥接给的时长，其次 clip 自己的长度。</summary>
        double WaitMsFor(VoiceRef voice, AudioClip clip)
        {
            if (voice != null && voice.DurationMs > 0) return voice.DurationMs;
            if (clip != null && clip.length > 0f) return clip.length * 1000.0;
            return 0.0;
        }

        void Reveal(int index)
        {
            if (index < 0 || index >= _sentences.Length) return;
            if (index < _revealed) return;
            _revealed = index + 1;
            Publish();
        }

        void RevealAll()
        {
            if (_sentences.Length == 0) return;
            if (_revealed >= _sentences.Length) return;
            _revealed = _sentences.Length;
            Publish();
        }

        void Publish()
        {
            var start = Mathf.Max(0, _revealed - MaxSubtitleLines);
            var builder = new StringBuilder();
            if (start > 0) builder.Append("…");
            for (var i = start; i < _revealed && i < _sentences.Length; i++)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(_sentences[i]);
            }
            _subtitle = builder.ToString();
            if (Panel != null) Panel.ShowSubtitle(_subtitle);
        }

        /// <summary>降级：整段字幕 + 状态栏一句话。不抛异常、不碰输入可用性、不改 Phase。</summary>
        void Degrade(string message)
        {
            if (!string.IsNullOrEmpty(message)) Debug.Log("[voice] 语音不可用：" + message);
            _status = FallbackCopy;
            _speaking = false;
            _routine = null;
            StopAudio();
            ReleaseClip();
            RevealAll();
            if (Panel != null) Panel.SetStatus(FallbackCopy);
        }

        void Play(AudioClip clip)
        {
            // 无头跑批没有声卡：建了也放不出声，只会在某些机器上留下"播放失败"的日志。
            // 字幕与时长不依赖音频（等的是 DurationMs），所以这里直接让开；
            // 但 clip 已经是我们的了，不播就当场销毁，别留下没人引用的常驻对象。
            if (Application.isBatchMode)
            {
                DestroyClip(clip);
                return;
            }

            if (Audio == null)
            {
                Audio = gameObject.AddComponent<AudioSource>();
                Audio.playOnAwake = false;
                Audio.spatialBlend = 0f;
            }

            try
            {
                StopAudio();
                ReleaseClip();   // 上一句的 clip 已经放完：换句时还回去，别攒着
                _clip = clip;
                Audio.clip = clip;
                Audio.volume = Volume;
                Audio.Play();
            }
            catch (Exception e)
            {
                // 播放失败不影响字幕与下一轮：播报不该成为新的失败点。
                Debug.Log("[voice] 播放失败：" + e.Message);
            }
        }

        void StopAudio()
        {
            if (Audio != null && Audio.isActiveAndEnabled) Audio.Stop();
        }

        /// <summary>销毁手上这个 clip（它由 <see cref="ITtsTransport.LoadAudio"/> 交出，所有权已转移到这里）。</summary>
        void ReleaseClip()
        {
            var clip = _clip;
            _clip = null;
            if (clip == null) return;
            if (Audio != null && Audio.clip == clip) Audio.clip = null;
            DestroyClip(clip);
        }

        void DestroyClip(AudioClip clip)
        {
            if (clip == null) return;
            ClipsDestroyed++;
            Destroy(clip);   // PlayMode 里延迟到帧末真正释放，不影响本帧的字幕
        }

        ITtsTransport ResolveTransport()
        {
            if (Transport != null) return Transport;
            var real = new UnityWebRequestTtsTransport();
            Transport = real;
            return real;
        }

        string ResolveToken()
        {
            if (Settings != null && !string.IsNullOrWhiteSpace(Settings.Token)) return Settings.Token;
            // 场景里 DialogueBridge 可能还没把磁盘令牌写回 Settings：回磁盘兜一次（同一个读取口）。
            return DialogueBridge.ReadTokenFromDisk() ?? string.Empty;
        }
    }
}
