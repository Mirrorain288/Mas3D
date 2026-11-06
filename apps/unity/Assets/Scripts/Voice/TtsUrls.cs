using System;
using Mas3D.Net;

namespace Mas3D.Voice
{
    /// <summary>
    /// 语音那两段 HTTP 地址的拼法（纯函数，EditMode 里逐条钉死）。
    ///
    /// 为什么要有它：桥接给的是 ws 地址（<see cref="BridgeSettings.DefaultUrl"/> 形如
    /// ws://127.0.0.1:8790/mas3d/ws），语音接口却是普通 HTTP。把换算与拼接写成纯函数，
    /// 就能在没有网络、没有 Unity 播放循环的地方验；错在字符串上，比错在一次真实请求上便宜得多。
    /// </summary>
    public static class TtsUrls
    {
        /// <summary>合成入口。与 apps/bridge/web_api.py 的路由一致。</summary>
        public const string SynthPath = "/mas3d/api/voice/synth";

        /// <summary>连默认地址都解不开时的最后兜底：宁可打不通，也别抛异常把整轮回复拖掉。</summary>
        const string LastResortBase = "http://127.0.0.1:8790";

        /// <summary>ws/wss 地址换成 http/https 的基础地址：保留 host 与端口，丢掉路径。</summary>
        public static string BaseFrom(string wsUrl)
        {
            var url = string.IsNullOrWhiteSpace(wsUrl) ? BridgeSettings.DefaultUrl : wsUrl.Trim();
            if (TryConvert(url, out var converted)) return converted;
            // 配置写坏了：回落到默认端点。播报只是附加，不该因为一个写错的地址让整轮回复失败。
            if (TryConvert(BridgeSettings.DefaultUrl, out var fallback)) return fallback;
            return LastResortBase;
        }

        /// <summary>合成接口的绝对地址。</summary>
        public static string SynthUrl(string baseUrl)
        {
            return TrimEnd(baseUrl) + SynthPath;
        }

        /// <summary>
        /// 音频地址：桥接给的是相对路径（/mas3d/api/voice/xxx.wav），接在基础地址后面；
        /// 已经是绝对 http(s) 地址就原样用 —— 以后音频换到 CDN 时不该被拼坏。
        /// </summary>
        public static string AudioUrl(string baseUrl, string audioUrl)
        {
            var path = audioUrl == null ? string.Empty : audioUrl.Trim();
            if (path.Length == 0) return string.Empty;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            return TrimEnd(baseUrl) + "/" + path.TrimStart('/');
        }

        static bool TryConvert(string url, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(url)) return false;

            string scheme;
            string rest;
            if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "http://";
                rest = url.Substring(5);
            }
            else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                // 加密通道不能降级成明文：wss 对 https。
                scheme = "https://";
                rest = url.Substring(6);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "http://";
                rest = url.Substring(7);
            }
            else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "https://";
                rest = url.Substring(8);
            }
            else
            {
                return false;
            }

            var stop = rest.IndexOfAny(new[] { '/', '?' });
            if (stop >= 0) rest = rest.Substring(0, stop);
            if (rest.Length == 0) return false;

            result = scheme + rest;
            return true;
        }

        static string TrimEnd(string url)
        {
            var value = url == null ? string.Empty : url.Trim();
            return value.TrimEnd('/');
        }
    }
}
