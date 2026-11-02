// JSON 键映射 —— C# 字段名保持 PascalCase，JSON 键名照 packages/protocol/src/plan.ts，两者只在这里对照，
// 改键名请连这里一起改（没有任何自动映射：解析是手写逐字段的）：
//   MotionPlan    Base ← base       Reply ← reply      Operation ← operation
//                 Timing ← timing   Intents ← intents
//   MotionBase    Change ← change   Name ← name        Motion ← motion       Rest ← rest
//   MotionIntent  Key ← key         Speed ← speed      Strength ← strength
//                 Hold ← hold       Delay ← delay
using System;
using Newtonsoft.Json.Linq;

namespace Mas3D.Net
{
    /// <summary>plan.intents 里的一条动作指令，数值范围见 plan.ts。</summary>
    [Serializable]
    public class MotionIntent
    {
        public string Key;
        public float Speed;
        public float Strength;
        public float Hold;
        public float Delay;
    }

    /// <summary>plan.base：换不换基准姿势，换的话换到哪个。</summary>
    [Serializable]
    public class MotionBase
    {
        public bool Change;
        public string Name;
        public float Motion;
        public bool Rest;
    }

    /// <summary>服务端下发的动作计划，与 packages/protocol/src/plan.ts 的 MotionPlan 一一对应。</summary>
    [Serializable]
    public class MotionPlan
    {
        public MotionBase Base;
        public string Reply;
        public string Operation;
        public string Timing;
        public MotionIntent[] Intents;
    }

    /// <summary>
    /// plan.ts 的校验规则搬到 C#：顺序、文案都照抄，任何不合规抛 <see cref="ProtocolException"/>。
    /// </summary>
    public static class MotionPlanRules
    {
        public const int MaxIntents = 4;
        public const int MaxBaseName = 40;
        public const int MaxReply = 600;

        public static readonly string[] Operations = { "chat", "command", "release", "unsupported" };
        public static readonly string[] Timings = { "同时", "依次" };

        const double SpeedMin = 0.4;
        const double SpeedMax = 2;
        const double StrengthMin = 0;
        const double StrengthMax = 1;
        const double HoldMin = 0;
        const double HoldMax = 20;
        const double DelayMin = 0;
        const double DelayMax = 30;

        public static MotionPlan Validate(JToken raw)
        {
            var obj = raw as JObject;
            if (obj == null) throw new ProtocolException("plan must be an object");

            var operation = Framing.OneOf(obj, "operation", Operations, "plan");

            var timingToken = obj["timing"];
            var timing = timingToken != null && timingToken.Type == JTokenType.String ? (string)timingToken : null;
            if (timing == null || Array.IndexOf(Timings, timing) < 0)
            {
                throw new ProtocolException("plan.timing must be 同时 or 依次");
            }

            var intentsToken = obj["intents"] as JArray;
            if (intentsToken == null) throw new ProtocolException("plan.intents must be an array");
            if (intentsToken.Count > MaxIntents)
            {
                throw new ProtocolException("plan.intents must have at most " + MaxIntents + " entries");
            }

            var planBase = ParseBase(obj["base"]);

            var intents = new MotionIntent[intentsToken.Count];
            for (var i = 0; i < intents.Length; i++) intents[i] = ParseIntent(intentsToken[i], i);

            if (operation == "command")
            {
                if (intents.Length == 0 && !planBase.Change)
                {
                    throw new ProtocolException("a command plan needs at least one intent or a base change");
                }
            }
            else if (intents.Length > 0)
            {
                throw new ProtocolException(operation + " plan must not carry intents");
            }

            var plan = new MotionPlan
            {
                Base = planBase,
                Operation = operation,
                Timing = timing,
                Intents = intents,
            };

            // reply 的 null 与缺失等价：Python 侧 validate_plan 也这么判，跨语言契约不许分叉。
            var replyToken = obj["reply"];
            if (replyToken != null && replyToken.Type != JTokenType.Null)
            {
                if (replyToken.Type != JTokenType.String) throw new ProtocolException("plan.reply must be a string");
                var reply = (string)replyToken;
                if (reply.Length > MaxReply)
                {
                    throw new ProtocolException("plan.reply must be at most " + MaxReply + " characters");
                }
                plan.Reply = reply;
            }

            return plan;
        }

        static MotionBase ParseBase(JToken raw)
        {
            var obj = raw as JObject;
            if (obj == null) throw new ProtocolException("base must be an object");

            var nameToken = obj["name"];
            if (nameToken == null || nameToken.Type != JTokenType.String || ((string)nameToken).Length == 0)
            {
                throw new ProtocolException("base.name must be a non-empty string");
            }
            var name = (string)nameToken;
            if (name.Length > MaxBaseName)
            {
                throw new ProtocolException("base.name must be at most " + MaxBaseName + " characters");
            }

            return new MotionBase
            {
                Change = Framing.Bool(obj, "change", "base"),
                Name = name,
                Motion = (float)Framing.NumberInRange(obj, "motion", 0, 1, "base"),
                Rest = Framing.Bool(obj, "rest", "base"),
            };
        }

        static MotionIntent ParseIntent(JToken raw, int index)
        {
            var where = "intents[" + index + "]";
            var obj = raw as JObject;
            if (obj == null) throw new ProtocolException(where + " must be an object");

            var keyToken = obj["key"];
            if (keyToken == null || keyToken.Type != JTokenType.String || ((string)keyToken).Length == 0)
            {
                throw new ProtocolException(where + ".key must be a non-empty string");
            }

            return new MotionIntent
            {
                Key = (string)keyToken,
                Speed = (float)Framing.NumberInRange(obj, "speed", SpeedMin, SpeedMax, where),
                Strength = (float)Framing.NumberInRange(obj, "strength", StrengthMin, StrengthMax, where),
                Hold = (float)Framing.NumberInRange(obj, "hold", HoldMin, HoldMax, where),
                Delay = (float)Framing.NumberInRange(obj, "delay", DelayMin, DelayMax, where),
            };
        }
    }
}
