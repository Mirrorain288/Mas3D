using Mas3D.Avatar;
using NUnit.Framework;
using UniVRM10;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 3 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: the idle layer only
    /// stacks offsets on top of a pose somebody else owns. Every case starts from
    /// <see cref="AvatarPose.Sitting"/> and drives <see cref="AvatarIdle.Tick"/> by hand with the
    /// component disabled, so nothing here depends on Update timing.
    /// </summary>
    public class AvatarIdleTests
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

        /// <summary>Spawn Monkia, sit her down, then add a disabled idle so only Tick moves her.</summary>
        AvatarIdle Sit()
        {
#if UNITY_EDITOR
            AvatarPrefabFixture.Require();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabFixture.PrefabPath);
            Assert.IsNotNull(prefab, "Monkia.vrm 在本地，但 AssetDatabase 没导入它（在编辑器里重导入一次）："
                                     + AvatarPrefabFixture.PrefabPath);
#else
            Assert.Ignore("PlayMode idle tests need the Editor asset database (VRM importer).");
            GameObject prefab = null;
#endif
            _rig = AvatarRig.Spawn(prefab);
            AvatarPose.Sitting(_rig);

            var idle = _rig.gameObject.AddComponent<AvatarIdle>();
            idle.Rig = _rig;
            idle.enabled = false;                   // Update 不许插一脚：时间只由 Tick(dt) 推进
            return idle;
        }

        [Test]
        public void BreathingMovesTheChestAndReturnsToTheBaseline()
        {
            var idle = Sit();
            var chest = _rig.Bone(HumanBodyBones.Chest);
            Assert.IsNotNull(chest, "模型上没有 Chest 骨，呼吸无从谈起");

            var rest = chest.localRotation;
            idle.Tick(0.9f);
            var moved = Quaternion.Angle(rest, chest.localRotation);
            Assert.Greater(moved, 0.15f, "胸口没动");

            idle.Tick(1.8f);
            idle.Tick(1.8f);
            var home = Quaternion.Angle(rest, chest.localRotation);
            Debug.Log("[idle] breath peak=" + moved.ToString("0.000") +
                      " after 4.5s=" + home.ToString("0.000") +
                      " period=" + idle.BreathPeriod.ToString("0.0") +
                      " degrees=" + idle.BreathDegrees.ToString("0.0"));
            Assert.Less(home, 0.35f, "呼吸没有回到基线");
        }

        /// <summary>
        /// The weight has to reach the VRM, not just the property: <c>BlinkWeight</c> is set even
        /// when <c>SetExpression</c> is never called, so a case reading the property cannot tell a
        /// blinking avatar from a frozen one.
        /// </summary>
        [Test]
        public void BlinksThenOpens()
        {
            var idle = Sit();
            var peak = 0f;
            var last = 0f;
            for (var i = 0; i < 150; i++)
            {
                idle.Tick(1f / 30f);
                last = _rig.ExpressionWeight(ExpressionKey.Blink);
                peak = Mathf.Max(peak, last);
            }

            Debug.Log("[idle] vrm blink peak=" + peak.ToString("0.000") +
                      " final=" + last.ToString("0.000") +
                      " property=" + idle.BlinkWeight.ToString("0.000") +
                      " blinking=" + idle.Blinking);
            Assert.Greater(peak, 0.5f, "VRM 没收到眨眼：SetExpression 没被调用，只在属性上记了个数");
            Assert.LessOrEqual(peak, 0.9f, "眨眼不该把眼睛全合上（会穿模）");
            Assert.Less(last, 0.05f, "眨完要睁开");
        }

        [Test]
        public void GazeFollowsThePlayerHead()
        {
            var idle = Sit();
            var head = new GameObject("HeadProbe").transform;
            try
            {
                idle.SetGaze(head);
                Assert.AreSame(head, idle.GazeTarget, "GazeTarget 必须记住传进来的头");
                Assert.AreSame(head, _rig.Instance.LookAtTarget, "视线要落到 VRM 运行时上");
                Assert.AreEqual(VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform,
                    _rig.Instance.LookAtTargetType, "只设 Target 不设 Type，眼睛不会跟着走");
            }
            finally
            {
                Object.DestroyImmediate(head.gameObject);
            }
        }

        /// <summary>
        /// <c>BindGaze</c> runs at scene boot, possibly before the VRM runtime exists: SetGaze then
        /// can only remember the target, so the capture has to apply it once the instance is there.
        /// </summary>
        [Test]
        public void GazeSetBeforeTheRigExistsIsAppliedAtCapture()
        {
#if UNITY_EDITOR
            AvatarPrefabFixture.Require();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabFixture.PrefabPath);
            Assert.IsNotNull(prefab, "Monkia.vrm 在本地，但 AssetDatabase 没导入它（在编辑器里重导入一次）："
                                     + AvatarPrefabFixture.PrefabPath);
#else
            Assert.Ignore("PlayMode idle tests need the Editor asset database (VRM importer).");
            GameObject prefab = null;
#endif
            _rig = AvatarRig.Spawn(prefab);
            var head = new GameObject("HeadProbe").transform;
            var host = new GameObject("IdleHost");
            try
            {
                var idle = host.AddComponent<AvatarIdle>();
                idle.enabled = false;                       // Update 不许插一脚
                Assert.IsNull(idle.Rig, "host 上没有 AvatarRig，SetGaze 此刻写不进 VRM");

                idle.SetGaze(head);
                Assert.AreSame(head, idle.GazeTarget, "光线没人可写时也得先把头记住");
                Assert.IsTrue(_rig.Instance.LookAtTarget == null, "Instance 还不存在，VRM 不该被动过");

                idle.Rig = _rig;
                idle.CaptureBaseline();
                Assert.IsTrue(_rig.Instance.LookAtTarget == head, "CaptureBaseline 该补上 SetGaze 时还写不进去的视线");
                Assert.AreEqual(VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform,
                    _rig.Instance.LookAtTargetType, "补写时 Type 也得跟着设");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(head.gameObject);
            }
        }

        [Test]
        public void TheIdleBaselineSurvivesTicks()
        {
            var idle = Sit();
            var thigh = _rig.Bone(HumanBodyBones.LeftUpperLeg);
            Assert.IsNotNull(thigh, "模型上没有大腿骨，坐姿无从谈起");

            var sitRotation = thigh.localRotation;
            for (var i = 0; i < 60; i++) idle.Tick(1f / 30f);

            var drift = Quaternion.Angle(sitRotation, thigh.localRotation);
            Debug.Log("[idle] thigh drift=" + drift.ToString("0.000"));
            Assert.Less(drift, 3f, "Idle 把坐姿抹掉了");
        }

        /// <summary>
        /// Lead ruling after the Task 2 review: the sit table also moves Chest (+2 deg) and
        /// Spine (+4 deg), so the idle must stack on the seated rotation, never on the rest pose.
        /// 1.5 s is half a breath, i.e. the phase where the breath itself is back at zero: a
        /// correct implementation shows the sway only (chest 0.000 / spine 0.773), a rest-baseline
        /// one shows the whole table error (chest 2.000 / spine 4.047), so 1.5 deg bites both.
        /// </summary>
        [Test]
        public void TheSeatedTorsoLeanSurvivesTicks()
        {
            var idle = Sit();
            var chest = _rig.Bone(HumanBodyBones.Chest);
            var spine = _rig.Bone(HumanBodyBones.Spine);
            Assert.IsNotNull(chest, "模型上没有 Chest 骨");
            Assert.IsNotNull(spine, "模型上没有 Spine 骨");

            var chestSit = chest.localRotation;
            var spineSit = spine.localRotation;
            for (var i = 0; i < 45; i++) idle.Tick(1f / 30f);   // 1.5 s = 半个呼吸周期，呼吸正过零

            var chestDrift = Quaternion.Angle(chestSit, chest.localRotation);
            var spineDrift = Quaternion.Angle(spineSit, spine.localRotation);
            Debug.Log("[idle] seated lean drift chest=" + chestDrift.ToString("0.000") +
                      " spine=" + spineDrift.ToString("0.000"));
            Assert.Less(chestDrift, 1.5f, "坐姿的胸口前倾（+2°）被抹掉了：胸口该停在 0.000，不接坐姿是 2.000");
            Assert.Less(spineDrift, 1.5f, "坐姿的脊柱前倾（+4°）被抹掉了：脊柱该只走 0.773 的摆动，不接坐姿是 4.047");
        }

        /// <summary>
        /// The guard re-captures a bone somebody else wrote. It must re-read only that bone: reading
        /// both bakes our own breath angle into the untouched bone's base, and every such event
        /// ratchets the chest up by one breath (+1.1 deg) for good.
        /// </summary>
        [Test]
        public void CapturingAStaleBoneLeavesTheOtherBaseAlone()
        {
            var idle = Sit();
            var chest = _rig.Bone(HumanBodyBones.Chest);
            var spine = _rig.Bone(HumanBodyBones.Spine);
            Assert.IsNotNull(chest, "模型上没有 Chest 骨");
            Assert.IsNotNull(spine, "模型上没有 Spine 骨");

            var chestSit = chest.localRotation;
            idle.Tick(0.75f);                                   // 呼吸峰值：胸口正偏 1.1°
            var peak = Quaternion.Angle(chestSit, chest.localRotation);
            spine.localRotation = spine.localRotation * Quaternion.Euler(1f, 0f, 0f);   // 只有脊柱被外力动了
            idle.Tick(0f);                                      // 守卫只该重抓脊柱
            for (var i = 0; i < 90; i++) idle.Tick(1f / 30f);   // 再走满一个周期，呼吸相位回到零点

            var drift = Quaternion.Angle(chestSit, chest.localRotation);
            Debug.Log("[idle] stale-spine peak=" + peak.ToString("0.000") +
                      " chest after a period=" + drift.ToString("0.000"));
            Assert.Less(drift, 0.05f,
                "脊柱被外力动了，胸口基线也被一起重抓：1.1° 的呼吸角被烘进基线，胸口从此高 1.1°");
        }

        /// <summary>
        /// Somebody writing a driven bone every frame makes the guard want to re-baseline every tick,
        /// and a re-baseline restarts the breath clock: 60 Hz leaves the chest at one tick's worth
        /// (0.038 deg). The debounce has to keep the breath inside its own envelope instead.
        /// </summary>
        [Test]
        public void AForeignPerFrameWriterKeepsTheBreathMoving()
        {
            var idle = Sit();
            var chest = _rig.Bone(HumanBodyBones.Chest);
            var spine = _rig.Bone(HumanBodyBones.Spine);
            Assert.IsNotNull(chest, "模型上没有 Chest 骨");
            Assert.IsNotNull(spine, "模型上没有 Spine 骨");

            var chestSit = chest.localRotation;
            var maximum = 0f;
            for (var i = 0; i < 60; i++)                        // 1 s @ 60 Hz，每帧都有人写脊柱
            {
                spine.localRotation = spine.localRotation * Quaternion.Euler(1f, 0f, 0f);
                idle.Tick(1f / 60f);
                maximum = Mathf.Max(maximum, Quaternion.Angle(chestSit, chest.localRotation));
            }

            Debug.Log("[idle] foreign writer: chest max=" + maximum.ToString("0.000"));
            Assert.GreaterOrEqual(maximum, 0.5f, "每帧被重捕获、时钟每帧归零：呼吸只剩 0.038°");
            Assert.Less(maximum, 1.2f, "重捕获棘轮把胸口一路推高，越过了呼吸包络 ±1.1°");
        }
    }
}
