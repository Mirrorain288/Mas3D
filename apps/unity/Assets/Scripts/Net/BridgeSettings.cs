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

        /// <summary>访问令牌；空串表示不带 token（只对没开鉴权的 mock 有意义）。</summary>
        public string Token = string.Empty;

        /// <summary>重试间隔，单位秒；用完最后一项后一直按最后一项来。</summary>
        public float[] BackoffSeconds = { 0.5f, 1f, 2f, 4f, 8f };

        /// <summary>
        /// 桥接令牌文件的绝对路径：bridge 启动时自己生成并写盘的那一份，
        /// Unity 侧只读不算，避免两处各存一份秘密。
        /// </summary>
        public static string TokenFilePath
        {
            get
            {
                // Application.dataPath 是 <repo>/apps/unity/Assets，往上三层就是仓库根。
                var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
                return Path.GetFullPath(Path.Combine(repoRoot, "apps", "bridge", "data", "mas3d", "token"));
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
