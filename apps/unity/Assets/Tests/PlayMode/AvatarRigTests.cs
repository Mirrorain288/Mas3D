using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UniVRM10;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 1 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: AvatarRig must
    /// recognise the imported Monkia prefab (bones, expressions, bounds) and know nothing
    /// about the room or the conversation.
    /// </summary>
    public class AvatarRigTests
    {
        AvatarRig _rig;

        [TearDown]
        public void TearDown()
        {
            if (_rig)
            {
                Object.DestroyImmediate(_rig.gameObject);
                _rig = null;
            }
        }

        /// <summary>
        /// Load Monkia through the shared guard: a clean clone has no Assets/_Local, so a missing
        /// local model skips the case instead of reddening it (fix round 2).
        /// </summary>
        static GameObject LoadPrefab()
        {
#if UNITY_EDITOR
            AvatarPrefabFixture.Require();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabFixture.PrefabPath);
            Assert.IsNotNull(prefab, "Monkia.vrm 在本地，但 AssetDatabase 没导入它（在编辑器里重导入一次）："
                                     + AvatarPrefabFixture.PrefabPath);
            return prefab;
#else
            Assert.Ignore("PlayMode rig tests need the Editor asset database (VRM importer).");
            return null;
#endif
        }

        IEnumerator SpawnRig()
        {
            var prefab = LoadPrefab();
            _rig = AvatarRig.Spawn(prefab);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AHandWiredRigBindsInAwake()
        {
            var prefab = LoadPrefab();

            var instance = Object.Instantiate(prefab);
            instance.name = "HandWiredMonkia";
            try
            {
                // 只挂组件，不调用 Spawn：Awake 必须自己认领场景里已经存在的 VRM。
                var rig = instance.AddComponent<AvatarRig>();
                yield return null;

                Assert.IsNotNull(rig.Instance, "手挂的 AvatarRig 必须在 Awake 里认出 Vrm10Instance");
                Assert.IsNotNull(rig.Root, "手挂的 AvatarRig 必须给出根节点");
                Assert.IsNotNull(rig.Bone(HumanBodyBones.Hips), "手挂之后骨骼也要能用");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [UnityTest]
        public IEnumerator ANonVrmObjectIsWarnedNotSilentlyInert()
        {
            var bare = new GameObject("NotAVrm");
            try
            {
                // 没有 VRM 的物体必须报警告（点名对象），而不是安静地假装绑上了。
                LogAssert.Expect(LogType.Warning, new Regex("NotAVrm"));
                var rig = bare.AddComponent<AvatarRig>();
                yield return null;

                Assert.IsNull(rig.Instance, "没有 VRM 就不该假装绑定成功");
                Assert.IsNotNull(rig.Root, "兜底根节点仍是自己的 transform");
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }

        [UnityTest]
        public IEnumerator TheImportedAvatarCarriesTheVrmRuntime()
        {
            var prefab = LoadPrefab();
            var rig = AvatarRig.Spawn(prefab);
            try
            {
                yield return null;
                Assert.IsNotNull(rig.Instance, "Spawn 之后必须拿到 Vrm10Instance");
                Assert.IsNotNull(rig.Animator, "Animator 是 Humanoid 骨骼的入口");
                Assert.IsNotNull(rig.Bone(HumanBodyBones.Hips), "hips 必须存在");
                Assert.IsNotNull(rig.Bone(HumanBodyBones.LeftLowerLeg), "膝盖是坐姿的判据");
                Assert.IsNotNull(rig.Bone(HumanBodyBones.RightUpperArm), "手臂要能放下");
            }
            finally
            {
                Object.DestroyImmediate(rig.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator SheIsOneMetreSixtyOneTallAndArmsOut()
        {
            // SpawnRig loads the prefab one enumerator deeper; requiring here keeps the Ignore inside
            // the test coroutine itself, so a clean clone skips instead of failing on the nested throw.
            AvatarPrefabFixture.Require();
            yield return SpawnRig();
            var size = _rig.Measure().size;
            Assert.That(size.y, Is.InRange(1.50f, 1.75f), "她该有 1.61 m 高，实测 " + size.y);
            Assert.That(size.x, Is.GreaterThan(1.0f), "T-pose 的手臂是张开的，宽度应超过 1 m");
        }

        [UnityTest]
        public IEnumerator AnUnknownExpressionIsReportedNotThrown()
        {
            AvatarPrefabFixture.Require();
            yield return SpawnRig();
            var rig = _rig;
            // Review Focus 3: log every preset this model actually carries, so Tasks 3/4 know
            // which guards are real and which keys are safe to hard-code.
            var keys = rig.Instance.Runtime.Expression.ExpressionKeys;
            var missing = ExpressionKey.CreateCustom("sulk");
            Debug.Log("sulk present=" + rig.HasExpression(missing) + " weight=" + rig.ExpressionWeight(missing)
                      + " keys=" + string.Join(" ", keys));

            Assert.IsFalse(rig.HasExpression(missing), "模型不该有这个表情");
            Assert.DoesNotThrow(() => rig.SetExpression(missing, 0.5f));
            Assert.AreEqual(0f, rig.ExpressionWeight(missing), 1e-4f);

            rig.SetExpression(ExpressionKey.Happy, 2f);
            Assert.AreEqual(1f, rig.ExpressionWeight(ExpressionKey.Happy), 1e-4f, "权重必须 clamp 到 1");
        }
    }
}
