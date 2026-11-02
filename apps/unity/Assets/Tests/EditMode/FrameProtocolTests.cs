using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mas3D.Net;
using NUnit.Framework;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 1 of the unity-bridge-channel plan: this C# mirror of packages/protocol/src/frames.ts and
    /// plan.ts has to judge every frame exactly the way the TypeScript parser does. Both sides read the
    /// same corpus (packages/protocol/test/fixtures/frames-cases.json), so a divergence shows up here
    /// instead of in a running game — Task 2's receive loop and Task 3's state machine both trust it.
    /// The corpus is read with the tiny reader below instead of Newtonsoft: the assembly under test may
    /// use Newtonsoft (the com.unity.nuget.newtonsoft-json package supplies it), but the test assembly
    /// must not depend on it, or a package swap would break the tests that prove the swap is safe.
    /// </summary>
    public class FrameProtocolTests
    {
        // Application.dataPath is <repo>/apps/unity/Assets, so three parents up is the repo root.
        const string CorpusRelativePath = "packages/protocol/test/fixtures/frames-cases.json";

        /// <summary>One corpus entry: the expectations plus the frame JSON exactly as the bridge would send it.</summary>
        sealed class CorpusCase
        {
            public string Name;
            public bool Ok;
            public string Error;
            public JsonNode Frame;
            public string FrameJson;
        }

        static string CorpusPath()
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", CorpusRelativePath));
            if (!File.Exists(path)) throw new FileNotFoundException("shared frames corpus missing: " + path, path);
            return path;
        }

        static List<CorpusCase> CorpusCases()
        {
            var path = CorpusPath();
            var text = File.ReadAllText(path, Encoding.UTF8);
            var root = MiniJson.Parse(text);
            var cases = root.Kind == "object" ? MiniJson.Member(root, "cases") : null;
            if (cases == null || cases.Kind != "array")
            {
                throw new InvalidDataException("frames-cases.json has no \"cases\" array: " + path);
            }

            var result = new List<CorpusCase>(cases.Items.Count);
            foreach (var item in cases.Items)
            {
                var name = MiniJson.String(item, "name");
                var ok = MiniJson.Bool(item, "ok");
                var frame = MiniJson.Member(item, "frame");
                if (string.IsNullOrEmpty(name)) throw new InvalidDataException("corpus case without a name in " + path);
                if (!ok.HasValue) throw new InvalidDataException("corpus case without an ok flag: " + name);
                if (frame == null) throw new InvalidDataException("corpus case without a frame: " + name);
                result.Add(new CorpusCase
                {
                    Name = name,
                    Ok = ok.Value,
                    Error = MiniJson.String(item, "error"),
                    Frame = frame,
                    // Slice the original text: no re-serialization, so the parser sees the bridge's bytes.
                    FrameJson = text.Substring(frame.Start, frame.End - frame.Start),
                });
            }
            return result;
        }

        static CorpusCase FindCase(string name)
        {
            foreach (var item in CorpusCases())
            {
                if (item.Name == name) return item;
            }
            throw new AssertionException("corpus case disappeared while running: " + name);
        }

        static IEnumerable<TestCaseData> CorpusTestCases()
        {
            foreach (var item in CorpusCases())
            {
                yield return new TestCaseData(item.Name, item.FrameJson, item.Ok, item.Error)
                    .SetName("frames corpus: " + item.Name);
            }
        }

        [Test]
        public void CorpusKeepsTheContractShape()
        {
            var cases = CorpusCases();
            Assert.GreaterOrEqual(cases.Count, 14, "the shared frames corpus must keep at least 14 cases");

            var passing = 0;
            var failing = 0;
            foreach (var item in cases)
            {
                Assert.IsNotEmpty(item.Name, "every corpus case needs a name");
                Assert.IsNotEmpty(item.FrameJson, item.Name + ": the case carries no frame JSON");
                if (item.Ok)
                {
                    passing++;
                    continue;
                }
                failing++;
                // FIX 1: an ok:false case without the TypeScript wording cannot pin the C# message.
                Assert.IsNotEmpty(item.Error, "a failing corpus case must record the TS error wording: " + item.Name);
            }
            Assert.GreaterOrEqual(passing, 13, "the corpus must keep the 13 passing cases");
            Assert.GreaterOrEqual(failing, 18, "the corpus must keep the 18 failing cases, MotionPlan rules included");
        }

        [TestCaseSource(nameof(CorpusTestCases))]
        public void MatchesTheSharedCorpus(string name, string frameJson, bool ok, string expectedError)
        {
            var item = FindCase(name);

            if (ok)
            {
                var frame = FrameParser.Parse(frameJson);
                AssertRoundTrips(name, item.Frame, frame);
                return;
            }

            var error = Assert.Throws<ProtocolException>(
                () => FrameParser.Parse(frameJson),
                name + ": a bad frame must raise ProtocolException");
            Assert.IsNotEmpty(expectedError, name + ": a failing corpus case must record the TS error wording");
            Assert.AreEqual(expectedError, error.Message,
                name + ": the C# message drifted from the TS one (got \"" + error.Message + "\")");
        }

        /// <summary>
        /// FIX 2a: a passing case proves nothing if the parser only returns "some frame". Every field the
        /// corpus supplies has to come back equal, and every field it omits has to stay null.
        /// </summary>
        static void AssertRoundTrips(string name, JsonNode expected, ServerFrame frame)
        {
            Assert.IsNotNull(frame, name + ": the parser returned no frame");
            Assert.AreEqual(MiniJson.String(expected, "type"), frame.Type, name + ": type");

            var turnId = MiniJson.String(expected, "turnId");
            if (turnId == null) Assert.IsNull(frame.TurnId, name + ": the corpus has no turnId");
            else Assert.AreEqual(turnId, frame.TurnId, name + ": turnId");

            var text = MiniJson.String(expected, "text");
            if (text == null) Assert.IsNull(frame.Text, name + ": the corpus has no text");
            else Assert.AreEqual(text, frame.Text, name + ": text");

            var cue = MiniJson.Member(expected, "cue");
            if (cue == null)
            {
                Assert.IsNull(frame.Cue, name + ": the corpus has no cue");
            }
            else
            {
                Assert.IsNotNull(frame.Cue, name + ": the cue survived parsing");
                Assert.AreEqual(MiniJson.String(cue, "emotion"), frame.Cue.Emotion, name + ": cue.emotion");
                var intensity = MiniJson.Number(cue, "intensity");
                Assert.IsTrue(intensity.HasValue, name + ": the corpus cue carries no intensity");
                Assert.AreEqual(intensity.Value, (double)frame.Cue.Intensity, 1e-6, name + ": cue.intensity");
                Assert.AreEqual(MiniJson.String(cue, "gaze"), frame.Cue.Gaze, name + ": cue.gaze");
                Assert.AreEqual(MiniJson.String(cue, "gesture"), frame.Cue.Gesture, name + ": cue.gesture");
            }

            var voice = MiniJson.Member(expected, "voice");
            if (voice == null)
            {
                Assert.IsNull(frame.Voice, name + ": the corpus has no voice");
            }
            else
            {
                Assert.IsNotNull(frame.Voice, name + ": the voice survived parsing");
                Assert.AreEqual(MiniJson.String(voice, "audioUrl"), frame.Voice.AudioUrl, name + ": voice.audioUrl");
                var duration = MiniJson.Number(voice, "durationMs");
                Assert.IsTrue(duration.HasValue, name + ": the corpus voice carries no durationMs");
                Assert.AreEqual(duration.Value, frame.Voice.DurationMs, 1e-9, name + ": voice.durationMs");
                Assert.AreEqual(MiniJson.String(voice, "englishText"), frame.Voice.EnglishText, name + ": voice.englishText");

                var starts = MiniJson.Member(voice, "sentenceStartsMs");
                Assert.IsNotNull(starts, name + ": the corpus voice carries no sentenceStartsMs");
                Assert.IsNotNull(frame.Voice.SentenceStartsMs, name + ": sentenceStartsMs survived parsing");
                Assert.AreEqual(starts.Items.Count, frame.Voice.SentenceStartsMs.Length, name + ": sentenceStartsMs length");
                for (var i = 0; i < starts.Items.Count; i++)
                {
                    var start = MiniJson.ToNumber(starts.Items[i]);
                    Assert.AreEqual(start, frame.Voice.SentenceStartsMs[i], 1e-9, name + ": sentenceStartsMs[" + i + "]");
                }
            }

            var plan = MiniJson.Member(expected, "plan");
            if (plan == null)
            {
                Assert.IsNull(frame.Plan, name + ": the corpus has no plan");
            }
            else
            {
                Assert.IsNotNull(frame.Plan, name + ": the plan survived parsing");
                Assert.AreEqual(MiniJson.String(plan, "operation"), frame.Plan.Operation, name + ": plan.operation");
                Assert.AreEqual(MiniJson.String(plan, "timing"), frame.Plan.Timing, name + ": plan.timing");

                var planBase = MiniJson.Member(plan, "base");
                Assert.IsNotNull(planBase, name + ": the corpus plan carries no base");
                Assert.IsNotNull(frame.Plan.Base, name + ": plan.base survived parsing");
                Assert.AreEqual(MiniJson.String(planBase, "name"), frame.Plan.Base.Name, name + ": plan.base.name");
                var change = MiniJson.Bool(planBase, "change");
                Assert.IsTrue(change.HasValue, name + ": the corpus base carries no change flag");
                Assert.AreEqual(change.Value, frame.Plan.Base.Change, name + ": plan.base.change");

                var intents = MiniJson.Member(plan, "intents");
                Assert.IsNotNull(intents, name + ": the corpus plan carries no intents");
                Assert.AreEqual("array", intents.Kind, name + ": plan.intents is not an array");
                Assert.IsNotNull(frame.Plan.Intents, name + ": plan.intents survived parsing");
                Assert.AreEqual(intents.Items.Count, frame.Plan.Intents.Length, name + ": plan.intents length");
                for (var i = 0; i < intents.Items.Count; i++)
                {
                    Assert.AreEqual(MiniJson.String(intents.Items[i], "key"), frame.Plan.Intents[i].Key, name + ": intents[" + i + "].key");
                }

                // reply: absent and JSON null both mean "no reply", exactly like plan.ts.
                var reply = MiniJson.Member(plan, "reply");
                var replyText = reply == null || reply.Kind == "null" ? null : reply.Text;
                Assert.AreEqual(replyText, frame.Plan.Reply, name + ": plan.reply");
            }

            var code = MiniJson.String(expected, "code");
            if (code != null) Assert.AreEqual(code, frame.Code, name + ": code");
            var message = MiniJson.String(expected, "message");
            if (message != null) Assert.AreEqual(message, frame.Message, name + ": message");
        }

        [Test]
        public void ParsesAckWithoutTheOptionalFields()
        {
            var frame = FrameParser.Parse("{\"type\":\"ack\",\"turnId\":\"t0\"}");
            Assert.AreEqual("ack", frame.Type);
            Assert.AreEqual("t0", frame.TurnId);
            Assert.IsNull(frame.Text);
            Assert.IsNull(frame.Cue);
            Assert.IsNull(frame.Voice);
            Assert.IsNull(frame.Plan);
            Assert.IsNull(frame.Code);
        }

        [Test]
        public void ParsesCueFields()
        {
            var frame = FrameParser.Parse(
                "{\"type\":\"reply\",\"turnId\":\"t1\",\"text\":\"晚上好呀。\",\"cue\":{\"emotion\":\"joy\",\"intensity\":0.6,\"gaze\":\"player\",\"gesture\":\"wave\"}}");
            Assert.AreEqual("reply", frame.Type);
            Assert.AreEqual("t1", frame.TurnId);
            Assert.AreEqual("晚上好呀。", frame.Text);
            Assert.IsNotNull(frame.Cue, "the cue survived parsing");
            Assert.AreEqual("joy", frame.Cue.Emotion);
            Assert.AreEqual(0.6, (double)frame.Cue.Intensity, 1e-6, "cue intensity");
            Assert.AreEqual("player", frame.Cue.Gaze);
            Assert.AreEqual("wave", frame.Cue.Gesture);
            Assert.IsNull(frame.Voice, "a cue-only frame carries no voice");
            Assert.IsNull(frame.Plan, "a reply frame carries no plan");
        }

        [Test]
        public void ParsesVoiceFields()
        {
            var frame = FrameParser.Parse(
                "{\"type\":\"reply\",\"turnId\":\"t2\",\"text\":\"晚上好。\",\"voice\":{\"audioUrl\":\"/mas3d/api/voice/v1/audio\",\"durationMs\":1800,\"sentenceStartsMs\":[0,720],\"englishText\":\"Good evening.\"}}");
            Assert.IsNotNull(frame.Voice, "the voice survived parsing");
            Assert.AreEqual("/mas3d/api/voice/v1/audio", frame.Voice.AudioUrl);
            Assert.AreEqual(1800.0, frame.Voice.DurationMs, 1e-9, "voice duration");
            Assert.AreEqual(2, frame.Voice.SentenceStartsMs.Length);
            Assert.AreEqual(0.0, frame.Voice.SentenceStartsMs[0], 1e-9);
            Assert.AreEqual(720.0, frame.Voice.SentenceStartsMs[1], 1e-9);
            Assert.AreEqual("Good evening.", frame.Voice.EnglishText);
        }

        [Test]
        public void ParsesCommandPlanFields()
        {
            var frame = FrameParser.Parse(
                "{\"type\":\"intent\",\"turnId\":\"t3\",\"plan\":{\"base\":{\"change\":true,\"name\":\"前倾\",\"motion\":1,\"rest\":false},\"operation\":\"command\",\"timing\":\"依次\",\"intents\":[{\"key\":\"right-挥手打招呼\",\"speed\":1,\"strength\":0.8,\"hold\":2,\"delay\":0}]}}");
            Assert.AreEqual("intent", frame.Type);
            Assert.IsNotNull(frame.Plan, "the plan survived parsing");
            Assert.AreEqual("command", frame.Plan.Operation);
            Assert.AreEqual("依次", frame.Plan.Timing);
            Assert.IsNull(frame.Plan.Reply, "an absent reply stays null");
            Assert.IsNotNull(frame.Plan.Base, "the plan carries a base");
            Assert.IsTrue(frame.Plan.Base.Change);
            Assert.AreEqual("前倾", frame.Plan.Base.Name);
            Assert.AreEqual(1.0, (double)frame.Plan.Base.Motion, 1e-6);
            Assert.IsFalse(frame.Plan.Base.Rest);
            Assert.AreEqual(1, frame.Plan.Intents.Length);
            var intent = frame.Plan.Intents[0];
            Assert.AreEqual("right-挥手打招呼", intent.Key);
            Assert.AreEqual(1.0, (double)intent.Speed, 1e-6);
            Assert.AreEqual(0.8, (double)intent.Strength, 1e-6);
            Assert.AreEqual(2.0, (double)intent.Hold, 1e-6);
            Assert.AreEqual(0.0, (double)intent.Delay, 1e-6);
        }

        [Test]
        public void ParsesErrorFrameWithoutATurnId()
        {
            var frame = FrameParser.Parse("{\"type\":\"error\",\"code\":\"bridge_down\",\"message\":\"桥接没在跑。\"}");
            Assert.AreEqual("error", frame.Type);
            Assert.IsNull(frame.TurnId, "an error frame may arrive without a turnId");
            Assert.AreEqual("bridge_down", frame.Code);
            Assert.AreEqual("桥接没在跑。", frame.Message);
        }

        [Test]
        public void ReplyWithAnUnknownFieldStillParses()
        {
            var frame = FrameParser.Parse("{\"type\":\"reply\",\"turnId\":\"t\",\"text\":\"嗯\",\"extra\":1}");
            Assert.AreEqual("reply", frame.Type);
            Assert.AreEqual("t", frame.TurnId);
            Assert.AreEqual("嗯", frame.Text);
        }

        [Test]
        public void RejectsMalformedJsonAsAProtocolError()
        {
            // Task 2's receive loop catches ProtocolException per frame and keeps the socket open, so
            // syntax errors must not escape as JsonReaderException.
            Assert.Throws<ProtocolException>(() => FrameParser.Parse("{\"type\":"));
            Assert.Throws<ProtocolException>(() => FrameParser.Parse(string.Empty));
            Assert.Throws<ProtocolException>(() => FrameParser.Parse(null));
            // JSON.parse rejects trailing content too ("Unexpected non-whitespace character after JSON").
            Assert.Throws<ProtocolException>(() => FrameParser.Parse("{\"type\":\"ack\",\"turnId\":\"t\"} {}"));
        }

        [Test]
        public void TimestampLookingTextStaysText()
        {
            // Newtonsoft stores an ISO-8601 string as a Date token unless DateParseHandling is off,
            // while JSON.parse in frames.ts keeps it a string. A reply that mentions a date and time
            // must arrive as the exact text the bridge sent, not as a reformatted local timestamp.
            // A text that is ONLY an ISO timestamp is the dangerous shape: Newtonsoft's default
            // DateParseHandling turns it into a Date token, so reply.text would be rejected outright.
            const string ExactTimestamp = "2026-11-02T20:13:58+08:00";
            var exact = FrameParser.Parse("{\"type\":\"reply\",\"turnId\":\"t-time\",\"text\":\"" + ExactTimestamp + "\"}");
            Assert.AreEqual(ExactTimestamp, exact.Text);

            const string InSentence = "会议定在 2026-11-02T20:13:58+08:00，别迟到。";
            var sentence = FrameParser.Parse("{\"type\":\"reply\",\"turnId\":\"t-time2\",\"text\":\"" + InSentence + "\"}");
            Assert.AreEqual(InSentence, sentence.Text);
        }

        [Test]
        public void SayOmitsAnEmptyTurnId()
        {
            const string Expected = "{\"type\":\"say\",\"text\":\"晚上好\"}";
            Assert.AreEqual(Expected, ClientFrame.Say("晚上好"));
            Assert.AreEqual(Expected, ClientFrame.Say("晚上好", null));
            Assert.AreEqual(Expected, ClientFrame.Say("晚上好", string.Empty));
        }

        [Test]
        public void SayCarriesTheTurnIdWhenThereIsOne()
        {
            Assert.AreEqual(
                "{\"type\":\"say\",\"text\":\"晚上好\",\"turnId\":\"t-1\"}",
                ClientFrame.Say("晚上好", "t-1"));
        }

        [Test]
        public void SayEscapesQuotesAndNewlines()
        {
            const string Text = "她说：\"你先坐\"\n第二行";
            var json = ClientFrame.Say(Text, "t-2");
            var parsed = MiniJson.Parse(json);
            Assert.AreEqual("say", MiniJson.String(parsed, "type"));
            Assert.AreEqual(Text, MiniJson.String(parsed, "text"), "the text round-trips through JSON untouched");
        }

        [Test]
        public void MaxInputCharsMatchesTheProtocol()
        {
            Assert.AreEqual(300, ClientFrame.MaxInputChars);
        }
    }

    /// <summary>Parsed JSON value that remembers where it sits in the source text.</summary>
    sealed class JsonNode
    {
        public string Kind;   // object | array | string | number | bool | null
        public string Text;   // decoded string, raw number, or "true"/"false"
        public readonly Dictionary<string, JsonNode> Members = new Dictionary<string, JsonNode>();
        public readonly List<JsonNode> Items = new List<JsonNode>();
        public int Start;
        public int End;
    }

    /// <summary>
    /// Minimal JSON reader for the corpus and for the ClientFrame output. It exists so the test assembly
    /// needs no third-party JSON library: Newtonsoft reaches the runtime through the
    /// com.unity.nuget.newtonsoft-json package, and the tests must not depend on it.
    /// </summary>
    static class MiniJson
    {
        public static JsonNode Parse(string text)
        {
            var index = 0;
            var node = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length) throw new InvalidDataException("trailing JSON content at offset " + index);
            return node;
        }

        public static JsonNode Member(JsonNode node, string key)
        {
            if (node == null || node.Kind != "object") return null;
            JsonNode value;
            return node.Members.TryGetValue(key, out value) ? value : null;
        }

        public static string String(JsonNode node, string key)
        {
            var value = Member(node, key);
            return value != null && value.Kind == "string" ? value.Text : null;
        }

        public static double? Number(JsonNode node, string key)
        {
            var value = Member(node, key);
            if (value == null || value.Kind != "number") return null;
            return ToNumber(value);
        }

        public static bool? Bool(JsonNode node, string key)
        {
            var value = Member(node, key);
            if (value == null || value.Kind != "bool") return null;
            return value.Text == "true";
        }

        public static double ToNumber(JsonNode node)
        {
            return double.Parse(node.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                var c = text[index];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') return;
                index++;
            }
        }

        static JsonNode ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new InvalidDataException("unexpected end of JSON");
            var start = index;
            var c = text[index];
            JsonNode node;
            switch (c)
            {
                case '{':
                    node = ParseObject(text, ref index);
                    break;
                case '[':
                    node = ParseArray(text, ref index);
                    break;
                case '"':
                    node = new JsonNode { Kind = "string", Text = ParseString(text, ref index) };
                    break;
                case 't':
                    Expect(text, ref index, "true");
                    node = new JsonNode { Kind = "bool", Text = "true" };
                    break;
                case 'f':
                    Expect(text, ref index, "false");
                    node = new JsonNode { Kind = "bool", Text = "false" };
                    break;
                case 'n':
                    Expect(text, ref index, "null");
                    node = new JsonNode { Kind = "null" };
                    break;
                default:
                    node = new JsonNode { Kind = "number", Text = ParseNumber(text, ref index) };
                    break;
            }
            node.Start = start;
            node.End = index;
            return node;
        }

        static JsonNode ParseObject(string text, ref int index)
        {
            var node = new JsonNode { Kind = "object" };
            index++; // '{'
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}')
            {
                index++;
                return node;
            }
            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"') throw new InvalidDataException("object key expected at offset " + index);
                var key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':') throw new InvalidDataException("':' expected at offset " + index);
                index++;
                node.Members[key] = ParseValue(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new InvalidDataException("unterminated object");
                if (text[index] == ',')
                {
                    index++;
                    continue;
                }
                if (text[index] != '}') throw new InvalidDataException("',' or '}' expected at offset " + index);
                index++;
                return node;
            }
        }

        static JsonNode ParseArray(string text, ref int index)
        {
            var node = new JsonNode { Kind = "array" };
            index++; // '['
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']')
            {
                index++;
                return node;
            }
            while (true)
            {
                node.Items.Add(ParseValue(text, ref index));
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new InvalidDataException("unterminated array");
                if (text[index] == ',')
                {
                    index++;
                    continue;
                }
                if (text[index] != ']') throw new InvalidDataException("',' or ']' expected at offset " + index);
                index++;
                return node;
            }
        }

        static string ParseString(string text, ref int index)
        {
            index++; // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                if (index >= text.Length) throw new InvalidDataException("unterminated string");
                var c = text[index++];
                if (c == '"') return builder.ToString();
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (index >= text.Length) throw new InvalidDataException("unterminated escape");
                var escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) throw new InvalidDataException("truncated \\u escape");
                        builder.Append((char)Convert.ToInt32(text.Substring(index, 4), 16));
                        index += 4;
                        break;
                    default:
                        throw new InvalidDataException("unsupported escape \\" + escape);
                }
            }
        }

        static string ParseNumber(string text, ref int index)
        {
            var start = index;
            while (index < text.Length && "+-.eE0123456789".IndexOf(text[index]) >= 0) index++;
            if (index == start) throw new InvalidDataException("unexpected character '" + text[index] + "' at offset " + index);
            return text.Substring(start, index - start);
        }

        static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new InvalidDataException("'" + literal + "' expected at offset " + index);
            }
            index += literal.Length;
        }
    }
}
