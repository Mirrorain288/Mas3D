using Mas3D.Boot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 打包版唯一的证据是 marker 文件里那几行。身体这一行必须把「有没有身体、多高、什么 shader」
    /// 一次说清：IL2CPP 把 URP 剥掉时材质会变粉，高度会漂，只有 shader 名字能把那种情况抓出来。
    /// 结论按「所有 renderer、所有材质都合格」算：身体里只要有一份材质丢了 shader，屏幕上就有一块
    /// 是粉的，所以「找到一份能用的 shader」不等于「body ok」。
    /// </summary>
    public class PlayerSelfTestBodyTests
    {
        GameObject _player;
        Material _scratchMaterial;

        [TearDown]
        public void TearDown()
        {
            if (_player) Object.DestroyImmediate(_player);
            _player = null;
            if (_scratchMaterial) Object.DestroyImmediate(_scratchMaterial);
            _scratchMaterial = null;
        }

        PlayerBody NewBody()
        {
            _player = new GameObject("Player");
            _player.tag = "Player";
            _player.AddComponent<CharacterController>();
            var motor = _player.AddComponent<PlayerMotor>();
            motor.KeyboardInput = false;
            var rig = _player.AddComponent<PlayerCameraRig>();
            var body = _player.AddComponent<PlayerBody>();
            body.Attach(motor, motor.Head, rig);
            return body;
        }

        [Test]
        public void BodySummaryIsOkWithARealBody()
        {
            if (!PlayerBody.PrefabAvailable)
                Assert.Ignore("缺 " + PlayerBody.BodyPrefabPath + "，跳过身体自检");

            var body = NewBody();
            Assert.IsTrue(body.Spawn(), "Spawn() 失败：" + PlayerBody.BodyPrefabPath);

            var line = PlayerSelfTest.BodySummary(body);

            StringAssert.StartsWith("body ok renderers=", line);
            // BodySummary 用 {1:F3}：真实行固定是三位小数，钉死整段（含 shader= 前的空格），
            // 免得断言只覆盖 " height=1.7" 而放过 "height=1.700" 这类格式漂移。
            StringAssert.Contains(" height=1.700 shader=", line);
        }

        [Test]
        public void BodySummarySaysFailedWhenThereIsNoBody()
        {
            _player = new GameObject("Player");
            _player.tag = "Player";
            var body = _player.AddComponent<PlayerBody>();

            Assert.AreEqual("body failed reason=no-body", PlayerSelfTest.BodySummary(body));
        }

        [Test]
        public void BodySummarySaysFailedWhenTheMaterialLostItsShader()
        {
            if (!PlayerBody.PrefabAvailable)
                Assert.Ignore("缺 " + PlayerBody.BodyPrefabPath + "，跳过身体自检");

            var body = NewBody();
            Assert.IsTrue(body.Spawn(), "Spawn() 失败：" + PlayerBody.BodyPrefabPath);

            // 先证明这具身体本来是 ok 的；否则下面的 failed 也可能来自「模型根本没有 renderer」。
            StringAssert.StartsWith("body ok", PlayerSelfTest.BodySummary(body));

            // 只清 renderer 上的数组，不碰 sharedMaterials 指向的资产本身——改了 shader 名字会污染
            // 同一进程里后面所有用例（VRM 的材质是共享资产，不是这份实例的副本）。
            foreach (var renderer in body.Avatar.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = new Material[0];

            Assert.AreEqual("body failed reason=no-shader", PlayerSelfTest.BodySummary(body));
        }

        /// <summary>
        /// 槽位存在但全是 null：pass 1 只跳过 null 材质，这类身体会走到 :316 的兜底，必须报 no-shader。
        /// 一并钉住 renderers= 的语义——计数的是通过判定的 renderer，而不是成句时真正取到名字的枚数。
        /// </summary>
        [Test]
        public void BodySummarySaysFailedWhenEverySlotIsNull()
        {
            if (!PlayerBody.PrefabAvailable)
                Assert.Ignore("缺 " + PlayerBody.BodyPrefabPath + "，跳过身体自检");

            var body = NewBody();
            Assert.IsTrue(body.Spawn(), "Spawn() 失败：" + PlayerBody.BodyPrefabPath);
            StringAssert.StartsWith("body ok", PlayerSelfTest.BodySummary(body));

            foreach (var renderer in body.Avatar.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = new Material[renderer.sharedMaterials.Length];

            Assert.AreEqual("body failed reason=no-shader", PlayerSelfTest.BodySummary(body));
        }

        [Test]
        public void BodySummarySaysFailedWhenTheShaderIsTheBuiltInErrorShader()
        {
            if (!PlayerBody.PrefabAvailable)
                Assert.Ignore("缺 " + PlayerBody.BodyPrefabPath + "，跳过身体自检");

            // 剥离 URP 的构建里，Unity 交给材质的是非空的 "Hidden/InternalErrorShader"：名字非空，
            // 只判 null/空会把它当 ok（粉红身体报成功）。Hidden/ 前缀一律按丢 shader 处理。
            var errorShader = Shader.Find("Hidden/InternalErrorShader");
            if (errorShader == null)
            {
                Assert.Ignore("Shader.Find(\"Hidden/InternalErrorShader\") 返回 null，构造不出该场景");
                return;
            }

            var body = NewBody();
            Assert.IsTrue(body.Spawn(), "Spawn() 失败：" + PlayerBody.BodyPrefabPath);

            // 先证明这具身体本来是 ok 的：否则下面那句 failed 可能因为别的原因成立，等于什么都没证明。
            StringAssert.StartsWith("body ok", PlayerSelfTest.BodySummary(body));

            // 材质是本用例新建的，只替换 renderer 自己的数组引用；共享的 VRM 材质资产一个字节都不动。
            _scratchMaterial = new Material(errorShader);
            foreach (var renderer in body.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                var slots = new Material[Mathf.Max(1, renderer.sharedMaterials.Length)];
                for (int i = 0; i < slots.Length; i++) slots[i] = _scratchMaterial;
                renderer.sharedMaterials = slots;
            }

            Assert.AreEqual("body failed reason=no-shader", PlayerSelfTest.BodySummary(body));
        }

        /// <summary>
        /// 混材质：第一枚 renderer 留着真 shader，第二枚换成 Hidden/InternalErrorShader。
        /// 旧实现扫到第一枚可用 shader 就 break，会把这具「一半粉红」的身体报成 ok——这条用例挡的就是它。
        /// 顺序是这里自己摆的（先拆掉 Me.vrm 自己的 renderer），不依赖模型里恰好有几枚。
        /// </summary>
        [Test]
        public void BodySummarySaysFailedWhenOneOfTwoRenderersCarriesTheErrorShader()
        {
            if (!PlayerBody.PrefabAvailable)
                Assert.Ignore("缺 " + PlayerBody.BodyPrefabPath + "，跳过身体自检");

            var errorShader = Shader.Find("Hidden/InternalErrorShader");
            if (errorShader == null)
            {
                Assert.Ignore("Shader.Find(\"Hidden/InternalErrorShader\") 返回 null，构造不出该场景");
                return;
            }

            var body = NewBody();
            Assert.IsTrue(body.Spawn(), "Spawn() 失败：" + PlayerBody.BodyPrefabPath);

            // 先证明这具身体本来是 ok 的，后面的 failed 才有意义。
            StringAssert.StartsWith("body ok", PlayerSelfTest.BodySummary(body));

            // 刚报过 ok，所以模型材质里一定挑得出一份可用 shader；只读引用，不改资产。
            var good = FirstUsableMaterial(body.Avatar.transform);
            Assert.IsNotNull(good, "刚报过 body ok 的身体里却挑不出可用材质");

            foreach (var renderer in body.Avatar.GetComponentsInChildren<Renderer>(true))
                Object.DestroyImmediate(renderer);

            var first = NewRenderer(body.Avatar.transform, "body-good");
            first.sharedMaterials = new[] { good };

            _scratchMaterial = new Material(errorShader);
            var second = NewRenderer(body.Avatar.transform, "body-error");
            second.sharedMaterials = new[] { _scratchMaterial };

            Assert.AreEqual("body failed reason=no-shader", PlayerSelfTest.BodySummary(body));
        }

        /// <summary>一枚只有材质槽、没有 mesh 的 renderer：BodySummary 只看 sharedMaterials。</summary>
        static Renderer NewRenderer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<MeshRenderer>();
        }

        /// <summary>挑一份「能用」的材质：非空、shader 非空、名字非空且不是 Hidden/ 回退。</summary>
        static Material FirstUsableMaterial(Transform root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null) continue;
                    var name = material.shader.name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.StartsWith("Hidden/", System.StringComparison.Ordinal)) continue;
                    return material;
                }
            }

            return null;
        }
    }
}
