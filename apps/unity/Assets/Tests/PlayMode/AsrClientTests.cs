using System;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Mas3D.Voice;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 3 的 ASR 客户端用例：对着 <see cref="ScriptedHttp"/>（真 socket）跑真 UnityWebRequest，
    /// 断言线缆上的请求形状与四种响应结局（成功 / 非 2xx / ok=false / 坏 JSON），以及
    /// “任何失败都走 onError、协程不抛异常”。
    ///
    /// 协程是手动泵的（while + MoveNext），不经 Unity 的嵌套协程调度：这样每个用例对
    /// “请求什么时候真的结束”有明确控制，失败原因也只落在断言里。
    /// </summary>
    public class AsrClientTests
    {
        const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string AsrPath = "/mas3d/api/asr";
        const string OkBody = @"{""ok"":true,""asr"":{""text"":""晚上好"",""durationMs"":1234,""model"":""small""}}";
        // 真身上：桥接的 require_token 抛 HTTPException(401, detail=...)，fastapi 回 {"detail": "..."}，
        // 既没有 ok 也没有 error —— /mas3d/api/asr 自己的失败信封不是这种形状。
        const string UnauthorizedBody = @"{""detail"":""令牌不正确（http://127.0.0.1:8790/mas3d/api/health 需要 Authorization: Bearer <token>）""}";
        // ASR 路由自己的失败信封（400/503/504 走这里）：桥接在这个形状里给的是 error。
        const string AsrErrorEnvelopeBody = @"{""ok"":false,""error"":""whisper 模型加载超时""}";

        [UnityTest]
        public IEnumerator PostsWavAndReadsTheTranscript()
        {
            var http = new ScriptedHttp(200, OkBody);
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var wav = Wav();
                var ok = false;
                AsrResult result = default(AsrResult);
                string error = null;

                var routine = client.Transcribe(wav, r => { ok = true; result = r; }, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsTrue(ok, "200 + ok=true 必须走 onOk");
                Assert.IsNull(error, "成功时不该走 onError：" + error);
                Assert.AreEqual("晚上好", result.Text, "asr.text 原样透传");
                Assert.AreEqual(1234, result.DurationMs);
                Assert.AreEqual("small", result.Model);

                var requests = http.Requests;
                Assert.AreEqual(1, requests.Length, "一次识别只发一条请求");
                var request = requests[0];
                Assert.AreEqual("POST", request.Method);
                Assert.AreEqual(AsrPath, request.Path, "打到桥接的 ASR 路由");
                Assert.AreEqual("audio/wav", request.Header("Content-Type"), "body 是原始 WAV，不是 multipart");
                Assert.AreEqual("Bearer " + Token, request.Header("Authorization"));
                Assert.AreEqual(wav.Length, request.BodyLength, "上传的字节必须就是传进来的 WAV");
                Assert.AreEqual((byte)'R', request.Body[0]);
                Assert.AreEqual((byte)'I', request.Body[1]);
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsARejectedToken()
        {
            var http = new ScriptedHttp(401, UnauthorizedBody);
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = "wrong" };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsFalse(ok, "401 不该走 onOk");
                Assert.IsNotNull(error, "401 必须走 onError");
                StringAssert.Contains("401", error, "错误文案要带上状态码，用户/日志才知道是鉴权问题");
                StringAssert.Contains("令牌不正确", error, "fastapi 的 detail 原句要透传，别只剩一个状态码");
                StringAssert.Contains("/mas3d/api/health", error, "detail 里的令牌提示是用户唯一能照着修的信息");
                Assert.AreEqual(1, http.Requests.Length, "401 时请求仍然只发一条");
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsAnErrorEnvelopeFromTheAsrRoute()
        {
            // 没有 401 那么特殊：ASR 路由自己的失败（400/503/504）回的是信封，error 就是给人看的那句。
            var http = new ScriptedHttp(503, AsrErrorEnvelopeBody);
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsFalse(ok, "503 不该走 onOk");
                StringAssert.Contains("503", error);
                StringAssert.Contains("whisper 模型加载超时", error, "ASR 路由的 error 信封原文要透传");
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator PrefersTheErrorFieldOverDetail()
        {
            // 两种字段同时出现时 error 优先：detail 只是 fastapi 那条路的兜底，不能抢 error 的位置。
            var http = new ScriptedHttp(400, @"{""ok"":false,""error"":""body 必须是非空 WAV"",""detail"":""不该被选中的那句话""}");
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsFalse(ok, "400 不该走 onOk");
                StringAssert.Contains("body 必须是非空 WAV", error, "两种字段都在时 error 优先");
                StringAssert.DoesNotContain("不该被选中的那句话", error, "detail 只是兜底");
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsAnOkFalseEnvelope()
        {
            var http = new ScriptedHttp(200, @"{""ok"":false,""error"":""没听清""}");
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsFalse(ok, "ok=false 不该走 onOk");
                Assert.IsNotNull(error);
                StringAssert.Contains("没听清", error, "200 但 ok=false 时把 error 字段原文交给调用方");
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsUnparseableJson()
        {
            var http = new ScriptedHttp(200, "<html>not json</html>");
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsFalse(ok, "坏 JSON 不该走 onOk");
                Assert.IsFalse(string.IsNullOrEmpty(error), "坏 JSON 必须变成 onError，而不是异常或静默");
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsAMissingAsrPayload()
        {
            var http = new ScriptedHttp(200, @"{""ok"":true}");
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsFalse(ok, "ok=true 但没有 asr 也不该当成成功");
                Assert.IsFalse(string.IsNullOrEmpty(error));
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsAServerError()
        {
            var http = new ScriptedHttp(500, @"{""ok"":false,""error"":""内部错误""}");
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsFalse(ok, "500 不该走 onOk");
                StringAssert.Contains("500", error);
                StringAssert.Contains("内部错误", error);
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator OmitsAuthorizationWhenTheTokenIsEmpty()
        {
            var http = new ScriptedHttp(200, OkBody);
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsTrue(ok, "没令牌也要能发（对没开鉴权的 mock 有意义）：" + error);
                var request = http.Requests[0];
                Assert.IsNull(request.Header("Authorization"), "Token 为空时不该带 Bearer 头");
                Assert.AreEqual("audio/wav", request.Header("Content-Type"), "Content-Type 与令牌无关");
            }
            finally
            {
                http.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsAnUnreachableEndpoint()
        {
            var port = ClosedPort();
            var client = new AsrClient { Endpoint = "http://127.0.0.1:" + port + AsrPath, Token = Token };
            var ok = false;
            string error = null;

            var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
            var watch = Stopwatch.StartNew();
            while (true)
            {
                Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "连接失败也要在 8 秒内收敛");
                if (!routine.MoveNext()) break;

                var pending = routine.Current as AsyncOperation;
                while (pending != null && !pending.isDone)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "连接失败也要在 8 秒内收敛");
                    yield return null;
                }
                if (pending == null) yield return null;
            }

            Assert.IsFalse(ok, "端口没人听时不该走 onOk");
            Assert.IsFalse(string.IsNullOrEmpty(error), "传输错误必须变成 onError，协程不抛异常");
        }

        /// <summary>
        /// 换代/禁用时 <see cref="AsrClient.Abort"/> 必须真的掐掉连接，而且一个回调都不发：
        /// 调用方早就把这一代作废了，迟到的 onOk 会把旧文本写进新一回合的输入框。
        /// 只 StopCoroutine 是不够的 —— 请求还在飞，桥接的 worker 会继续推理并占着 GPU 锁。
        /// </summary>
        [UnityTest]
        public IEnumerator AbortStopsTheRequestWithoutCallingBack()
        {
            var http = new ScriptedHttp(200, OkBody) { DelayMs = 2000 };
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                var ok = false;
                string error = null;

                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);

                // 先让请求真的到服务端：服务端要睡 2 秒才回，这就是"在飞"的窗口。
                var watch = Stopwatch.StartNew();
                while (http.Accepted == 0)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 5.0, "请求 5 秒内必须到达服务端");
                    Assert.IsTrue(routine.MoveNext(), "协程不该在请求还没回包时结束");
                    var waiting = routine.Current as AsyncOperation;
                    while (waiting != null && !waiting.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 5.0, "请求 5 秒内必须到达服务端");
                        yield return null;
                    }
                    if (waiting == null) yield return null;
                }
                Assert.AreEqual(1, http.Accepted, "前置条件：请求已经在线缆上");

                client.Abort();

                var drain = Stopwatch.StartNew();
                while (routine.MoveNext())
                {
                    Assert.Less(drain.Elapsed.TotalSeconds, 3.0, "被掐断的请求必须马上收敛，不能等满 55 秒超时");
                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone) yield return null;
                }

                Assert.IsFalse(ok, "被中止的请求不许走 onOk");
                Assert.IsNull(error, "被中止的请求也不许报失败：回调只会是噪音，而调用方已经在下一件事上");
            }
            finally
            {
                http.Dispose();
            }
        }

        /// <summary>
        /// 中止不只是停协程：被掐断的连接必须立刻让出位置，下一次 Transcribe 不能排在它后面。
        /// 桥接侧一条请求占着工作线程和 GPU 锁（asr.py 的 shield 连客户端断开都不打断推理），
        /// 本地若还把旧请求当成活的，第二次录音只能等锁等到 503。
        /// 这里用"慢服务端 + 立刻回包的服务端"把这件事量出来：旧协程必须瞬间收敛，新请求必须立刻拿到 body。
        /// </summary>
        [UnityTest]
        public IEnumerator AnAbortedRequestNoLongerBlocksTheNextOne()
        {
            var slow = new ScriptedHttp(200, OkBody) { DelayMs = 2000 };
            var fast = new ScriptedHttp(200, OkBody);
            try
            {
                var client = new AsrClient { Endpoint = slow.Url + AsrPath, Token = Token };
                var firstOk = false;
                string firstError = null;
                var first = client.Transcribe(Wav(), r => firstOk = true, e => firstError = e);

                var watch = Stopwatch.StartNew();
                while (slow.Accepted == 0)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 5.0, "第一个请求 5 秒内必须到达服务端");
                    Assert.IsTrue(first.MoveNext(), "第一个请求还没回包，协程不该结束");
                    var waiting = first.Current as AsyncOperation;
                    while (waiting != null && !waiting.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 5.0, "第一个请求 5 秒内必须到达服务端");
                        yield return null;
                    }
                    if (waiting == null) yield return null;
                }

                client.Abort();

                var drain = Stopwatch.StartNew();
                while (first.MoveNext())
                {
                    Assert.Less(drain.Elapsed.TotalSeconds, 1.0,
                        "Abort 之后旧协程必须立刻收敛：还在等 2 秒的回包，说明连接没真的被掐掉");
                    var pending = first.Current as AsyncOperation;
                    while (pending != null && !pending.isDone) yield return null;
                }
                Assert.IsFalse(firstOk, "被中止的请求不许走 onOk");
                Assert.IsNull(firstError, "被中止的请求也不许报失败");

                // 换到立刻回包的地址：这就是"下一次按住 V"。
                client.Endpoint = fast.Url + AsrPath;
                var secondOk = false;
                string secondText = null;
                string secondError = null;
                var second = client.Transcribe(Wav(),
                    r => { secondOk = true; secondText = r.Text; },
                    e => secondError = e);

                var secondWatch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(secondWatch.Elapsed.TotalSeconds, 2.0,
                        "上一次请求已经中止，第二次转写不该再等它");
                    if (!second.MoveNext()) break;

                    var pending = second.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(secondWatch.Elapsed.TotalSeconds, 2.0,
                            "上一次请求已经中止，第二次转写不该再等它");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsTrue(secondOk, "第二次转写必须成功，错误：" + secondError);
                Assert.IsNull(secondError, "第二次转写不该报错：" + secondError);
                Assert.AreEqual("晚上好", secondText, "拿到的必须是第二次请求的 body");
                Assert.AreEqual(1, fast.Requests.Length, "第二次请求必须真的发出去");
            }
            finally
            {
                slow.Dispose();
                fast.Dispose();
            }
        }

        /// <summary>空闲时 Abort 必须是无操作：中止过一次之后客户端还要能照常发下一条。</summary>
        [UnityTest]
        public IEnumerator AnAbortLeavesTheClientUsable()
        {
            var http = new ScriptedHttp(200, OkBody);
            try
            {
                var client = new AsrClient { Endpoint = http.Url + AsrPath, Token = Token };
                client.Abort();   // 从来没有请求在飞：不许抛、不许把状态弄坏

                var ok = false;
                string error = null;
                var routine = client.Transcribe(Wav(), r => ok = true, e => error = e);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                    if (!routine.MoveNext()) break;

                    var pending = routine.Current as AsyncOperation;
                    while (pending != null && !pending.isDone)
                    {
                        Assert.Less(watch.Elapsed.TotalSeconds, 8.0, "ASR 请求超过 8 秒");
                        yield return null;
                    }
                    if (pending == null) yield return null;
                }

                Assert.IsTrue(ok, "Abort 之后客户端必须还能发下一条");
                Assert.IsNull(error, "成功时不该走 onError：" + error);
            }
            finally
            {
                http.Dispose();
            }
        }

        /// <summary>
        /// 客户端超时必须比桥接的 45 s 预算长：桥接从读完 body 才开始计时，里面还含最多 15 s 等锁。
        /// 两边同值时 Unity 几乎总是先超时，走通用失败文案，桥接那句 504「语音识别超时」永远回不到面板。
        /// </summary>
        [Test]
        public void TheClientOutlivesTheBridgeBudget()
        {
            Assert.Greater(AsrClient.TimeoutSeconds, 45,
                "桥接 ASR_TIMEOUT_S = 45 s（含 15 s 等锁）：客户端必须更宽，才能把 504 的原因带回面板");
        }

        static byte[] Wav()
        {
            // 10 ms 的静音，形状与 VoiceInput 真正上传的一致。
            return WavEncoder.Encode(new float[160], 16000);
        }

        static int ClosedPort()
        {
            // 先占一个端口再放掉：系统不会马上把它分给别人，够这条用例跑完。
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }
    }
}
