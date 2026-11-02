// JSON 键映射 —— C# 字段名保持 PascalCase，JSON 键名照 packages/protocol/src/frames.ts，两者只在这里对照，
// 改键名请连这里一起改（解析是手写逐字段的，没有反射或自动映射）：
//   ServerFrame    Type ← type             TurnId ← turnId         Text ← text       Cue ← cue
//                  Voice ← voice           Plan ← plan             Code ← code       Message ← message
//   ExpressionCue  Emotion ← emotion       Intensity ← intensity   Gaze ← gaze       Gesture ← gesture
//   VoiceRef       AudioUrl ← audioUrl     DurationMs ← durationMs SentenceStartsMs ← sentenceStartsMs
//                  EnglishText ← englishText
// Plan / MotionPlan 的键映射见 MotionPlan.cs 顶部。
using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mas3D.Net
{
    /// <summary>
    /// 桥接帧不符合 packages/protocol/src/frames.ts 的契约时抛出。
    /// 消息口径与 TypeScript 侧逐字一致，两个实现靠共享语料钉住。
    /// </summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>reply 帧可选的表情提示。表情推导在前端做，桥接只负责原样搬运。</summary>
    [Serializable]
    public class ExpressionCue
    {
        public string Emotion;
        public float Intensity;
        public string Gaze;
        public string Gesture;
    }

    /// <summary>reply 帧可选的语音引用，时间戳单位都是毫秒。</summary>
    [Serializable]
    public class VoiceRef
    {
        public string AudioUrl;
        public double DurationMs;
        public double[] SentenceStartsMs;
        public string EnglishText;
    }

    /// <summary>
    /// 服务端下行帧的 C# 镜像。五种 type 共用一个类型，按 <see cref="Type"/> 取对应字段：
    /// ack/thinking → TurnId；reply → TurnId/Text/Cue/Voice；intent → TurnId/Plan；error → Code/Message[/TurnId]。
    /// </summary>
    [Serializable]
    public class ServerFrame
    {
        public string Type;
        public string TurnId;
        public string Text;
        public ExpressionCue Cue;
        public VoiceRef Voice;
        public MotionPlan Plan;
        public string Code;
        public string Message;
    }

    /// <summary>按 frames.ts 的规则把一条下行帧 JSON 读成 <see cref="ServerFrame"/>。</summary>
    public static class FrameParser
    {
        static readonly string[] Emotions = { "neutral", "joy", "fun", "sorrow", "angry", "surprised" };
        static readonly string[] Gazes = { "player", "away", "down", "up", "left", "right" };
        static readonly string[] Gestures = { "none", "nod", "shake", "tilt", "wave", "lean_in", "look_away" };
        static readonly string[] ErrorCodes = { "bridge_down", "timeout", "model_error", "bad_request", "rate_limited" };

        /// <summary>
        /// 解析一条下行帧。JSON 语法错误同样抛 <see cref="ProtocolException"/>：
        /// Task 2 的接收循环只 catch 这一种，坏帧要被丢掉而不能掐断连接。
        /// </summary>
        public static ServerFrame Parse(string json)
        {
            if (json == null || json.Trim().Length == 0)
            {
                throw new ProtocolException("frame is not valid JSON: the text is empty");
            }

            JToken raw;
            try
            {
                // DateParseHandling.None is not optional here: Newtonsoft would otherwise turn a
                // string like "2026-11-02T20:13:58Z" into a Date token, while JSON.parse in
                // frames.ts keeps it a string — reply.text must stay the text the bridge sent.
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
                {
                    raw = JToken.ReadFrom(reader);
                    if (reader.Read())
                    {
                        throw new ProtocolException("frame is not valid JSON: additional content after the frame");
                    }
                }
            }
            catch (JsonException e)
            {
                throw new ProtocolException("frame is not valid JSON: " + e.Message);
            }

            return ParseFrame(raw);
        }

        static ServerFrame ParseFrame(JToken raw)
        {
            var obj = raw as JObject;
            if (obj == null) throw new ProtocolException("frame must be an object");

            var typeToken = obj["type"];
            var type = typeToken != null && typeToken.Type == JTokenType.String ? (string)typeToken : null;
            switch (type)
            {
                case "ack":
                    return new ServerFrame { Type = "ack", TurnId = Framing.Str(obj, "turnId", "ack") };

                case "thinking":
                    return new ServerFrame { Type = "thinking", TurnId = Framing.Str(obj, "turnId", "thinking") };

                case "reply":
                {
                    // 顺序照抄 TS：turnId、text，然后可选的 cue、voice。
                    var frame = new ServerFrame
                    {
                        Type = "reply",
                        TurnId = Framing.Str(obj, "turnId", "reply"),
                        Text = Framing.Str(obj, "text", "reply"),
                    };
                    // 缺键才叫“没有”；JSON 的 null 算有这个字段，照常进去校验并报错。
                    if (obj["cue"] != null) frame.Cue = ParseCue(obj["cue"]);
                    if (obj["voice"] != null) frame.Voice = ParseVoice(obj["voice"]);
                    return frame;
                }

                case "intent":
                    return new ServerFrame
                    {
                        Type = "intent",
                        TurnId = Framing.Str(obj, "turnId", "intent"),
                        Plan = MotionPlanRules.Validate(obj["plan"]),
                    };

                case "error":
                {
                    // 顺序照抄 TS：code、message，然后可能没有的 turnId。
                    var frame = new ServerFrame
                    {
                        Type = "error",
                        Code = Framing.OneOf(obj, "code", ErrorCodes, "error"),
                        Message = Framing.Str(obj, "message", "error"),
                    };
                    if (obj["turnId"] != null) frame.TurnId = Framing.Str(obj, "turnId", "error");
                    return frame;
                }

                default:
                    throw new ProtocolException("unknown frame type: " + Framing.Describe(typeToken));
            }
        }

        static ExpressionCue ParseCue(JToken raw)
        {
            var obj = raw as JObject;
            if (obj == null) throw new ProtocolException("cue must be an object");
            // intensity 先判：TS 的 cue() 也是先看强度再看三个词表。
            var intensity = Framing.NumberOrFail(obj["intensity"], "cue.intensity must be within 0..1");
            if (intensity < 0 || intensity > 1) throw new ProtocolException("cue.intensity must be within 0..1");
            return new ExpressionCue
            {
                Emotion = Framing.OneOf(obj, "emotion", Emotions, "cue"),
                Intensity = (float)intensity,
                Gaze = Framing.OneOf(obj, "gaze", Gazes, "cue"),
                Gesture = Framing.OneOf(obj, "gesture", Gestures, "cue"),
            };
        }

        static VoiceRef ParseVoice(JToken raw)
        {
            var obj = raw as JObject;
            if (obj == null) throw new ProtocolException("voice must be an object");

            const string DurationMessage = "voice.durationMs must be a non-negative number";
            var durationMs = Framing.NumberOrFail(obj["durationMs"], DurationMessage);
            if (durationMs < 0) throw new ProtocolException(DurationMessage);

            const string StartsMessage = "voice.sentenceStartsMs must be an array of non-negative numbers";
            var startsToken = obj["sentenceStartsMs"] as JArray;
            if (startsToken == null) throw new ProtocolException(StartsMessage);
            var starts = new double[startsToken.Count];
            for (var i = 0; i < starts.Length; i++)
            {
                var start = Framing.NumberOrFail(startsToken[i], StartsMessage);
                if (start < 0) throw new ProtocolException(StartsMessage);
                starts[i] = start;
            }

            return new VoiceRef
            {
                AudioUrl = Framing.Str(obj, "audioUrl", "voice"),
                DurationMs = durationMs,
                SentenceStartsMs = starts,
                EnglishText = Framing.Str(obj, "englishText", "voice"),
            };
        }
    }

    /// <summary>
    /// frames.ts / plan.ts 里那几个校验小工具的 C# 版：谁不合规就抛 <see cref="ProtocolException"/>，
    /// 消息里带上 "reply.cue.intensity" 这样的路径，前端和日志都能直接看懂。
    /// </summary>
    internal static class Framing
    {
        public static string Str(JObject obj, string key, string where)
        {
            var token = obj[key];
            if (token == null || token.Type != JTokenType.String)
            {
                throw new ProtocolException(where + "." + key + " must be a string");
            }
            return (string)token;
        }

        public static string OneOf(JObject obj, string key, string[] allowed, string where)
        {
            var token = obj[key];
            if (token == null || token.Type != JTokenType.String || Array.IndexOf(allowed, (string)token) < 0)
            {
                throw new ProtocolException(where + "." + key + " is not one of " + string.Join("|", allowed));
            }
            return (string)token;
        }

        /// <summary>取值必须是有限数字，否则用 <paramref name="message"/> 报错（TS 里 num() 与 cue.intensity 的两种口径）。</summary>
        public static double NumberOrFail(JToken token, string message)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                throw new ProtocolException(message);
            }
            var value = (double)token;
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ProtocolException(message);
            return value;
        }

        public static double NumberInRange(JObject obj, string key, double min, double max, string where)
        {
            var value = NumberOrFail(obj[key], where + "." + key + " must be a finite number");
            if (value < min || value > max)
            {
                throw new ProtocolException(
                    where + "." + key + " must be within " + Bound(min) + ".." + Bound(max));
            }
            return value;
        }

        public static bool Bool(JObject obj, string key, string where)
        {
            var token = obj[key];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                throw new ProtocolException(where + "." + key + " must be a boolean");
            }
            return (bool)token;
        }

        /// <summary>数字要按 JS 的写法渲染：0.4 不能写成 "0.40000000000000002"。</summary>
        static string Bound(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>TS 里 `String(raw['type'])` 的效果：缺键是 "undefined"，null 是 "null"。</summary>
        public static string Describe(JToken token)
        {
            if (token == null) return "undefined";
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return "null";
                case JTokenType.String:
                    return (string)token;
                case JTokenType.Boolean:
                    return (bool)token ? "true" : "false";
                case JTokenType.Integer:
                case JTokenType.Float:
                    return Convert.ToString((double)token, CultureInfo.InvariantCulture);
                case JTokenType.Array:
                {
                    var array = (JArray)token;
                    var parts = new string[array.Count];
                    for (var i = 0; i < parts.Length; i++) parts[i] = Describe(array[i]);
                    return string.Join(",", parts);
                }
                default:
                    return "[object Object]";
            }
        }
    }
}
