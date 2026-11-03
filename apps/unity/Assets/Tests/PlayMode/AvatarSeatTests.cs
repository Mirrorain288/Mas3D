using System;
using System.Collections;
using Mas3D.Avatar;
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 2 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: sitting is data
    /// (AvatarPose) plus one placement (AvatarSeat). These four cases pin the seat height, the
    /// deliberately dangling ankles, the direction the knees point, the absence of penetration and
    /// the way back to standing.
    /// </summary>
    public class AvatarSeatTests
    {
        const string RoomPath = "Assets/_Local/room/mita-room.glb";
        const string BenchName = "Mesh_0001out";

        AvatarRig _rig;
        GameObject _room;

        [TearDown]
        public void TearDown()
        {
            if (_rig) UnityEngine.Object.DestroyImmediate(_rig.gameObject);
            if (_room) UnityEngine.Object.DestroyImmediate(_room);
            _rig = null;
            _room = null;
        }

        static AnchorTable Table()
        {
            var table = AnchorTable.Load();
            Assert.IsNotNull(table, "anchors.json missing; run tools/unity.ps1 player first");
            Assert.IsNotNull(table.Seat("S1"), "anchor table has no S1 seat");
            return table;
        }

        AvatarSeat Spawn(SeatEntry s1)
        {
#if UNITY_EDITOR
            AvatarPrefabFixture.Require();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabFixture.PrefabPath);
            Assert.IsNotNull(prefab, "Monkia.vrm 在本地，但 AssetDatabase 没导入它（在编辑器里重导入一次）："
                                     + AvatarPrefabFixture.PrefabPath);
#else
            Assert.Ignore("PlayMode seat tests need the Editor asset database (VRM importer).");
            GameObject prefab = null;
#endif
            _rig = AvatarRig.Spawn(prefab);
            var seat = _rig.gameObject.AddComponent<AvatarSeat>();
            seat.Rig = _rig;
            seat.Sit(s1);
            return seat;
        }

        /// <summary>Instantiate the room and drop it onto the floor exactly the way CliPlayer does.</summary>
        void BuildRoom()
        {
#if UNITY_EDITOR
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPath);
            Assert.IsNotNull(prefab, "room prefab missing: " + RoomPath);
            _room = UnityEngine.Object.Instantiate(prefab);
#else
            Assert.Ignore("PlayMode seat test needs the Editor asset database (glTFast prefab).");
#endif
            _room.name = "room-under-test";
            var bounds = Bounds(_room);
            _room.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            Physics.SyncTransforms();
        }

        static Bounds Bounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.Greater(renderers.Length, 0, "room has no renderers");
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        /// <summary>
        /// 悬空的两只脚是<b>故意的</b>，不是没做完。座面 0.604 把髋关节钉在 s1.y + HipsGap
        /// （S1 = 0.754），而小腿只有 0.407 m：想让鞋底落地，髋得降到 ≈0.506，那会把臀部埋进
        /// 座面 11 cm（Lead 裁决 2026-11-03）。2.6 的「脚掌贴地」因此废弃，验收判据改为
        /// 「踝部自然下垂、鞋底离地 0.15–0.19 m 且不穿地板」；本用例钉住踝高，再用
        /// Measure().min.y 兜住穿地板。
        /// </summary>
        [UnityTest]
        public IEnumerator SittingPutsTheHipsOnTheBench()
        {
            var s1 = Table().Seat("S1");
            var seat = Spawn(s1);
            yield return null;                              // 等一帧，让 UniVRM 的 LateUpdate 跑完再量
            Assert.AreEqual(s1.y + AvatarSeat.HipsGap, seat.HipsWorld.y, 0.02f, "髋部没落在座面上");
            Assert.AreEqual(s1.x, seat.HipsWorld.x, 0.03f, "横向偏出座面");
            Assert.AreEqual(s1.z, seat.HipsWorld.z, 0.03f, "纵深偏出座面");
            Assert.IsTrue(seat.Seated);

            // 脚故意悬空（见 XML doc）：只钉「自然下垂的踝高」，不要求落地。
            var footL = _rig.Bone(HumanBodyBones.LeftFoot);
            var footR = _rig.Bone(HumanBodyBones.RightFoot);
            Assert.IsNotNull(footL, "左脚骨不存在，踝高无从量起");
            Assert.IsNotNull(footR, "右脚骨不存在，踝高无从量起");
            var ankleL = footL.position.y;
            var ankleR = footR.position.y;
            Assert.That(ankleL, Is.InRange(0.15f, 0.35f),
                "左脚踝悬空高度不对：实测 " + ankleL.ToString("F3") + " m，应在 0.15–0.35 m（鞋底离地 0.15–0.19 m）");
            Assert.That(ankleR, Is.InRange(0.15f, 0.35f),
                "右脚踝悬空高度不对：实测 " + ankleR.ToString("F3") + " m，应在 0.15–0.35 m（鞋底离地 0.15–0.19 m）");
            Assert.GreaterOrEqual(_rig.Measure().min.y, -0.01f, "脚不能穿地板");
        }

        [UnityTest]
        public IEnumerator SittingPutsTheKneesInFrontOfTheHips()
        {
            var s1 = Table().Seat("S1");
            Spawn(s1);
            yield return null;
            var forward = Quaternion.Euler(0f, s1.facingY, 0f) * Vector3.forward;
            var hips = _rig.Bone(HumanBodyBones.Hips).position;
            var knee = _rig.Bone(HumanBodyBones.LeftLowerLeg).position;
            var ahead = Vector3.ProjectOnPlane(knee - hips, Vector3.up);
            Assert.Greater(Vector3.Dot(ahead.normalized, forward), 0.15f, "膝盖朝后了：大腿转反了方向");
        }

        /// <summary>
        /// The body does not enter the seat. Criterion scope (Lead ruling, 2026-11-03):
        /// footprint = the bench renderer's <b>strict</b> world AABB (no 2 cm expansion);
        /// vertices = skeleton-driven skin only — each vertex is attributed to the bone with the
        /// largest weight in <c>boneWeights</c>, and <c>J_Sec_</c> spring-bone chains (hair, skirt)
        /// are skipped. Cloth and hair on this model hang below the seat top in any pose and the
        /// pose table cannot move them, so they are logged, never asserted.
        /// </summary>
        [UnityTest]
        public IEnumerator NothingSinksIntoTheBench()
        {
            var s1 = Table().Seat("S1");
            BuildRoom();
            Spawn(s1);
            yield return null;

            var benchObject = GameObject.Find(BenchName);
            Assert.IsNotNull(benchObject, "长凳没找到：" + BenchName);
            var bench = benchObject.GetComponent<Renderer>();
            Assert.IsNotNull(bench, BenchName + " 上没有渲染器");

            var hipsBone = _rig.Bone(HumanBodyBones.Hips);
            Assert.IsNotNull(hipsBone, "rig 上没有 Hips 骨，坐姿无从谈起");
            var thighLBone = _rig.Bone(HumanBodyBones.LeftUpperLeg);
            var thighRBone = _rig.Bone(HumanBodyBones.RightUpperLeg);

            var b = bench.bounds;                        // 严格 AABB：不再 ±0.02 外扩
            var seatTop = b.max.y;
            var hips = new Lowest();
            var thigh = new Lowest();
            var body = new Lowest();
            var skirt = new Lowest();
            var hair = new Lowest();

            var baked = new Mesh();
            try
            {
                foreach (var smr in _rig.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var shared = smr.sharedMesh;
                    if (shared == null) continue;
                    var weights = shared.boneWeights;
                    var names = BoneNames(smr);
                    smr.BakeMesh(baked, true);
                    var vertices = baked.vertices;
                    var t = smr.transform;
                    for (var i = 0; i < vertices.Length; i++)
                    {
                        var w = t.TransformPoint(vertices[i]);
                        if (w.x < b.min.x || w.x > b.max.x) continue;   // 严格足迹
                        if (w.z < b.min.z || w.z > b.max.z) continue;
                        var bone = Dominant(names, weights, i);
                        if (bone.StartsWith(SecondaryPrefix, StringComparison.Ordinal))
                        {
                            // 弹簧骨布料/头发：只记数，不参与“身体不进入座面”的断言。
                            if (bone.IndexOf("Skirt", StringComparison.Ordinal) >= 0) skirt.Consider(w, bone);
                            else if (bone.IndexOf("Hair", StringComparison.Ordinal) >= 0) hair.Consider(w, bone);
                            continue;
                        }

                        body.Consider(w, bone);
                        if (bone == hipsBone.name) hips.Consider(w, bone);
                        else if ((thighLBone != null && bone == thighLBone.name) ||
                                 (thighRBone != null && bone == thighRBone.name)) thigh.Consider(w, bone);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }

            // 四个数（hips / thigh / 座面 / 裙摆+骨名）连同取证点一起进日志和每条断言消息：
            // Debug.Log 只落在编辑器日志里，断言消息会随失败进 NUnit XML，回归可只读 XML 诊断。
            var numbers =
                $"hipsLowest={hips.Y:F3} thighLowest={thigh.Y:F3} seatTop={seatTop:F3} " +
                $"skirtLowest={skirt.Y:F3} skirtBone={skirt.Bone} " +
                $"bodyLowest={body.Y:F3} bodyBone={body.Bone} " +
                $"hairLowest={hair.Y:F3} hairBone={hair.Bone} " +
                $"hipsAt={Fmt(hips.At)} thighAt={Fmt(thigh.At)} skirtAt={Fmt(skirt.At)} " +
                $"bench={Fmt(b.min)}..{Fmt(b.max)}";
            Debug.Log("[bench] " + numbers);

            Assert.Less(hips.Y, float.PositiveInfinity,
                "hips 没有落在长凳足迹内，髋部断言会空转（她没坐在凳上）：" + numbers);
            Assert.GreaterOrEqual(hips.Y, seatTop - 0.005f,
                "髋部坐进座面（允许 5 mm 蒙皮插值）：" + numbers);
            Assert.Less(body.Y, float.PositiveInfinity,
                "没有骨架顶点落在长凳足迹内，这一条是空转的：" + numbers);
            Assert.GreaterOrEqual(body.Y, seatTop - 0.02f, "有身体顶点穿到座面以下：" + numbers);
            Assert.GreaterOrEqual(_rig.Measure().min.y, -0.01f, "脚不能穿地板");
        }

        /// <summary>Spring-bone chains: hair and skirt are cloth, not skeleton.</summary>
        const string SecondaryPrefix = "J_Sec_";

        /// <summary>Lowest vertex seen inside the bench footprint, with the bone that owns it.</summary>
        sealed class Lowest
        {
            public float Y = float.PositiveInfinity;
            public Vector3 At;
            public string Bone = "none";

            public void Consider(Vector3 world, string bone)
            {
                if (world.y >= Y) return;
                Y = world.y;
                At = world;
                Bone = bone;
            }
        }

        static string[] BoneNames(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            if (bones == null) return new string[0];
            var names = new string[bones.Length];
            for (var i = 0; i < bones.Length; i++) names[i] = bones[i] != null ? bones[i].name : "?";
            return names;
        }

        /// <summary>
        /// The bone that owns one baked vertex: the largest weight of its <c>BoneWeight</c>, mapped
        /// through <c>smr.bones</c>. "?" when the mesh carries no weights — counted as body, so a
        /// mesh that cannot be attributed never slips past the assert.
        /// </summary>
        static string Dominant(string[] names, BoneWeight[] weights, int index)
        {
            if (weights == null || index >= weights.Length) return "?";
            var bw = weights[index];
            var best = bw.weight0;
            var bone = bw.boneIndex0;
            if (bw.weight1 > best) { best = bw.weight1; bone = bw.boneIndex1; }
            if (bw.weight2 > best) { best = bw.weight2; bone = bw.boneIndex2; }
            if (bw.weight3 > best) { best = bw.weight3; bone = bw.boneIndex3; }
            return bone >= 0 && bone < names.Length ? names[bone] : "?";
        }

        static string Fmt(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";

        /// <summary>
        /// 站起来要还原<b>姿势</b>，不只是根节点。髋高由根节点决定，空转的
        /// <see cref="AvatarPose.Standing"/> 也能让髋高断言通过；所以先站着她抓一份基线，
        /// 坐下再站起后逐根比对骨骼旋转，外加左脚回到站立高度。
        /// </summary>
        [UnityTest]
        public IEnumerator StandingRestoresThePose()
        {
            var s1 = Table().Seat("S1");
#if UNITY_EDITOR
            AvatarPrefabFixture.Require();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabFixture.PrefabPath);
            Assert.IsNotNull(prefab, "Monkia.vrm 在本地，但 AssetDatabase 没导入它（在编辑器里重导入一次）："
                                     + AvatarPrefabFixture.PrefabPath);
#else
            Assert.Ignore("PlayMode seat tests need the Editor asset database (VRM importer).");
            GameObject prefab = null;
#endif
            // 先站着她，量一次基线，再坐再站，站起来必须回到同一套旋转和同一个高度。
            _rig = AvatarRig.Spawn(prefab);
            yield return null;
            var rest = AvatarPose.Capture(_rig);
            var standingHips = _rig.Bone(HumanBodyBones.Hips).position.y;
            var standingFoot = _rig.Bone(HumanBodyBones.LeftFoot).position.y;
            var thighL = _rig.Bone(HumanBodyBones.LeftUpperLeg);
            var thighR = _rig.Bone(HumanBodyBones.RightUpperLeg);
            var shinL = _rig.Bone(HumanBodyBones.LeftLowerLeg);
            var chest = _rig.Bone(HumanBodyBones.Chest);
            var spine = _rig.Bone(HumanBodyBones.Spine);
            var seat = _rig.gameObject.AddComponent<AvatarSeat>();
            seat.Rig = _rig;
            seat.Sit(s1);
            yield return null;
            seat.Stand(new Vector3(0.105f, 0f, -0.024f), s1.facingY);
            yield return null;

            Assert.AreEqual(standingHips, _rig.Bone(HumanBodyBones.Hips).position.y, 0.01f, "站起来没恢复");
            var thighLAngle = Quaternion.Angle(rest[HumanBodyBones.LeftUpperLeg], thighL.localRotation);
            var thighRAngle = Quaternion.Angle(rest[HumanBodyBones.RightUpperLeg], thighR.localRotation);
            var shinLAngle = Quaternion.Angle(rest[HumanBodyBones.LeftLowerLeg], shinL.localRotation);
            var chestAngle = Quaternion.Angle(rest[HumanBodyBones.Chest], chest.localRotation);
            var spineAngle = Quaternion.Angle(rest[HumanBodyBones.Spine], spine.localRotation);
            var footY = _rig.Bone(HumanBodyBones.LeftFoot).position.y;
            Assert.Less(thighLAngle, 1f,
                "左大腿没回到站姿：偏 " + thighLAngle.ToString("F2") + "°（只看髋高的话，空的 Standing 也会通过）");
            Assert.Less(thighRAngle, 1f, "右大腿没回到站姿：偏 " + thighRAngle.ToString("F2") + "°");
            Assert.Less(shinLAngle, 1f, "左小腿没回到站姿：偏 " + shinLAngle.ToString("F2") + "°");
            Assert.Less(chestAngle, 1f, "胸骨没回到站姿：偏 " + chestAngle.ToString("F2") + "°");
            Assert.Less(spineAngle, 1f, "脊柱没回到站姿：偏 " + spineAngle.ToString("F2") + "°");
            Assert.Less(Mathf.Abs(standingFoot - footY), 0.02f,
                "左脚没回到站立高度：站 " + standingFoot.ToString("F3") + " m，站起后 " + footY.ToString("F3") + " m");
            Assert.IsFalse(seat.Seated);
        }
    }
}
