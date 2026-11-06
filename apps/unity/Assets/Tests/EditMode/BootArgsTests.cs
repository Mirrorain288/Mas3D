using System.Text.RegularExpressions;
using Mas3D.Boot;
using Mas3D.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// P6 Task 2：启动参数只认 -bridge / -token / -selftest，其余一律忽略。
    /// 非法端口必须被拒（HasBridge=false + 一条警告），绝不猜一个"看起来能连"的地址——
    /// 那会把"桥接没起来"伪装成"连上了但没反应"，比直接报错更难查。
    ///
    /// 解析是纯函数：不起场景、不进播放模式，命令行原样从 Environment.GetCommandLineArgs() 进来。
    /// </summary>
    public class BootArgsTests
    {
        [Test]
        public void HostAndPortComeFromTheBridgeArgument()
        {
            var boot = BootArgs.Parse(new[] { "-bridge", "127.0.0.1:8790" });

            Assert.IsTrue(boot.HasBridge, "-bridge host:port 必须算作显式指定了桥接地址");
            Assert.AreEqual("127.0.0.1", boot.Host, "host 原样取冒号左边");
            Assert.AreEqual(8790, boot.Port, "port 原样取冒号右边");
            Assert.AreEqual("127.0.0.1:8790", boot.Endpoint, "日志行要的就是 host:port");
        }

        [Test]
        public void APortAloneKeepsTheLoopbackHost()
        {
            var boot = BootArgs.Parse(new[] { "-bridge", "9001" });

            Assert.IsTrue(boot.HasBridge, "-bridge port 也是合法写法");
            Assert.AreEqual("127.0.0.1", boot.Host, "只给端口时 host 用本机回环");
            Assert.AreEqual(9001, boot.Port);
        }

        [Test]
        public void ABadEndpointIsRefusedNotGuessed()
        {
            // 非法参数会 LogWarning：先登记，免得它被当成"意外日志"。
            LogAssert.Expect(LogType.Warning, new Regex("\\[boot\\].*foo"));

            var boot = BootArgs.Parse(new[] { "-bridge", "foo" });

            Assert.IsFalse(boot.HasBridge, "解析不出来的地址必须被拒，HasBridge=false");
            Assert.AreEqual(BootArgs.DefaultPort, boot.Port, "被拒之后端口回到默认值，不留半截结果");
            Assert.AreEqual(BootArgs.DefaultHost, boot.Host);
        }

        [Test]
        public void ASelfTestPathKeepsItsSpaces()
        {
            var boot = BootArgs.Parse(new[] { "-selftest", "C:/tmp/a b.txt" });

            Assert.AreEqual("C:/tmp/a b.txt", boot.SelfTestPath, "路径整体是一个参数，空格不能拆开");
            Assert.IsFalse(boot.HasBridge, "没给 -bridge 就不该动桥接地址");
            Assert.IsNull(boot.Token, "没给 -token 时 Token 是 null（交给磁盘令牌）");
        }

        [Test]
        public void NoArgumentsMeansNoOverrides()
        {
            var boot = BootArgs.Parse(new string[0]);

            Assert.IsFalse(boot.HasBridge, "空参数不能假装指定了桥接地址");
            Assert.AreEqual(BootArgs.DefaultHost, boot.Host, "默认落在本机回环");
            Assert.AreEqual(BootArgs.DefaultPort, boot.Port, "默认端口 8790，与 BridgeSettings.DefaultUrl 一致");
            Assert.IsNull(boot.SelfTestPath, "没有 -selftest 时自检路径必须是 null，Task 3 靠它判断要不要进自检");
            Assert.IsNull(boot.Token, "没有 -token 时 Token 是 null");
        }

        // ---- 应用层：RoomBootstrap 那一步就是"找 settings → BootArgs.ApplyTo"。写 Url/Token 的
        // ---- 逻辑全部在这里，所以这几条用例是那个写法的唯一护栏（删掉 ApplyTo 里的行就红）。

        [Test]
        public void ApplyToWritesTheBridgeUrlAndToken()
        {
            var settings = new BridgeSettings { Token = "磁盘令牌" };
            var boot = BootArgs.Parse(new[] { "-bridge", "192.168.1.5:9000", "-token", "handed-over" });

            BootArgs.ApplyTo(settings, boot);

            Assert.AreEqual("ws://192.168.1.5:9000/mas3d/ws", settings.Url,
                "Url 按 ws://host:port/mas3d/ws 拼，路径不能丢");
            Assert.AreEqual("handed-over", settings.Token, "-token 要覆盖磁盘令牌");
        }

        [Test]
        public void ApplyToKeepsTheExistingUrlWhenNoBridgeArgument()
        {
            var settings = new BridgeSettings
            {
                Url = "ws://10.1.2.3:1234/mas3d/ws",
                Token = "磁盘令牌",
            };
            var boot = BootArgs.Parse(new[] { "-selftest", "C:/tmp/a b.txt" });

            BootArgs.ApplyTo(settings, boot);

            Assert.AreEqual("ws://10.1.2.3:1234/mas3d/ws", settings.Url,
                "没给 -bridge 就不许改 Url（改了等于把玩家接到别处）");
            Assert.AreEqual("磁盘令牌", settings.Token, "没给 -token 时保留原令牌，交给磁盘那份");
        }

        [Test]
        public void ApplyToUsesTheLoopbackHostWhenOnlyThePortIsGiven()
        {
            var settings = new BridgeSettings();
            var boot = BootArgs.Parse(new[] { "-bridge", "9001" });

            BootArgs.ApplyTo(settings, boot);

            Assert.AreEqual("ws://127.0.0.1:9001/mas3d/ws", settings.Url, "只给端口时接本机回环");
        }

        [Test]
        public void ATokenThatLooksLikeAFlagIsIgnoredInsteadOfBeingSent()
        {
            // -token -nobody：预扫把 -nobody 当开关吃掉，主循环又把 "-nobody" 当令牌。
            // 带着一个假令牌去撞 401 比不带更糟——错误信息会指向"令牌不对"，而真相是命令行写错了。
            var boot = BootArgs.Parse(new[] { "-token", "-nobody" });

            Assert.IsNull(boot.Token, "看起来像开关的值不算令牌");
            Assert.IsTrue(boot.NoBody, "-nobody 本身照旧生效");
        }
    }
}
