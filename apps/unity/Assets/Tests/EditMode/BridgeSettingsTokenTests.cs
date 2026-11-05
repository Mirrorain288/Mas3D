using System;
using System.IO;
using Mas3D;
using Mas3D.Net;
using NUnit.Framework;

namespace Mas3D.Tests
{
    /// <summary>
    /// P6 Task 5：打包件找不到令牌（双击即连不成立）。候选表是纯函数，这里钉住顺序与"空目录跳过"，
    /// 再钉住"逐个试、取第一个非空"——顺序错了，双击就会去读一个不存在的路径。
    /// </summary>
    public class BridgeSettingsTokenTests
    {
        static string TempDir(string name)
        {
            return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mas3d-token-tests", name));
        }

        static string NewSandbox()
        {
            var root = Path.Combine(Path.GetTempPath(), "mas3d-token-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        [Test]
        public void CandidatesAreRepoThenExeThenPersistent()
        {
            var exeDir = TempDir("build");
            var repoRoot = Path.GetDirectoryName(exeDir);
            var persistent = TempDir("persistent");

            var candidates = BridgeSettings.TokenCandidates(exeDir, repoRoot, persistent);

            Assert.AreEqual(3, candidates.Length);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(repoRoot, "apps", "bridge", "data", "mas3d", "token")), candidates[0]);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(exeDir, "mas3d-token")), candidates[1]);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(persistent, "token")), candidates[2]);
        }

        [Test]
        public void MissingDirectoriesAreSkippedInsteadOfGuessed()
        {
            var candidates = BridgeSettings.TokenCandidates(null, string.Empty, null);
            Assert.AreEqual(0, candidates.Length);
        }

        [Test]
        public void SamePathResolvedTwiceIsKeptOnce()
        {
            var exeDir = TempDir("build");
            var repoRoot = Path.GetDirectoryName(exeDir);

            // persistentDataPath 指到 apps/bridge/data/mas3d 时，它的 token 就是仓库里那份；
            // 同一个绝对路径只留第一次出现的位置，免得同一个文件被读两遍。
            var persistent = Path.Combine(repoRoot, "apps", "bridge", "data", "mas3d");
            var candidates = BridgeSettings.TokenCandidates(exeDir, repoRoot, persistent);

            Assert.AreEqual(2, candidates.Length);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(repoRoot, "apps", "bridge", "data", "mas3d", "token")), candidates[0]);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(exeDir, "mas3d-token")), candidates[1]);
        }

        [Test]
        public void ReadFirstTokenSkipsEmptyFilesAndReportsThePath()
        {
            var root = NewSandbox();
            try
            {
                var repo = Path.Combine(root, "repo", "token");
                var beside = Path.Combine(root, "build", "mas3d-token");
                Directory.CreateDirectory(Path.GetDirectoryName(repo));
                Directory.CreateDirectory(Path.GetDirectoryName(beside));
                File.WriteAllText(repo, "   \r\n");
                File.WriteAllText(beside, "  abcdef0123  \r\n");

                string used;
                var token = DialogueBridge.ReadFirstToken(new[] { repo, beside }, out used);

                Assert.AreEqual("abcdef0123", token, "空文件不算令牌，要接着试下一条；读回来的内容要 trim");
                Assert.AreEqual(beside, used);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ReadFirstTokenReportsNoneWhenNoCandidateHasContent()
        {
            var root = NewSandbox();
            try
            {
                string used;
                var token = DialogueBridge.ReadFirstToken(
                    new[] { Path.Combine(root, "nope", "token"), Path.Combine(root, "also-nope", "mas3d-token") },
                    out used);

                Assert.AreEqual(string.Empty, token);
                Assert.AreEqual(DialogueBridge.NoTokenSource, used);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
