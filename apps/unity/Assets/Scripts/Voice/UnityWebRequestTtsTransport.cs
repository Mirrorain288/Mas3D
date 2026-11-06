using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Mas3D.Voice
{
    /// <summary>
    /// 真实现：只走 UnityWebRequest（不引第三方 HTTP 库，也不需要 System.Net 的线程/回调适配）。
    ///
    /// 三个静态方法（建请求、拼 body、解结果）单独抽出来是为了能在 EditMode 里直接验：
    /// 请求头、JSON 转义、非 2xx 的判据错了会静默播错，比播不出来更难发现。
    /// </summary>
    public sealed class UnityWebRequestTtsTransport : ITtsTransport
    {
        public string BaseUrl = "http://127.0.0.1:8790";
        public string Token = string.Empty;
        public int TimeoutSeconds = 30;

        /// <summary>
        /// 每次只合成传进来的这一句，body 只有 {"text": ...}。
        /// 不带 sentence 下标：桥接的下标是英文句的，中文句数一对不上就会少播或 400。
        /// </summary>
        public IEnumerator Synth(string text, Action<TtsReply> done)
        {
            using (var request = CreateSynthRequest(TtsUrls.SynthUrl(BaseUrl), Token, text))
            {
                request.timeout = TimeoutSeconds;
                yield return request.SendWebRequest();

                var status = (long)request.responseCode;
                var body = request.downloadHandler != null ? request.downloadHandler.text : null;
                done(ParseSynth(status, body, request.error));
            }
        }

        public IEnumerator LoadAudio(string url, Action<AudioClip> onOk, Action<string> onFail)
        {
            if (string.IsNullOrEmpty(url))
            {
                onFail("音频地址为空");
                yield break;
            }

            using (var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV))
            {
                // 令牌只发给桥接自己：音频地址是桥接给的外部输入，绝对地址可能指向别处。
                AuthorizeAudio(request, Token, BaseUrl, url);
                request.timeout = TimeoutSeconds;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    onFail(request.error ?? "音频下载失败");
                    yield break;
                }

                var downloaded = DownloadHandlerAudioClip.GetContent(request);
                if (downloaded == null)
                {
                    onFail("音频解不开（不是可用的 WAV）");
                    yield break;
                }

                // 下面这个 using 一结束，request 与它的 handler 就被销毁；GetContent() 交回来的 clip
                // 归谁、死没死，Unity 没有承诺（它可能只是 handler 的视图）。播到一半变哑是最难查的那种
                // 故障，所以拷一份自己拥有的样本再交出去；拷不动（压缩格式）才退回原件，保持旧行为。
                onOk(CopyClip(downloaded) ?? downloaded);
            }
        }

        /// <summary>
        /// 把下载来的 clip 拷成一份独立样本。WAV 是未压缩的，GetData 拿得到；拿不到返回 null，
        /// 调用方退回原件。
        /// </summary>
        public static AudioClip CopyClip(AudioClip source)
        {
            if (source == null || source.samples <= 0 || source.channels <= 0) return null;
            var buffer = new float[source.samples * source.channels];
            if (!source.GetData(buffer, 0)) return null;

            var copy = AudioClip.Create(source.name + "-owned", source.samples, source.channels, source.frequency, false);
            if (copy == null) return null;
            copy.SetData(buffer, 0);
            return copy;
        }

        /// <summary>建一个 synth 请求。测试直接拿它验方法/头/body，不必真的发出去。</summary>
        public static UnityWebRequest CreateSynthRequest(string url, string token, string text)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(Body(text)));
            request.downloadHandler = new DownloadHandlerBuffer();
            Authorize(request, token);
            request.SetRequestHeader("Content-Type", "application/json");   // fastapi 按 JSON 解 body
            return request;
        }

        /// <summary>
        /// 请求 body：只有 {"text": "这一句"}。用 Newtonsoft 拼，引号/换行/反斜杠才转义得对。
        /// 没有 sentence 字段——服务端拿到什么就合成什么，不需要我们自己算英文句下标。
        /// </summary>
        public static string Body(string text)
        {
            var body = new JObject
            {
                ["text"] = text ?? string.Empty,
            };
            return body.ToString(Formatting.None);
        }

        /// <summary>带令牌。空令牌不带 Authorization：空的 Bearer 只会换回 401。</summary>
        public static void Authorize(UnityWebRequest request, string token)
        {
            if (request == null) return;
            var trimmed = token == null ? string.Empty : token.Trim();
            if (trimmed.Length == 0) return;

            // SetRequestHeader 遇到非 ASCII 会直接抛 "Header value contains invalid characters"。
            // 令牌文件是外部输入（有人手改、编辑器存成别的编码），一声不响把整轮播报炸掉是最坏的结果：
            // 这里让开，请求照发、桥接回 401、播报按"语音暂时不可用"降级 —— 至少字幕还在。
            if (!IsHeaderSafe(trimmed))
            {
                Debug.Log("[voice] 令牌含非法字符，本次不带 Authorization（" + trimmed.Length + " 字符）");
                return;
            }

            request.SetRequestHeader("Authorization", "Bearer " + trimmed);
        }

        /// <summary>
        /// 音频请求带令牌的条件：与桥接同源（scheme/host/port 全同）。
        ///
        /// 音频地址是桥接返回的（可能以后换成 CDN 的绝对地址）。把 Bearer 发给外部域，
        /// 等于把桥接的令牌顺手交给了第三方；跨域时宁可不带——真需要鉴权的 CDN 应该用签名 URL。
        /// </summary>
        public static void AuthorizeAudio(UnityWebRequest request, string token, string baseUrl, string audioUrl)
        {
            if (!IsSameOrigin(baseUrl, audioUrl)) return;
            Authorize(request, token);
        }

        /// <summary>音频地址与基础地址是否同源。相对地址先按基础地址解析；解析不了就当作跨域。</summary>
        public static bool IsSameOrigin(string baseUrl, string audioUrl)
        {
            Uri baseUri;
            if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out baseUri)) return false;
            if (string.IsNullOrWhiteSpace(audioUrl)) return false;

            Uri audioUri;
            if (!Uri.TryCreate(audioUrl.Trim(), UriKind.Absolute, out audioUri)
                && !Uri.TryCreate(baseUri, audioUrl.Trim(), out audioUri))
            {
                return false;
            }

            return string.Equals(baseUri.Scheme, audioUri.Scheme, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(baseUri.Host, audioUri.Host, StringComparison.OrdinalIgnoreCase)
                   && baseUri.Port == audioUri.Port;
        }

        /// <summary>HTTP 头只能放可见 ASCII；这也顺带挡掉换行注入。</summary>
        static bool IsHeaderSafe(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c < 0x20 || c > 0x7E) return false;
            }
            return true;
        }

        /// <summary>
        /// 解 synth 的回复：2xx 且能解出 voice 才算成功；其它一切（400 超长/空句、401、404、503、
        /// 连不上、坏 JSON）都降级，不抛异常。
        ///
        /// 400 不再当"没有更多句子"：我们一次只送一句，400 意味着这一句合成不了（也可能整轮都被拒），
        /// 把它当 End 会让尾巴静默消失，既不播也不提示。
        /// </summary>
        public static TtsReply ParseSynth(long status, string body, string transportError)
        {
            if (status < 200 || status > 299) return TtsReply.Unavailable(DescribeFailure(status, body, transportError));

            JObject parsed;
            try
            {
                parsed = string.IsNullOrEmpty(body) ? null : JObject.Parse(body);
            }
            catch (JsonException)
            {
                return TtsReply.Unavailable("语音回复解不开：" + Shorten(body));
            }

            var voice = parsed == null ? null : parsed["voice"] as JObject;
            if (voice == null) return TtsReply.Unavailable("语音回复里没有 voice");

            return TtsReply.VoiceOf(new Mas3D.Net.VoiceRef
            {
                AudioUrl = (string)voice["audioUrl"],
                DurationMs = voice["durationMs"] == null ? 0.0 : (double)voice["durationMs"],
                EnglishText = (string)voice["englishText"] ?? string.Empty,
            });
        }

        static string DescribeFailure(long status, string body, string transportError)
        {
            var message = ExtractMessage(body);
            if (!string.IsNullOrEmpty(message)) return message;
            if (!string.IsNullOrEmpty(transportError)) return transportError;
            return status > 0 ? "语音服务返回 HTTP " + status : "语音服务暂时连不上";
        }

        static string ExtractMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var parsed = JObject.Parse(body);
                return (string)parsed["message"] ?? (string)parsed["detail"];
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= 80 ? text : text.Substring(0, 80) + "…";
        }
    }
}
