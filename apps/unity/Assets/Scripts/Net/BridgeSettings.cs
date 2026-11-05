using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Mas3D.Net
{
    /// <summary>
    /// BridgeClient 的连接参数。默认值对得上本机 Python 桥接（apps/bridge）：端口 8790、
    /// ws 路径 /mas3d/ws，令牌是桥接自己写盘的那份（apps/bridge/data/mas3d/token，64 位 hex）。
    /// 令牌不进版本库、不写死在 Unity 工程里，运行时从 <see cref="TokenFilePath"/> 读。
    /// </summary>
    public sealed class BridgeSettings
    {
        /// <summary>桥接默认端点，与 apps/bridge 的 server.ts 对齐。</summary>
        public const string DefaultUrl = "ws://127.0.0.1:8790/mas3d/ws";

        /// <summary>ws 端点；默认是运行时桥接，测试会换成 mock 的端口。</summary>
        public string Url = DefaultUrl;

        /// <summary>仓库里那份令牌的相对路径：桥接启动时自己生成并写盘，Unity 侧只读不算。</summary>
        public const string RepoTokenRelativePath = "apps/bridge/data/mas3d/token";

        /// <summary>免安装拷贝时放在 exe 旁边的那份令牌文件名。</summary>
        public const string ExeTokenFileName = "mas3d-token";

        /// <summary>装到别处（不在仓库里）时，用户在 persistentDataPath 里自己放的那份。</summary>
        public const string PersistentTokenFileName = "token";

        /// <summary>访问令牌；空串表示不带 token（只对没开鉴权的 mock 有意义）。</summary>
        public string Token = string.Empty;

        /// <summary>重试间隔，单位秒；用完最后一项后一直按最后一项来。</summary>
        public float[] BackoffSeconds = { 0.5f, 1f, 2f, 4f, 8f };

        /// <summary>
        /// 令牌候选路径，有序：仓库里那份 → exe 旁边 → persistentDataPath。纯函数：三个入参给了什么就用什么，
        /// 不读 <see cref="Application"/>，所以 EditMode 能直接测顺序。空目录跳过，重复路径只留第一次出现的。
        /// </summary>
        /// <param name="exeDir">exe 所在目录（player 里是 build/，编辑器里是 apps/unity）。</param>
        /// <param name="repoRoot">仓库根（player = exeDir 的上一级，编辑器 = dataPath 往上三层）。</param>
        /// <param name="persistentDir">Application.persistentDataPath。</param>
        public static string[] TokenCandidates(string exeDir, string repoRoot, string persistentDir)
        {
            var paths = new List<string>(3);
            AddCandidate(paths, repoRoot, RepoTokenRelativePath);
            AddCandidate(paths, exeDir, ExeTokenFileName);
            AddCandidate(paths, persistentDir, PersistentTokenFileName);
            return paths.ToArray();
        }

        static void AddCandidate(List<string> paths, string dir, string relative)
        {
            if (string.IsNullOrEmpty(dir)) return;

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(dir, relative));
            }
            catch (System.ArgumentException)
            {
                return;
            }
            catch (System.NotSupportedException)
            {
                return;
            }

            foreach (var existing in paths)
            {
                if (string.Equals(existing, full, System.StringComparison.OrdinalIgnoreCase)) return;
            }
            paths.Add(full);
        }

        /// <summary>
        /// 本机运行时的候选表。仓库根在编辑器里是 dataPath 往上三层；打包件里是 exe 所在目录的上一级
        /// （<c>build/Mas3D.exe</c> → 仓库根）——之前用 dataPath 往上三层在打包件里会落到盘符根，双击就读不到令牌。
        /// </summary>
        public static string[] RuntimeTokenCandidates()
        {
            string exeDir;
            try
            {
                // 编辑器里 dataPath = <repo>/apps/unity/Assets，player 里 = <build>/Mas3D_Data；
                // 两者的上一级都是"exe 那一侧"的目录。
                exeDir = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)) ?? string.Empty;
            }
            catch (System.ArgumentException)
            {
                exeDir = string.Empty;
            }

#if UNITY_EDITOR
            var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
#else
            var repoRoot = string.IsNullOrEmpty(exeDir) ? string.Empty : (Path.GetDirectoryName(exeDir) ?? string.Empty);
#endif
            return TokenCandidates(exeDir, repoRoot, Application.persistentDataPath);
        }

        /// <summary>
        /// 首选令牌文件的绝对路径（候选表第一条）：bridge 启动时自己生成并写盘的那一份。
        /// 读令牌不要只认这一条，用 <see cref="RuntimeTokenCandidates"/> 逐个试（见 DialogueBridge）。
        /// </summary>
        public static string TokenFilePath
        {
            get
            {
                var candidates = RuntimeTokenCandidates();
                return candidates.Length > 0 ? candidates[0] : string.Empty;
            }
        }

        /// <summary>后台连接循环拿一份自己的副本，之后外部再改这个对象也不会影响进行中的连接。</summary>
        internal BridgeSettings Clone()
        {
            var copy = (BridgeSettings)MemberwiseClone();
            if (BackoffSeconds != null) copy.BackoffSeconds = (float[])BackoffSeconds.Clone();
            return copy;
        }
    }
}
