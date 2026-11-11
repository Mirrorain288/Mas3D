using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 3 of the action-phases plan: the player's own legs fold for crouch, kneel and sit, and
    /// the soles stay on the floor while they do. The table test runs everywhere; the posture tests
    /// need the real Me body and skip when that asset is not checked out.
    /// </summary>
    public class PlayerLegsTests
    {
        private AvatarActor _actor;
        private PlayerMotor _player;
        private ActionDirector _director;
        private PlayerBody _body;
        private float _standingAnkle;

        private static readonly IReadOnlyDictionary<HumanBodyBones, Vector3>[] LegTables =
        {
            ActionPoses.CrouchLegs,
            ActionPoses.KneelLegs,
            ActionPoses.SitLegs,
        };

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();

            _actor = AvatarActor.Spawn();

            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;
            _player.EnsureReady();

            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;

            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null && _director.Current != null) _director.Cancel();
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        /// <summary>The bare player and the director every action test builds.</summary>
        private ActionDirector HandsDirector()
        {
            return _director;
        }

        /// <summary>
        /// The same player, now wearing the real body, crouched and ready for pat-me. Skips when the
        /// Me avatar is not available in this checkout.
        /// </summary>
        private ActionDirector CrouchFixture()
        {
            if (!PlayerBody.PrefabAvailable) Assert.Ignore("missing " + PlayerBody.BodyPrefabPath);

            _body = _player.gameObject.AddComponent<PlayerBody>();
            _body.Attach(_player, _player.Head, null);
            Assert.IsTrue(_body.Spawn(), "the body did not spawn");
            _body.ApplyNow();

            // The foot bone is the ankle, 0.1217 m above the sole on this fitted body (measured
            // standing). Remember that height so the sole can be derived from the bone later.
            _standingAnkle = _body.Avatar.Bone(HumanBodyBones.LeftFoot).position.y - _player.transform.position.y;

            _player.SetPosture(PlayerPosture.Crouching);
            _body.ApplyNow();
            return _director;
        }

        [Test]
        public void TheTableBendsBothKnees()
        {
            foreach (var table in LegTables)
            {
                Assert.IsTrue(table.ContainsKey(HumanBodyBones.LeftLowerLeg),
                    "no left shin in the leg table");
                Assert.IsTrue(table.ContainsKey(HumanBodyBones.LeftFoot),
                    "no left foot in the leg table");
                Assert.IsTrue(table.ContainsKey(HumanBodyBones.RightLowerLeg),
                    "no right shin in the leg table");
                Assert.IsTrue(table.ContainsKey(HumanBodyBones.RightFoot),
                    "no right foot in the leg table");
                Assert.AreEqual(6, table.Count, "a leg table poses both legs, upper leg to foot");

                Assert.Greater(Mathf.Abs(table[HumanBodyBones.LeftLowerLeg].x), 40f,
                    "the left knee does not fold");
                Assert.Greater(Mathf.Abs(table[HumanBodyBones.RightLowerLeg].x), 40f,
                    "the right knee does not fold");
            }
        }

        [Test]
        public void CrouchingBendsThePlayersLegs()
        {
            var d = CrouchFixture();
            Assert.IsTrue(d.TryEnterAction(ActionId.PatMe), "pat-me refused: " + d.LastFaults);
            ActionTestDrive.ToHold(d);

            var rig = d.Body.Avatar;
            Assert.Greater(PlayerLegs.KneeAngle(rig, true), 25f, "the left knee is straight");
            Assert.Greater(PlayerLegs.KneeAngle(rig, false), 25f, "the right knee is straight");
        }

        [Test]
        public void FootStaysOnTheFloor()
        {
            var d = CrouchFixture();
            Assert.IsTrue(d.TryEnterAction(ActionId.PatMe), "pat-me refused: " + d.LastFaults);
            ActionTestDrive.ToHold(d);

            // Bone(LeftFoot) is the ankle joint, not the sole: on this body the ankle stands 0.1217 m
            // above the floor (measured in CrouchFixture while the player was still standing). The
            // sole is therefore the ankle's height above the floor minus that standing height.
            var floor = d.Player.transform.position.y;
            var ankle = d.Body.Avatar.Bone(HumanBodyBones.LeftFoot).position.y - floor;
            var sole = ankle - _standingAnkle;
            Debug.Log(string.Format("[legs] crouch sole={0:F4} m ankle={1:F4} m knee={2:F1} deg",
                sole, ankle, PlayerLegs.KneeAngle(d.Body.Avatar, true)));
            Assert.That(sole, Is.InRange(-0.015f, 0.015f),
                "the left sole is " + sole.ToString("F3") + " m off the floor");
        }

        [Test]
        public void CrouchKeepsTheFootUnderTheHips()
        {
            var d = CrouchFixture();
            Assert.IsTrue(d.TryEnterAction(ActionId.PatMe), "pat-me refused: " + d.LastFaults);
            ActionTestDrive.ToHold(d);

            var rig = d.Body.Avatar;
            var hips = rig.Bone(HumanBodyBones.Hips).position;
            var foot = rig.Bone(HumanBodyBones.LeftFoot).position;
            Assert.Less(Mathf.Abs(foot.x - hips.x), 0.10f, "the foot drifted sideways");
            Assert.Greater(foot.z - hips.z, 0f, "the toe points forward, not through the shin");
        }

        /// <summary>
        /// The stance is applied in one frame, so the fold has to be too: easing it with the phase
        /// weight leaves the soles under the floor for the whole entry. One frame into the entry is
        /// where the old code was 0.57 m down.
        /// </summary>
        [Test]
        public void TheSolesAreOnTheFloorDuringTheEntry()
        {
            var d = CrouchFixture();
            Assert.IsTrue(d.TryEnterAction(ActionId.PatMe), "pat-me refused: " + d.LastFaults);
            ActionTestDrive.Hold(d, 0.1f);   // still inside the 0.9 s entry: the weight is 0

            var ankle = d.Body.Avatar.Bone(HumanBodyBones.LeftFoot).position.y - d.Player.transform.position.y;
            var sole = ankle - _standingAnkle;
            Debug.Log(string.Format("[legs] entry sole={0:F4} m at t={1:F2} s", sole, d.PhaseTime));
            Assert.That(sole, Is.InRange(-0.015f, 0.015f),
                "the fold has to land with the stance, not ease in behind it: sole " + sole.ToString("F3") + " m");
        }

        /// <summary>
        /// Cancel is the only way the legs come back, and nothing else measured it: delete the restore
        /// and every other test still passed.
        /// </summary>
        [Test]
        public void TheCancelPutsEveryLegBackWhereItWas()
        {
            var d = CrouchFixture();
            var rig = d.Body.Avatar;
            var bones = new[]
            {
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            };
            var home = new Quaternion[bones.Length];
            for (var i = 0; i < bones.Length; i++) home[i] = rig.Bone(bones[i]).localRotation;

            Assert.IsTrue(d.TryEnterAction(ActionId.PatMe), "pat-me refused: " + d.LastFaults);
            ActionTestDrive.ToHold(d);
            ActionTestDrive.Hold(d, 0.2f);
            d.Cancel();

            for (var i = 0; i < bones.Length; i++)
                Assert.That(Quaternion.Angle(home[i], rig.Bone(bones[i]).localRotation), Is.LessThan(0.1f),
                    bones[i] + " did not come back to the rotation it entered with");
        }

        /// <summary>
        /// The geometry the sole measurement rests on: every table's three angles sum to zero, which is
        /// what keeps the foot level, both soles land, and the standing ankle is where the body says it
        /// is - a uniformly sunk body would otherwise pass every relative comparison in this file.
        /// The renderer bounds are no use here: a SkinnedMeshRenderer's bounds are not rebuilt without
        /// a render, so a headless tick still reports the bind pose.
        /// </summary>
        [Test]
        public void TheFoldLeavesTheFootLevelAndBothSolesOnTheFloor()
        {
            var legs = new[]
            {
                new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot },
                new[] { HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot },
            };
            foreach (var table in LegTables)
            {
                foreach (var leg in legs)
                {
                    var sum = table[leg[0]].x + table[leg[1]].x + table[leg[2]].x;
                    Assert.That(sum, Is.EqualTo(0f).Within(0.01f),
                        leg[0] + " angles do not sum to zero, so the foot is not level");
                }
            }

            var d = CrouchFixture();

            // Absolute anchor: on the fitted body the ankle joint stands 0.1217 m over the floor. A
            // body sunk by a bad _feetOffset moves this number with it, so this is the check the
            // relative ones cannot make.
            Assert.That(_standingAnkle, Is.InRange(0.10f, 0.14f),
                "the standing ankle is at " + _standingAnkle.ToString("F4") + " m, so the body is not on the floor");

            Assert.IsTrue(d.TryEnterAction(ActionId.PatMe), "pat-me refused: " + d.LastFaults);
            ActionTestDrive.ToHold(d);

            var floor = d.Player.transform.position.y;
            foreach (var bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
            {
                var sole = d.Body.Avatar.Bone(bone).position.y - floor - _standingAnkle;
                Debug.Log(string.Format("[legs] crouch {0} sole={1:F4} m", bone, sole));
                Assert.That(sole, Is.InRange(-0.015f, 0.015f), bone + " is " + sole.ToString("F3") + " m off the floor");
            }
        }

        [Test]
        public void ALegacyRigWithoutABodyDoesNotThrow()
        {
            var d = HandsDirector();
            Assert.DoesNotThrow(() => d.TryEnterAction(ActionId.PatHer));
            ActionTestDrive.ToHold(d);
            Assert.DoesNotThrow(() => d.Cancel());
        }
    }
}
