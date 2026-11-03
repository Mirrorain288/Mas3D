using System.IO;
using Mas3D.Avatar;
using NUnit.Framework;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// One guard shared by every PlayMode fixture that needs her model. Monkia.vrm lives under
    /// Assets/_Local/avatars, which apps/unity/.gitignore drops from the repository (17 MB,
    /// non-redistributable), so a clean clone has no model: a missing local asset must Ignore the
    /// case, never red the suite. AvatarSceneTests already did this for the shipped scene; fix round 2
    /// moved the check here so the rig, seat, idle and mood fixtures share one path constant and one
    /// message instead of five copies of File.Exists.
    /// </summary>
    public static class AvatarPrefabFixture
    {
        /// <summary>Must equal <see cref="AvatarRig.DefaultPrefabPath"/>; Require() pins the two together.</summary>
        public const string PrefabPath = "Assets/_Local/avatars/Monkia.vrm";

        /// <summary>Application.dataPath is &lt;project&gt;/Assets whatever the process CWD is.</summary>
        public static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        public static string FullPath
        {
            get { return Path.Combine(ProjectRoot, PrefabPath); }
        }

        /// <summary>True when the local model is not on disk: a clean clone, or a machine without it.</summary>
        public static bool Missing
        {
            get { return !File.Exists(FullPath); }
        }

        /// <summary>
        /// Skip the calling case when the model is absent. Call it before loading the prefab, so a
        /// missing local asset never reaches an Assert.IsNotNull.
        /// </summary>
        public static void Require()
        {
            VerifyRootOnce();
            if (Missing)
            {
                Assert.Ignore("缺 " + PrefabPath + "：从 apps/unity/.gitignore 忽略的本地资产里拷回来"
                              + "（17 MB 的 VRoid 模型不进仓库）");
            }
        }

        /// <summary>
        /// Fix round 2 asked for one self-check that the guard can actually see the model: a guard
        /// that always answered "missing" would silently skip the whole avatar suite and look green.
        /// Resolve the root from Application.dataPath (the process CWD belongs to the runner, not the
        /// project), prove it really is the project root, and log both paths once per run so the green
        /// run records missing=False.
        /// </summary>
        static void VerifyRootOnce()
        {
            if (RootVerified) return;
            RootVerified = true;

            var root = ProjectRoot;
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "Assets")),
                "守卫的工程根算错了（Application.dataPath 的父目录里没有 Assets）：" + root);
            Assert.AreEqual(AvatarRig.DefaultPrefabPath, PrefabPath,
                "守卫的路径与 AvatarRig.DefaultPrefabPath 漂移了");
            Debug.Log("[avatar] prefab guard root=" + root
                      + " cwd=" + Directory.GetCurrentDirectory()
                      + " prefab=" + FullPath
                      + " missing=" + (File.Exists(FullPath) ? "False" : "True"));
        }

        static bool RootVerified;
    }
}
