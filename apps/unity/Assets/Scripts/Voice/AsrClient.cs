using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Mas3D.Voice
{
    /// <summary>一次识别成功的结果；字段与桥接 <c>asr</c> 对象的 text/durationMs/model 一一对应。</summary>
    public readonly struct AsrResult
    {
        /// <summary>识别出来的文字；桥接没给就为空串。</summary>
        public readonly string Text;

        /// <summary>音频时长（毫秒），桥接侧算的。</summary>
        public readonly int DurationMs;

        /// <summary>实际加载的 whisper 模型名（small / tiny 兜底）。</summary>
        public readonly string Model;

        public AsrResult(string text, int durationMs, string model)
        {
            Text = text ?? string.Empty;
            DurationMs = durationMs;
            Model = model ?? string.Empty;
        }
    }

    /// <summary>
    /// 到本机桥接 <c>POST /mas3d/api/asr</c> 的 ASR 客户端：把 <see cref="WavEncoder"/> 编好的
    /// 裸 WAV 字节原样发上去，用 Bearer 令牌鉴权。
    ///
    /// 约定：任何失败 —— 传输错误、非 2xx、body 不是 <c>{"ok":true,"asr":{...}}</c>、JSON 坏掉 ——
    /// 都只走 <c>onError</c> 并给一句人话，协程本身不抛异常。UI 层因此不需要 try/catch，
    /// 也不必区分“网络挂了”和“桥接说不行”，两种情况对用户是同一句话。
    ///
    /// 真值来源：Endpoint/Token 都由调用方（VoiceInput）给，这里不读 BridgeSettings.TokenFilePath，
    /// 也不去解析 ws:// 的 Url —— 本类只认 HTTP 端点。
    /// </summary>
    public sealed class AsrClient
    {
        /// <summary>桥接默认端点；VoiceInput 会按 BridgeSettings 覆盖。</summary>
        public const string DefaultEndpoint = "http://127.0.0.1:8790/mas3d/api/asr";

        /// <summary>
        /// 客户端超时，必须比桥接的 <c>ASR_TIMEOUT_S</c>（45 s）长：桥接从读完 body 才开始计时，
        /// 里面还含最多 15 s 的等锁，如果两边同值，Unity 几乎总是先超时 —— 玩家看到的是通用失败，
        /// 桥接那句 504「语音识别超时」永远回不到面板。多留 10 s，让桥接的判据先说话。
        /// </summary>
        public const int TimeoutSeconds = 55;

        /// <summary>完整 HTTP 端点；空串时退回 <see cref="DefaultEndpoint"/>。</summary>
        public string Endpoint = DefaultEndpoint;

        /// <summary>Bearer 令牌；空串表示不带 Authorization 头（只对没开鉴权的 mock 有意义）。</summary>
        public string Token = string.Empty;

        /// <summary>正在飞的那条请求；<see cref="Abort"/> 靠它把连接真的掐掉。</summary>
        UnityWebRequest _active;

        /// <summary>
        /// 中止正在飞的请求（换代、禁用、卸载）。空闲时无操作，可重复调用。
        ///
        /// 只 <c>StopCoroutine</c> 是不够的：协程停下但 <see cref="UnityWebRequest"/> 还在，
        /// 桥接侧的 worker 会继续推理并占着 GPU 锁（<c>asr.py</c> 用 shield 保证锁不提前放），
        /// 紧接着的下一次采集可能等满 15 s 拿到 503。这里把连接掐掉，也让桥接看到断开。
        /// </summary>
        public void Abort()
        {
            var request = _active;
            _active = null;
            if (request == null) return;

            // 先清引用：协程醒来看到 _active 不是自己，就知道这一代作废了，不再回调。
            request.Abort();
        }

        /// <summary>上传一段 WAV 并等识别结果；恰好回调 onOk 或 onError 之一（被 <see cref="Abort"/> 中止时一个都不回调）。</summary>
        public IEnumerator Transcribe(byte[] wav, Action<AsrResult> onOk, Action<string> onError)
        {
            var url = string.IsNullOrEmpty(Endpoint) ? DefaultEndpoint : Endpoint;
            var body = wav ?? Array.Empty<byte>();

            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            _active = request;
            try
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "audio/wav");
                if (!string.IsNullOrEmpty(Token))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + Token);
                }
                request.timeout = TimeoutSeconds;

                yield return request.SendWebRequest();

                // Abort()（或下一次请求接管）之后 _active 已经不是这一条：这一代作废，一个回调都不发。
                // 调用方早就换代了，回调只会变成噪音（甚至把旧文本写进新一回合的框）。
                if (!ReferenceEquals(_active, request)) yield break;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail(onError, DescribeFailure(request));
                    yield break;
                }

                AsrEnvelope envelope;
                try
                {
                    envelope = JsonUtility.FromJson<AsrEnvelope>(request.downloadHandler.text);
                }
                catch (Exception)
                {
                    Fail(onError, "语音识别返回了无法解析的响应（HTTP " + request.responseCode + "）");
                    yield break;
                }

                if (envelope == null)
                {
                    Fail(onError, "语音识别返回了空响应");
                    yield break;
                }

                if (!envelope.ok)
                {
                    // 桥接的 error 是给人看的（中文），原样交给调用方，别二次包装。
                    Fail(onError, string.IsNullOrEmpty(envelope.error) ? "语音识别失败（桥接没给原因）" : envelope.error);
                    yield break;
                }

                // JsonUtility 会把自定义类字段按值内联：JSON 里没有 asr 时它照样造一个空壳，
                // 所以 asr 非 null 不能证明响应合法 —— 桥接总会带 text（空识别是 ""），
                // text 为 null 才是“压根没这个字段”。
                if (envelope.asr == null || envelope.asr.text == null)
                {
                    Fail(onError, "语音识别响应里没有 asr 字段");
                    yield break;
                }

                if (onOk != null)
                {
                    onOk(new AsrResult(envelope.asr.text, envelope.asr.durationMs, envelope.asr.model));
                }
            }
            finally
            {
                if (ReferenceEquals(_active, request)) _active = null;
                request.Dispose();
            }
        }

        static void Fail(Action<string> onError, string message)
        {
            if (onError != null) onError(message);
        }

        static string DescribeFailure(UnityWebRequest request)
        {
            var status = request.responseCode;
            if (status >= 100)
            {
                var detail = TryReadError(request.downloadHandler != null ? request.downloadHandler.text : null);
                return detail == null
                    ? "语音识别请求失败（HTTP " + status + "）"
                    : "语音识别请求失败（HTTP " + status + "）：" + detail;
            }

            var reason = string.IsNullOrEmpty(request.error) ? "连接不上语音识别服务" : request.error;
            return "语音识别请求失败：" + reason;
        }

        /// <summary>
        /// 从失败响应里抠出能给人看的那句原文。两条路由两种形状：
        /// <c>/mas3d/api/asr</c> 自己失败时给 <c>{"ok":false,"error":...}</c>（400/503/504），
        /// 而 <c>require_token</c> 抛的 401 由 fastapi 接管，body 是 <c>{"detail":...}</c>、没有 error。
        /// error 优先，detail 兜底；都不是非空字符串就返回 null（让调用方用通用文案）。
        /// </summary>
        static string TryReadError(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var envelope = JsonUtility.FromJson<AsrEnvelope>(text);
                if (envelope == null) return null;
                if (!string.IsNullOrEmpty(envelope.error)) return envelope.error;
                if (!string.IsNullOrEmpty(envelope.detail)) return envelope.detail;
            }
            catch (Exception)
            {
                // 不是 JSON（例如网关回的 HTML）：没有可透传的原文，用通用文案。
            }
            return null;
        }

        /// <summary>
        /// 桥接的响应信封 <c>{"ok":bool,"asr":{...},"error":string}</c>；只给 JsonUtility 用。
        /// <c>detail</c> 不是桥接的字段，是 fastapi 的 <c>HTTPException</c> 形状（401 鉴权失败走这条），
        /// 只有 error 缺席时才读它。
        /// </summary>
        [Serializable]
        sealed class AsrEnvelope
        {
            public bool ok;
            public AsrPayload asr;
            public string error;
            public string detail;
        }

        /// <summary>信封里的 asr 对象。</summary>
        [Serializable]
        sealed class AsrPayload
        {
            public string text;
            public int durationMs;
            public string model;
        }
    }
}
