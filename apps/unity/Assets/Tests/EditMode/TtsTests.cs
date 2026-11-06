using System;
using Mas3D.Net;
using Mas3D.Voice;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;

namespace Mas3D.Tests
{
    /// <summary>
    /// P7 Task 1 的纯函数层：ws→http 的地址换算、synth/音频两段 URL 的拼接、请求头与 JSON body、
    /// 按句切分。这些都是"错了会静默播错"的地方——播错比播不出来更难发现，
    /// 所以放在 EditMode 里逐条钉死；PlayMode 只验"回复到了之后字幕真的逐句出现"。
    /// </summary>
    public class TtsTests
    {
        // ---- 地址 ---------------------------------------------------------------

        [Test]
        public void TheBridgeSocketAddressBecomesAnHttpBase()
        {
            Assert.AreEqual("http://127.0.0.1:8790", TtsUrls.BaseFrom("ws://127.0.0.1:8790/mas3d/ws"),
                "ws 的地址要换成 http，端口照留，路径丢掉");
            Assert.AreEqual("https://example.test", TtsUrls.BaseFrom("wss://example.test/mas3d/ws"),
                "wss 要换成 https：把一段加密的通道降成明文是不可接受的副作用");
            Assert.AreEqual("http://127.0.0.1:8790", TtsUrls.BaseFrom(""),
                "空地址回落到默认端点，而不是拼出一个空 base");
            Assert.AreEqual("http://127.0.0.1:8790", TtsUrls.BaseFrom("不是地址"),
                "非法地址同样回落——播报不该因为配置写错就抛异常");
        }

        [Test]
        public void TheTwoVoiceUrlsJoinOntoTheBaseWithoutDoublingSlashes()
        {
            const string baseUrl = "http://127.0.0.1:8790";

            Assert.AreEqual("http://127.0.0.1:8790/mas3d/api/voice/synth", TtsUrls.SynthUrl(baseUrl));
            Assert.AreEqual("http://127.0.0.1:8790/mas3d/api/voice/synth", TtsUrls.SynthUrl(baseUrl + "/"),
                "基础地址带不带尾斜杠都得拼出同一个地址");

            Assert.AreEqual("http://127.0.0.1:8790/mas3d/api/voice/abc.wav",
                TtsUrls.AudioUrl(baseUrl, "/mas3d/api/voice/abc.wav"));
            Assert.AreEqual("http://127.0.0.1:8790/mas3d/api/voice/abc.wav",
                TtsUrls.AudioUrl(baseUrl, "mas3d/api/voice/abc.wav"),
                "桥接给的是相对路径，带不带前导斜杠都得接得上");
            Assert.AreEqual("https://cdn.example.test/a.wav",
                TtsUrls.AudioUrl(baseUrl, "https://cdn.example.test/a.wav"),
                "已经绝对的地址原样用：以后音频换到 CDN 时不该被拼坏");
            Assert.AreEqual(string.Empty, TtsUrls.AudioUrl(baseUrl, null),
                "空地址就是空地址，别拼出一个只有 base 的请求");
        }

        // ---- 请求 ---------------------------------------------------------------

        [Test]
        public void TheSynthRequestCarriesTheBearerTokenAndOnlyTheCurrentSentence()
        {
            // 令牌是真的令牌文件里那种 ASCII 串：HTTP 头放不下中文，
            // 非 ASCII 令牌由下面那条"非法字符"用例钉住（不带 Authorization，而不是抛异常）。
            using (var request = UnityWebRequestTtsTransport.CreateSynthRequest(
                       "http://127.0.0.1:8790/mas3d/api/voice/synth", "s3cr3t-token", "她说：\"你好\"。"))
            {
                Assert.AreEqual("POST", request.method, "synth 是 POST");
                Assert.AreEqual("Bearer s3cr3t-token", request.GetRequestHeader("Authorization"),
                    "桥接的 require_token 只认 Bearer");
                Assert.AreEqual("application/json", request.GetRequestHeader("Content-Type"));

                var raw = request.uploadHandler as UnityEngine.Networking.UploadHandlerRaw;
                Assert.IsNotNull(raw, "synth 的 body 必须真的挂在请求上，不能只写在字段里");
                var json = System.Text.Encoding.UTF8.GetString(raw.data);
                var body = JsonUtility.FromJson<SynthBody>(json);
                Assert.AreEqual("她说：\"你好\"。", body.text, "正文要原样送上去，引号也得转义对");
                StringAssert.DoesNotContain("\"sentence\"", json,
                    "body 里不许带 sentence：桥接的下标是英文句的，中文句数对不上就会少播或 400");
            }

            using (var anonymous = UnityWebRequestTtsTransport.CreateSynthRequest("http://x/y", "  ", "你好"))
            {
                Assert.IsNull(anonymous.GetRequestHeader("Authorization"),
                    "没有令牌时不带 Authorization：空的 Bearer 只会换回 401");
            }

            // 令牌文件被改坏（非 ASCII / 带换行）时 SetRequestHeader 会抛异常，把整轮播报炸掉。
            // 播报是附加品：这里必须降成"不带令牌"，让桥接的 401 走正常的降级路径。
            using (var broken = UnityWebRequestTtsTransport.CreateSynthRequest("http://x/y", "秘密\n令牌", "你好"))
            {
                Assert.IsNull(broken.GetRequestHeader("Authorization"),
                    "非法令牌不带 Authorization，更不能抛异常");
            }
        }

        [Test]
        public void TheAudioRequestOnlyCarriesTheTokenForTheBridgeOrigin()
        {
            const string baseUrl = "http://127.0.0.1:8790";
            const string token = "s3cr3t-token";

            // 桥接自己发出来的 wav 也要 token（web_api.py 的 require_token），同源必须带。
            var relative = TtsUrls.AudioUrl(baseUrl, "/mas3d/api/voice/v1.wav");
            using (var same = UnityWebRequest.Get(relative))
            {
                UnityWebRequestTtsTransport.AuthorizeAudio(same, token, baseUrl, relative);
                Assert.AreEqual("Bearer " + token, same.GetRequestHeader("Authorization"),
                    "同源的音频地址必须带令牌，否则桥接的 wav 端点直接 401");
            }

            using (var absoluteSame = UnityWebRequest.Get("http://127.0.0.1:8790/mas3d/api/voice/v1.wav"))
            {
                UnityWebRequestTtsTransport.AuthorizeAudio(absoluteSame, token, baseUrl, absoluteSame.url);
                Assert.AreEqual("Bearer " + token, absoluteSame.GetRequestHeader("Authorization"),
                    "写成绝对地址的同一个源，判定必须与相对地址一致");
            }

            using (var cdn = UnityWebRequest.Get("https://cdn.example.test/a.wav"))
            {
                UnityWebRequestTtsTransport.AuthorizeAudio(cdn, token, baseUrl, cdn.url);
                Assert.IsNull(cdn.GetRequestHeader("Authorization"),
                    "跨域音频不带 Bearer：音频地址是桥接给的外部输入，别把令牌交给第三方域");
            }

            using (var otherPort = UnityWebRequest.Get("http://127.0.0.1:9999/a.wav"))
            {
                UnityWebRequestTtsTransport.AuthorizeAudio(otherPort, token, baseUrl, otherPort.url);
                Assert.IsNull(otherPort.GetRequestHeader("Authorization"), "端口不同就是不同源");
            }

            Assert.IsFalse(UnityWebRequestTtsTransport.IsSameOrigin(baseUrl, string.Empty), "空地址不当同源");
            Assert.IsFalse(UnityWebRequestTtsTransport.IsSameOrigin("不是地址", "/a.wav"), "解不开的 base 不带令牌");
        }

        [Test]
        public void EveryNonTwoHundredResponseIsADegrade()
        {
            const string ok =
                "{\"ok\":true,\"voice\":{\"id\":\"v1\",\"audioUrl\":\"/mas3d/api/voice/v1.wav\"," +
                "\"durationMs\":1200,\"sentenceStartsMs\":[0,400,800],\"englishText\":\"hi\"}}";

            var voice = UnityWebRequestTtsTransport.ParseSynth(200, ok, null);
            Assert.AreEqual(TtsStatus.Voice, voice.Status);
            Assert.IsNotNull(voice.Voice, "200 必须解出 voice");
            Assert.AreEqual("/mas3d/api/voice/v1.wav", voice.Voice.AudioUrl);
            Assert.AreEqual(1200.0, voice.Voice.DurationMs, 0.001);

            // 400 是"这一句合成不了"（空句/超长/英文句下标越界），不是"播完了"。
            // 以前把它当 End，尾巴就被静默吞掉；整轮既没声音也不降级，违反 brief 的硬规则。
            var badSentence = UnityWebRequestTtsTransport.ParseSynth(400, "{\"ok\":false,\"message\":\"句子下标越界\"}", "HTTP/1.1 400");
            Assert.AreEqual(TtsStatus.Unavailable, badSentence.Status,
                "400 必须走降级，不许当成'没有更多句子'静默收尾");
            StringAssert.Contains("句子下标越界", badSentence.Message, "降级日志要留下服务端那句话");

            var unauthorized = UnityWebRequestTtsTransport.ParseSynth(401, null, "HTTP/1.1 401 Unauthorized");
            Assert.AreEqual(TtsStatus.Unavailable, unauthorized.Status, "401 也是降级：只显示字幕，不抛异常");

            var down = UnityWebRequestTtsTransport.ParseSynth(503,
                "{\"ok\":false,\"code\":\"bridge_down\",\"message\":\"TTS 没起来\"}", "HTTP/1.1 503");
            Assert.AreEqual(TtsStatus.Unavailable, down.Status);
            StringAssert.Contains("TTS 没起来", down.Message, "降级日志要留下服务端那句话，别只剩一个状态码");

            Assert.AreEqual(TtsStatus.Unavailable,
                UnityWebRequestTtsTransport.ParseSynth(0, null, "Cannot connect to destination host").Status,
                "连不上也是降级，不能抛异常");
            Assert.AreEqual(TtsStatus.Unavailable, UnityWebRequestTtsTransport.ParseSynth(200, "not json", null).Status,
                "解不开的回复同样是降级：播放器不该因为一段坏 JSON 崩掉");
        }

        // ---- 切分 ---------------------------------------------------------------

        [Test]
        public void SentencesSplitOnBothChineseAndEnglishTerminators()
        {
            CollectionAssert.AreEqual(
                new[] { "你好！", "Hello there.", "再见？", "Bye." },
                TtsSentences.Split("你好！Hello there. 再见？Bye."),
                "中英混排按两种终止符切，标点留在句尾");

            CollectionAssert.AreEqual(new[] { "只有一句没有标点的话" }, TtsSentences.Split("只有一句没有标点的话"),
                "没有标点也算一句：整段丢掉等于这一轮没有声音");

            CollectionAssert.IsEmpty(TtsSentences.Split("。。。！！"),
                "纯标点不是一句话，送上去只会换回 400");
            CollectionAssert.IsEmpty(TtsSentences.Split("   "));
            CollectionAssert.IsEmpty(TtsSentences.Split(null));
        }

        [Test]
        public void AnOverLongRunIsCutIntoSpeakablePieces()
        {
            var longRun = new string('啊', 450);
            var pieces = TtsSentences.Split(longRun);
            Assert.AreEqual(3, pieces.Length, "450 字按 200 一段切开");
            foreach (var piece in pieces)
            {
                Assert.LessOrEqual(piece.Length, TtsSentences.MaxSentenceChars,
                    "单句必须远低于桥接的 600 字上限，否则长回复会整轮 400");
            }
            Assert.AreEqual(longRun, string.Concat(pieces), "切开之后拼回去必须还是原文，一个字都不能丢");

            var withComma = new string('啊', 150) + "，" + new string('啊', 150);
            var cut = TtsSentences.Split(withComma);
            Assert.AreEqual(2, cut.Length);
            Assert.AreEqual(151, cut[0].Length, "优先在逗号处断句：读出来的停顿才自然");

            // 桥接对超过 600 字的 text 直接 400。700 字必须被切到每段 ≤200，否则这一轮整段没有声音，
            // 而 EditMode 这条是唯一能在不联网的情况下钉住"不会踩到 600"的地方。
            var huge = new string('啊', 700);
            var hugePieces = TtsSentences.Split(huge);
            Assert.AreEqual(4, hugePieces.Length, "700 字按 200 一段切成 4 段");
            foreach (var piece in hugePieces)
            {
                Assert.LessOrEqual(piece.Length, TtsSentences.MaxSentenceChars);
            }
            Assert.AreEqual(huge, string.Concat(hugePieces), "切开之后拼回去必须还是原文");
        }

        [Test]
        public void TheSplitKeepsEverySentenceForTheSubtitleWhileSynthStopsAtForty()
        {
            var many = new System.Text.StringBuilder();
            for (var i = 0; i < 45; i++) many.Append("第").Append(i).Append("句。");

            var sentences = TtsSentences.Split(many.ToString());
            Assert.AreEqual(45, sentences.Length, "切分不截断：字幕要能显示完整回复");
            Assert.AreEqual(40, ReplySpeaker.MaxSentences, "合成最多 40 句，与 web 端 VOICE_MAX_SENTENCES 对齐");
        }

        [Test]
        public void ALongSentenceIsNeverCutThroughTheMiddleOfOneCharacter()
        {
            // 一个 emoji 是两个 UTF-16 code unit：按 200 硬切正好落在代理对中间时，
            // 头一段会以一个孤立的高位代理结尾 —— 送进 TTS 是一串乱码，拼回去也不再是原文。
            var text = new string('啊', 199) + "😀" + new string('哦', 50);
            var pieces = TtsSentences.Split(text);

            Assert.AreEqual(text, string.Concat(pieces), "切开再拼回去必须逐字相同");
            foreach (var piece in pieces)
            {
                Assert.LessOrEqual(piece.Length, TtsSentences.MaxSentenceChars);
                for (var i = 0; i < piece.Length; i++)
                {
                    if (char.IsHighSurrogate(piece[i]))
                    {
                        Assert.IsTrue(i + 1 < piece.Length && char.IsLowSurrogate(piece[i + 1]),
                            "任何一段都不许以孤立代理结尾：" + piece);
                        i++;
                    }
                }
            }
        }

        [Test]
        public void TheDownloadedClipIsCopiedSoItOutlivesTheRequest()
        {
            // GetContent() 交回来的 clip 归下载 handler 所有；handler 一 Dispose，它还活着没有文档保证。
            // 这里钉的是"拷一份自己的"这个动作本身：样本一致、且不是同一个对象。
            var source = AudioClip.Create("downloaded", 256, 1, 24000, false);
            var data = new float[256];
            for (var i = 0; i < data.Length; i++) data[i] = Mathf.Sin(i * 0.05f) * 0.5f;
            source.SetData(data, 0);

            var copy = UnityWebRequestTtsTransport.CopyClip(source);
            try
            {
                Assert.IsNotNull(copy, "未压缩的 WAV 必须拷得动");
                Assert.AreNotSame(source, copy, "必须是一份新的 AudioClip，而不是原对象的引用");
                Assert.AreEqual(source.samples, copy.samples);
                Assert.AreEqual(source.channels, copy.channels);
                Assert.AreEqual(source.frequency, copy.frequency);

                var readBack = new float[copy.samples * copy.channels];
                Assert.IsTrue(copy.GetData(readBack, 0));
                CollectionAssert.AreEqual(data, readBack, "拷出来的样本要一模一样，不能静音或错位");
            }
            finally
            {
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        /// <summary>只给 JsonUtility 解 body 用：测试要验"引号有没有转义对"，不能只看字符串像不像。</summary>
        [Serializable]
        sealed class SynthBody
        {
            public string text;
        }
    }
}
