using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Mas3D.Actions;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mas3D.Boot
{
    /// <summary>
    /// 打包后的玩家自检：只有命令行带 <c>-selftest &lt;marker 路径&gt;</c> 时才活，其余情况 Awake 里直接关掉。
    /// 依次验证三件事——桥接连上、一句话拿到回复、hands 动作能进入并保持；每一步写一行带毫秒时间戳的 marker，
    /// 最后按结果退出进程：0 全过，3 桥接超时，4 回复超时，5 动作失败。Windows 上退出走 kernel32!ExitProcess——
    /// batchmode player 实测：Application.Quit 之后进程根本不退出（码自然也交不出来），单独 Environment.Exit 同样不退出。
    /// 计时一律用 Stopwatch，不吃 Time.time（batchmode 下 timeScale 与暂停都不该影响判据）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerSelfTest : MonoBehaviour
    {
        public const float BridgeTimeoutSeconds = 45f;
        public const float SubmitRetrySeconds = 5f;
        public const float ReplyTimeoutSeconds = 30f;
        public const float ActionEnterSeconds = 1.5f;
        public const float ActionHoldSeconds = 1f;

        /// <summary>抖动容差：进场判定的 1.5 s 之外再放 0.1 s，别让一帧的调度延迟把动作判成失败。</summary>
        public const float ActionSlack = 0.1f;

        public const int ExitOk = 0;
        public const int ExitBridgeTimeout = 3;
        public const int ExitReplyTimeout = 4;
        public const int ExitActionFailed = 5;

        /// <summary>自检发出去的那句话；空的提交会被 ConversationController 直接拒掉，所以必须有一句真的。</summary>
        public const string ProbeLine = "你好";

        public const string ProbeActionId = "hands";

        /// <summary>marker 文件路径，来自 -selftest；空字符串表示这不是自检模式。</summary>
        public string MarkerPath { get; private set; }

        /// <summary>自检的退出码；还没结束时是 -1。</summary>
        public int ExitCode { get; private set; } = -1;

        public bool Finished { get; private set; }

        public float ElapsedSeconds { get; private set; }

        readonly Stopwatch _clock = new Stopwatch();

        /// <summary>命令行 -token 给的那份；空表示交给磁盘（候选表见 BridgeSettings）。</summary>
        string _tokenFromArgs;

        void Awake()
        {
            BootArgs boot;
            try
            {
                boot = BootArgs.Parse(Environment.GetCommandLineArgs());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[selftest] 命令行解析失败，自检关闭：" + e.Message);
                enabled = false;
                return;
            }

            if (string.IsNullOrEmpty(boot.SelfTestPath))
            {
                enabled = false;
                return;
            }

            MarkerPath = boot.SelfTestPath;
            _tokenFromArgs = boot.Token;
        }

        IEnumerator Start()
        {
            if (!enabled) yield break;
            _clock.Start();

            // 令牌来源先落一行：冒烟脚本据此钉住「令牌真的来自磁盘」，而不是被 -token 喂进来的。
            string tokenSource = DialogueBridge.NoTokenSource;
            var bootToken = _tokenFromArgs;
            if (!string.IsNullOrEmpty(bootToken))
            {
                tokenSource = DialogueBridge.ArgTokenSource;
            }
            else
            {
                bootToken = DialogueBridge.ReadTokenFromDisk(out tokenSource);
            }
            Mark("token src=" + tokenSource + " len=" + (bootToken != null ? bootToken.Length : 0));

            DialogueBridge bridge = null;
            while (_clock.Elapsed.TotalSeconds < BridgeTimeoutSeconds)
            {
                if (bridge == null) bridge = FindAnyObjectByType<DialogueBridge>();
                if (bridge != null && bridge.Client != null && bridge.Client.State == BridgeState.Connected)
                {
                    Mark("bridge ok");
                    break;
                }

                yield return null;
            }

            if (Finished) yield break;
            if (_clock.Elapsed.TotalSeconds >= BridgeTimeoutSeconds && !(bridge != null && bridge.Client != null && bridge.Client.State == BridgeState.Connected))
            {
                Mark("bridge timeout elapsed=" + _clock.Elapsed.TotalSeconds.ToString("F1"));
                Finish(ExitBridgeTimeout);
                yield break;
            }

            var controller = bridge != null && bridge.Controller != null ? bridge.Controller : FindAnyObjectByType<ConversationController>();
            var panel = controller != null && controller.Panel != null ? controller.Panel
                : bridge != null && bridge.Panel != null ? bridge.Panel
                : FindAnyObjectByType<DialoguePanel>();

            if (controller == null || panel == null)
            {
                Mark("reply timeout elapsed=" + _clock.Elapsed.TotalSeconds.ToString("F1") + " reason=" + (controller == null ? "no-controller" : "no-panel"));
                Finish(ExitReplyTimeout);
                yield break;
            }

            bool submitted = false;
            double submitUntil = _clock.Elapsed.TotalSeconds + SubmitRetrySeconds;
            while (_clock.Elapsed.TotalSeconds < submitUntil)
            {
                if (controller.Submit(ProbeLine))
                {
                    submitted = true;
                    break;
                }

                yield return null;
            }

            if (!submitted)
            {
                Mark("reply timeout elapsed=" + _clock.Elapsed.TotalSeconds.ToString("F1") + " reason=submit-refused");
                Finish(ExitReplyTimeout);
                yield break;
            }

            // 判“回没回”只看 Phase（只有 reply 帧会把它置成 Answered）＋面板非空。
            // 不能用面板长度比较：空面板的占位文案有 12 个字，而一句「嗯。」只有 2 个字——
            // 短回复会让长度反而变小，链路完全正常也会被判超时。
            bool answered = false;
            int replyLength = 0;
            double replyUntil = _clock.Elapsed.TotalSeconds + ReplyTimeoutSeconds;
            while (_clock.Elapsed.TotalSeconds < replyUntil)
            {
                var text = panel.LogText;
                replyLength = text != null ? text.Length : 0;
                if (controller.Phase == TurnPhase.Answered && replyLength > 0)
                {
                    answered = true;
                    break;
                }

                yield return null;
            }

            if (!answered)
            {
                Mark("reply timeout elapsed=" + _clock.Elapsed.TotalSeconds.ToString("F1") + " phase=" + controller.Phase);
                Finish(ExitReplyTimeout);
                yield break;
            }

            Mark("reply ok len=" + replyLength);

            var director = FindAnyObjectByType<ActionDirector>();
            if (director == null)
            {
                Mark("action failed reason=no-director");
                Finish(ExitActionFailed);
                yield break;
            }

            if (!director.TryEnterAction(ProbeActionId))
            {
                Mark("action failed reason=" + (string.IsNullOrEmpty(director.LastFaults) ? "refused" : director.LastFaults));
                Finish(ExitActionFailed);
                yield break;
            }

            // 进场与保持分开计时：先等 Held 从 0 起来（进场可能比一帧慢），起来之后再独立给 1.0 s + 余量。
            float held = 0f;
            double enterUntil = _clock.Elapsed.TotalSeconds + ActionEnterSeconds + ActionSlack;
            while (_clock.Elapsed.TotalSeconds < enterUntil && held <= 0f)
            {
                held = director.Held;
                yield return null;
            }

            bool entered = held > 0f;
            if (entered)
            {
                double holdUntil = _clock.Elapsed.TotalSeconds + ActionHoldSeconds + ActionSlack;
                while (_clock.Elapsed.TotalSeconds < holdUntil && held < ActionHoldSeconds)
                {
                    held = director.Held;
                    yield return null;
                }
            }

            director.Cancel();

            if (!entered)
            {
                Mark("action failed reason=never-entered");
                Finish(ExitActionFailed);
                yield break;
            }

            if (held < ActionHoldSeconds)
            {
                Mark("action failed held=" + held.ToString("F2"));
                Finish(ExitActionFailed);
                yield break;
            }

            Mark("action " + ProbeActionId + " held=" + held.ToString("F2") + " ok");
            Mark("done");
            Finish(ExitOk);
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern void ExitProcess(uint exitCode);
#endif

        /// <summary>写一行 marker（毫秒时间戳 + 固定前缀）并同步进 player 日志；写盘失败只警告，不让自检跟着倒。</summary>
        public void Mark(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [selftest] " + text;
            Debug.Log(line);
            if (string.IsNullOrEmpty(MarkerPath)) return;

            try
            {
                string full = Path.GetFullPath(MarkerPath);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(full, line + Environment.NewLine);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[selftest] marker 写入失败：" + e.Message);
            }
        }

        /// <summary>按自检结果退出进程：0/3/4/5。只认第一次调用，避免超时和成功路径重复刷码。</summary>
        public void Finish(int code)
        {
            if (Finished) return;

            Finished = true;
            ExitCode = code;
            ElapsedSeconds = (float)_clock.Elapsed.TotalSeconds;
            Debug.Log("[selftest] exit=" + code + " elapsed=" + ElapsedSeconds.ToString("F2"));

            // 实测（Windows IL2CPP player + -batchmode，三轮）：Application.Quit 之后进程不退出（连 -quit 单独
            // 跑 45 s 仍活着，码无从谈）；单独 Environment.Exit(3) 也不退出（90 s 后仍活，只能被外部 kill）。
            // marker 是同步 AppendAllText 写的、已经落盘，所以 Windows 上直接调 kernel32!ExitProcess：
            // 它同步结束进程，并把退出码原样交给 OS。其它平台回落到引擎自己的退出路径。
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            ExitProcess((uint)code);
#endif
            Application.Quit(code);
            Environment.Exit(code);
        }
    }
}
