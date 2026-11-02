using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mas3D.Net
{
    /// <summary>
    /// 客户端上行帧的组帧器。上行只有 say/hello 之类的小帧，键序固定便于日志比对；
    /// turnId 为 null 或空串时不写这个键（frames.ts 那边是可缺省字段）。
    /// </summary>
    public static class ClientFrame
    {
        /// <summary>与 frames.ts 的 MAX_INPUT_CHARS 对齐，输入框截断长度。</summary>
        public const int MaxInputChars = 300;

        public static string Say(string text, string turnId = null)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var frame = new JObject
            {
                { "type", "say" },
                { "text", text },
            };
            if (!string.IsNullOrEmpty(turnId)) frame["turnId"] = turnId;
            return frame.ToString(Formatting.None);
        }
    }
}
