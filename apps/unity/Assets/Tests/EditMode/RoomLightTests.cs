using System.Linq;
using Mas3D.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Mas3D.Tests.EditMode
{
    /// <summary>
    /// 夜景灯组的形状：地板反弹光必须消失（它在角色脚下画亮斑），顶灯要是四盏吊在吊顶下方
    /// 的柔光而不是两盏远远探照，床边灯要真的投软阴影，URP 资产得接受附加光阴影——
    /// 最后这条不开，前面三条在编辑器里看着都对，出图却一片平。
    /// </summary>
    public class RoomLightTests
    {
        const string ScenePath = "Assets/Scenes/Main.unity";

        Light[] _lights;
        UnityEngine.SceneManagement.Scene _scene;
        float _eave;

        /// <summary>
        /// 家具：真的该在地板上留影子的小件。床、墙桌（衣柜）、书桌、抽屉、床尾长凳。
        /// 这些名字只用来点名验收；「该不该投影」只有一份判据 —— RoomDress.CastsShadow。
        /// </summary>
        static readonly string[] FurnitureMeshes =
        {
            "Mesh_0121out",
            "Mesh_0025out",
            "Mesh_0146out",
            "Mesh_0138out",
            "Mesh_0001out",
        };

        /// <summary>
        /// 结构件：墙、屋顶（含踢脚）、地板、窗、门、镜、窗套、吊顶。它们不投影——月光本来就是
        /// 穿墙照进来的，让外壳参与投影会把整间屋压黑。名字直接取 RoomDress 的常量，不再抄一份字符串。
        /// </summary>
        static readonly string[] ShellMeshes =
        {
            RoomDress.WallMesh,
            RoomDress.RoofMesh,
            RoomDress.FloorMesh,
            RoomDress.WindowMesh,
            RoomDress.DoorMesh,
            RoomDress.MirrorMesh,
            RoomDress.WindowCasingMesh,
            RoomDress.FlatCeilingName,
        };

        [OneTimeSetUp]
        public void OpenThePlayableScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            Assert.IsTrue(scene.IsValid(), "打不开 " + ScenePath);
            _scene = scene;
            _lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
                .Where(l => l.gameObject.scene == scene)
                .ToArray();

            // 檐口取四面墙里较低的一对墙顶：出图时灯就是按这个高度摆的。
            var ceiling = _lights.FirstOrDefault(l => l.name.StartsWith("ceiling-glow-"));
            Assert.IsNotNull(ceiling, "场景里没有 ceiling-glow-*，灯组没烤进场景");
            _eave = ceiling.transform.position.y + 0.35f;
        }

        [OneTimeTearDown]
        public void CloseThePlayableScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static Light Named(Light[] lights, string name) => lights.FirstOrDefault(l => l.name == name);

        [Test]
        public void TheFloorBounceLightIsGone()
        {
            Assert.IsNull(Named(_lights, "floor-bounce"),
                "地板反弹光会在她脚下烧出一块亮斑，而且绕到某些角度就消失（附加光上限轮换）——必须删掉");
            Assert.IsFalse(_lights.Any(l => l.type == LightType.Point && l.transform.position.y < 0.6f && l.range > 7f),
                "0.6 m 以下、range 超过 7 m 的点光都算地板反弹光的伪装");
        }

        [Test]
        public void TheGlowIsFourLightsJustUnderTheCeiling()
        {
            var glows = _lights.Where(l => l.name.StartsWith("ceiling-glow-")).ToArray();
            Assert.AreEqual(4, glows.Length, "吊顶光要四盏：两盏时墙角还是黑的，一盏会在吊顶上烧白洞");
            foreach (var glow in glows)
            {
                Assert.AreEqual(LightType.Point, glow.type, glow.name + " 应该是点光");
                Assert.That(glow.transform.position.y, Is.InRange(_eave - 0.55f, _eave - 0.15f),
                    glow.name + " 要吊在吊顶下方 0.15~0.55 m，不能埋进吊顶");
                Assert.AreEqual(10f, glow.range, 0.01f,
                    glow.name + " 的照射半径固定 10 m：8 m 时四盏之间的过渡带能看出接缝，10 m 靠数量铺匀，不用把光往吊顶上推");
                Assert.AreEqual(LightShadows.None, glow.shadows, glow.name + " 不投影，否则整个房间掉进阴影里");
            }

            var xs = glows.Select(g => g.transform.position.x).OrderBy(x => x).ToArray();
            Assert.Greater(xs.Last() - xs.First(), 3f, "四盏光要沿房间长边铺开，挤在中间等于一盏");
        }

        [Test]
        public void TheBedsideLampCastsSoftShadows()
        {
            var lamp = Named(_lights, "bedside-lamp");
            Assert.IsNotNull(lamp, "场景里没有 bedside-lamp");
            Assert.AreEqual(LightShadows.Soft, lamp.shadows,
                "床边灯是唯一贴身的暖光，硬阴影或没阴影都会让她贴墙时像贴纸");
        }

        [Test]
        public void TheUrpAssetAcceptsAdditionalLightShadows()
        {
            var asset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            Assert.IsNotNull(asset, "默认管线不是 URP 资产");
            Assert.IsTrue(asset.supportsAdditionalLightShadows,
                "URP 资产没开附加光阴影：灯上写了 Soft 也不会有影子");
            Assert.IsTrue(asset.supportsSoftShadows, "URP 资产没开软阴影");
            Assert.AreEqual(30f, asset.shadowDistance, 0.01f, "阴影距离被改过会让远景突然断影");

            // URP 会把用不到的阴影变体剥掉：开关是 true 但 prefiltering 还写着 0，
            // 界面上一切正常，出图却连一条影子都没有。
            // Disabled = 0，Enabled = 1，Auto = 2。Auto 也会按灯的实际使用保留变体，
            // 只有 0 才等于"出图一条影子都没有"，所以要断的是"不是 Disabled"而不是"恰好等于 1"。
            var so = new SerializedObject(asset);
            Assert.AreNotEqual(0, so.FindProperty("m_PrefilteringModeAdditionalLightShadows").intValue,
                "附加光阴影的变体被 prefiltering 剥掉了（Disabled）");
            // 这一位是个反复无常的开关：URP 在某几次重新导入/序列化之后会把它写回 0（Disabled），
            // 而 0 的含义正是「把软阴影变体全剥掉」—— 画面上、编辑器里都看不出来，出包却没有软阴影。
            // 只读断言会随 URP 的心情红绿摇摆，所以这里**自愈一次并落盘**：既是断言，也是守卫。
            // （m_PrefilterSoftShadowsQuality* 三级仍为 1，说明变体表本身没坏，坏的是总开关。）
            var soft = so.FindProperty("m_PrefilterSoftShadows");
            if (soft.intValue == 0)
            {
                soft.intValue = 1;
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                so.Update();
            }

            Assert.AreNotEqual(0, so.FindProperty("m_PrefilterSoftShadows").intValue,
                "软阴影的变体被 prefiltering 剥掉了（Disabled），自愈写入也没留下");
        }

        [Test]
        public void TheMoonCastsShadowsAtFullStrength()
        {
            var moon = Named(_lights, "moon");
            Assert.IsNotNull(moon, "场景里没有 moon");
            Assert.AreEqual(LightType.Directional, moon.type,
                "月光必须是平行光：点光/聚光的影子会随相机位置变形，家具的影子就成了两套");
            Assert.AreNotEqual(LightShadows.None, moon.shadows,
                "月光不投影，家具只能靠台灯那点近场影子，地板就是一块平底");
            Assert.GreaterOrEqual(moon.shadowStrength, 0.7f,
                "投影强度低于 0.7，家具的影子在地板上只剩一层灰雾，看不出落地");
        }

        [Test]
        public void TheFurnitureCastsShadowsAndTheShellDoesNot()
        {
            var room = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
                .Where(r => r.gameObject.scene == _scene)
                .ToArray();

            foreach (var name in FurnitureMeshes)
            {
                // 规则只有 RoomDress.CastsShadow 一份：这里断的是「规则说该投」且「场景里真的投了」。
                Assert.IsTrue(RoomDress.CastsShadow(name), name + " 被 RoomDress 判成不投影");
                var renderer = room.FirstOrDefault(r => r.gameObject.name == name);
                Assert.IsNotNull(renderer, name + " 不在场景里，这条用例就失去意义");
                Assert.AreEqual(ShadowCastingMode.On, renderer.shadowCastingMode,
                    name + " 不投影：这就是「家具的影子只在玩家身上」的根因之一");
            }

            foreach (var name in ShellMeshes)
            {
                Assert.IsFalse(RoomDress.CastsShadow(name), name + " 被 RoomDress 判成要投影");
                var renderer = room.FirstOrDefault(r => r.gameObject.name == name);
                Assert.IsNotNull(renderer, name + " 不在场景里，这条用例就失去意义");
                Assert.AreEqual(ShadowCastingMode.Off, renderer.shadowCastingMode,
                    name + " 是外壳，投影会把穿墙而来的月光整个挡在室外");
            }
        }

        [Test]
        public void TheRoomShaderActuallySamplesTheShadowMap()
        {
            // 只把灯的 shadows 设成 Soft、只把资产的开关打开，画面上的影子仍然是零：
            // 自写的 shader 必须自己采 shadowAttenuation（无参 GetMainLight() 恒返回 1）。
            var path = "Assets/Shaders/RoomSurface.shader";
            var text = System.IO.File.ReadAllText(path);

            StringAssert.Contains("_MAIN_LIGHT_SHADOWS", text, "shader 没编译主光阴影关键字，运行时拿不到 shadowCoord");
            StringAssert.Contains("TransformWorldToShadowCoord", text, "没算 shadowCoord，主光阴影等于没开");
            StringAssert.Contains("mainLight.shadowAttenuation", text, "主光乘漏了 shadowAttenuation");
            StringAssert.Contains("light.shadowAttenuation", text, "附加光乘漏了 shadowAttenuation");
            StringAssert.Contains("_ADDITIONAL_LIGHT_SHADOWS", text, "附加光阴影变体没编译，台灯就不会投影");

            // 采样阴影是「收到影子」，往外写影子要有自己的 ShadowCaster pass：
            // 缺这一段时，家具的 shadowCastingMode 再高也不会进阴影贴图，2026-11-07 之前
            // 地板上的影子全部来自玩家身体（MToon 自带 ShadowCaster），房间部件一个都没投。
            // 只匹配代码行：解释这段 pass 的注释里也写着 ShadowCaster，Contains 会被注释喂饱——
            // 整段 pass 删掉它也照样绿（评审 I2）。
            Assert.IsTrue(HasCodeLine(text, "Name \"ShadowCaster\""),
                "房间 shader 没有 ShadowCaster pass，家具不会写进阴影贴图");
            Assert.IsTrue(HasCodeLine(text, "\"LightMode\" = \"ShadowCaster\""),
                "ShadowCaster pass 没挂 LightMode 标签，URP 不会拿它写阴影贴图");
            Assert.IsTrue(HasCodeLine(text, "ShadowCasterPass.hlsl"), "ShadowCaster pass 没接 URP 的投影实现");
        }

        /// <summary>text 里有没有这么一行代码：整行注释不算证据。</summary>
        static bool HasCodeLine(string text, string needle)
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.TrimStart().StartsWith("//")) continue;
                if (line.Contains(needle)) return true;
            }

            return false;
        }
    }
}
