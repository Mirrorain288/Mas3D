using System;
using System.Collections.Generic;
using System.Text;

namespace Mas3D.Voice
{
    /// <summary>
    /// 把一段回复切成可以逐句合成/逐句显示的小句（纯函数）。
    ///
    /// 切分规则对齐 apps/web/src/chat/voice.ts 与 apps/bridge/tts.py 的 split_sentences：
    /// 中文和英文的终止符都算句尾，标点留在句子里；纯标点的片段丢掉（送上去只会换回 400）。
    /// 另外单句要压到 <see cref="MaxSentenceChars"/> 以内：桥接对整段有 600 字上限，
    /// 一句话太长整轮就白瞎了，所以长句在逗号处再切，实在没有断点就硬切。
    /// </summary>
    public static class TtsSentences
    {
        /// <summary>单句上限。留出余量：桥接是 600 字上限，200 字读起来也还没到换气的极限。</summary>
        public const int MaxSentenceChars = 200;

        const string Terminators = "。！？.!?";
        const string BreakChars = "，,、；;：: ";

        /// <summary>切句。不截断——字幕要能显示完整回复，40 句的合成上限由调用方（ReplySpeaker）管。</summary>
        public static string[] Split(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result.ToArray();

            var buffer = new StringBuilder();
            foreach (var c in text)
            {
                buffer.Append(c);
                if (Terminators.IndexOf(c) < 0) continue;
                Flush(buffer, result);   // 终止符跟着前一句走
            }
            Flush(buffer, result);
            return result.ToArray();
        }

        static void Flush(StringBuilder buffer, List<string> result)
        {
            if (buffer.Length == 0) return;
            var piece = buffer.ToString();
            buffer.Clear();
            AddPiece(piece, result);
        }

        static void AddPiece(string raw, List<string> result)
        {
            var piece = raw.Trim();
            if (!HasContent(piece)) return;

            while (piece.Length > MaxSentenceChars)
            {
                var cut = CutLength(piece);
                var head = piece.Substring(0, cut).Trim();
                if (HasContent(head)) result.Add(head);
                piece = piece.Substring(cut).Trim();
            }
            if (HasContent(piece)) result.Add(piece);
        }

        static int CutLength(string piece)
        {
            // 优先在逗号/顿号/空白处断：读出来的停顿才自然。找不到就按上限硬切。
            for (var i = MaxSentenceChars - 1; i > 0; i--)
            {
                if (BreakChars.IndexOf(piece[i]) >= 0) return i + 1;
            }
            return SafeCut(piece, MaxSentenceChars);
        }

        /// <summary>不在代理对中间切：head 以高位代理结尾就把整个字符留给下一句。</summary>
        static int SafeCut(string piece, int cut)
        {
            if (cut <= 0) return 0;
            if (cut >= piece.Length) return piece.Length;
            if (char.IsHighSurrogate(piece[cut - 1])) return cut - 1;
            return cut;
        }

        static bool HasContent(string piece)
        {
            if (string.IsNullOrEmpty(piece)) return false;
            foreach (var c in piece)
            {
                // 中文、拉丁字母、数字都算内容；纯标点（"。。。！！"）不是一句话。
                if (char.IsLetterOrDigit(c)) return true;
            }
            return false;
        }
    }
}
