using System.Collections.Generic;
using Mas3D.Avatar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mas3D.Tests.EditMode
{
    /// <summary>
    /// 眼睛从 VRM 自带的荧光黄绿换成贴图级的柔绿虹膜，三件事必须有机器证据：
    ///
    /// ① 运行时虹膜材质的 _MainTex 真的指向 Assets/_Local/avatars/eyes 里的图，而且落在一份
    ///    **场景副本**上——直接改 VRM 资产的话，下次重新导入模型就全丢了。
    /// ② MToon 的 _Color 是乘算、_ShadeColor 是暗部色：任何一项调暗，画面上那点白高光就会被
    ///    压到 0.90 以下（look-compare 的高光簇判据就是这么量的）；参数贴图关键字更要关掉，
    ///    否则 shift 纹理会把明暗界整个抹平。
    /// ③ 换图必须幂等。CliPlayer.BuildScene 会被反复执行（player / capture / look-shoot 都跑），
    ///    如果每次都给材质再套一份副本，场景里的材质实例数会一路涨上去。
    /// </summary>
    public class EyeLookTests
    {
        const string AvatarPath = AvatarRig.DefaultPrefabPath;

        GameObject _instance;

        [SetUp]
        public void SetUp()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            if (prefab == null) Assert.Ignore("没有 " + AvatarPath + "：干净的 clone 不带这个 17 MB 模型");
            _instance = Object.Instantiate(prefab);
        }

        [TearDown]
        public void TearDown()
        {
            if (_instance != null) Object.DestroyImmediate(_instance);
        }

        static Material IrisMaterial(GameObject avatar)
        {
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (AvatarEyes.IsIrisMaterial(material)) return material;
                }
            }
            return null;
        }

        static int MaterialCount(GameObject avatar)
        {
            // 6000.6 把 GetInstanceID() 标成过时并升级成编译错误，这里用引用相等的集合代替。
            var ids = new HashSet<Material>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material != null) ids.Add(material);
                }
            }
            return ids.Count;
        }

        static float MinChannel(Color colour)
        {
            return Mathf.Min(colour.r, Mathf.Min(colour.g, colour.b));
        }

        static Texture2D ShippedIris()
        {
            var texture = AvatarEyes.LoadIris();
            Assert.IsNotNull(texture, "缺少 " + AvatarEyes.IrisTexturePath + "：先用 node tools/eyes-recolor.mjs 生成");
            return texture;
        }

        [Test]
        public void TheIrisMaterialUsesTheShippedTexture()
        {
            var texture = ShippedIris();
            var before = IrisMaterial(_instance);
            Assert.IsNotNull(before, "VRM 里没找到名字带 EyeIris 的材质");

            var touched = AvatarEyes.Apply(_instance, texture);
            Assert.GreaterOrEqual(touched, 1, "一件虹膜材质都没换");

            var after = IrisMaterial(_instance);
            Assert.AreSame(texture, after.mainTexture, "虹膜材质的 _MainTex 必须换成 eyes/ 里的图");
            Assert.AreNotSame(before, after, "换图要落在场景副本上，不能改 VRM 资产里的材质");
        }

        [Test]
        public void TheIrisCopySwapsItsShadeTextureToo()
        {
            var texture = ShippedIris();
            var before = IrisMaterial(_instance);
            string shadeName = null;
            foreach (var name in AvatarEyes.ShadeTextureNames)
                if (before.HasProperty(name) && before.GetTexture(name) != null) { shadeName = name; break; }
            Assert.IsNotNull(shadeName, "这份虹膜材质没有暗部贴图：下面的断言会变成空转，先确认资产变了");

            AvatarEyes.Apply(_instance, texture);

            var material = IrisMaterial(_instance);
            foreach (var name in AvatarEyes.ShadeTextureNames)
            {
                if (!material.HasProperty(name)) continue;
                if (material.GetTexture(name) == null) continue;
                Assert.AreSame(texture, material.GetTexture(name), "暗部贴图 " + name + " 也必须换成 eyes 里的图");
            }
            Assert.AreSame(texture, material.GetTexture(shadeName), "暗部贴图 " + shadeName + " 没换：虹膜落在阴影里时画面上仍是原图");
        }

        [Test]
        public void TheIrisCopyKeepsItsHighlightBright()
        {
            var texture = ShippedIris();
            AvatarEyes.Apply(_instance, texture);

            var material = IrisMaterial(_instance);
            Assert.IsNotNull(material);
            Assert.IsTrue(material.HasProperty("_ShadeColor"), "这张虹膜应当是 MToon 材质，否则下面的暗部色断言没有意义");

            // 直接断数值：AvatarEyes.Brighten 已经把它们抬到下限，这里量的是「抬过之后确实在」
            // 而不是再调一次同一个判定函数（那样就成了死校验，永远真）。
            var colour = material.GetColor("_Color");
            Assert.GreaterOrEqual(MinChannel(colour), AvatarEyes.MinLit,
                "_Color 是乘算：调暗一点白高光就过不了 0.90，实测 " + colour);

            var shade = material.GetColor("_ShadeColor");
            // 门槛用 MinShade 派生的下限（0.9 倍），不是精确值：Brighten 给冷调时红色通道只抬到
            // MinShade * 0.92，实测最小通道 0.506，写死 0.5 只比它低 0.6%，一调冷调比例就会误红。
            var shadeFloor = AvatarEyes.MinShade * 0.9f;
            Assert.GreaterOrEqual(MinChannel(shade), shadeFloor,
                "_ShadeColor 太黑，暗面那半个虹膜会把高光吃掉（下限 " + shadeFloor + "），实测 " + shade);

            Assert.IsFalse(material.IsKeywordEnabled(AvatarEyes.ParameterMapKeyword),
                ParameterMapHint);
        }

        [Test]
        public void ApplyingTwiceReusesTheSameMaterial()
        {
            var texture = ShippedIris();
            Assert.GreaterOrEqual(AvatarEyes.Apply(_instance, texture), 1, "第一次换图应当动到虹膜材质");

            var first = IrisMaterial(_instance);
            var count = MaterialCount(_instance);

            var second = AvatarEyes.Apply(_instance, texture);
            Assert.AreEqual(0, second, "第二次调用不许再动任何材质");
            Assert.AreSame(first, IrisMaterial(_instance), "第二次调用不许再造一份副本");
            Assert.AreEqual(count, MaterialCount(_instance), "材质实例数必须保持不变");
        }

        const string ParameterMapHint =
            "_MTOON_PARAMETERMAP 打开时会去采样 shift 贴图，多数材质那张图是空的、恒取 1，" +
            "等于把明暗分界抹平（AvatarShading 关它的原因），这里必须同样关掉";
    }
}