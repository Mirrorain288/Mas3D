using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 4 of docs/superpowers/plans/2026-11-08-action-phases.md: a hold must never read as a
    /// freeze-frame. While an action holds, small sinusoidal offsets ride on her head, her spine and
    /// her hands, and on his spine. The offsets are pure functions of the phase clock - no random, no
    /// accumulating state - so they are bounded, periodic and frame-rate independent, and the contact
    /// the pose solved for stays inside <see cref="ActionDirector.ContactTolerance"/>.
    ///
    /// The fixture is the hands one copied from ActionPhaseTests: hands has the longest entry (2.6 s)
    /// and its table writes neither the spine nor the head, which is exactly where an offset applied
    /// on top of the previous frame's value would compound instead of oscillate.
    /// </summary>
    public class PerformanceMotionTests
    {
        const float Step = ActionTestDrive.Step;
        const float Tau = 2f * Mathf.PI;

        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;
        PlayerCameraRig _camera;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // the fixture must be able to walk, or Cancel returns what doing nothing returns
            _player.EnsureReady();
            var camGo = new GameObject("CameraRig");
            _camera = camGo.AddComponent<PlayerCameraRig>();
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.Camera = _camera;
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
            if (_camera != null) Object.Destroy(_camera.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        /// <summary>Frame-rate independence: one second of hold, stepped at 60 Hz and at 30 Hz.</summary>
        [UnityTest]
        public IEnumerator TheBreathDoesNotDependOnTheFrameRate()
        {
            var head = _actor.Rig.Bone(HumanBodyBones.Head);
            var spine = _actor.Rig.Bone(HumanBodyBones.Spine);

            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.ToHold(_director);
            var start = _director.PhaseTime;
            var headAtStart = head.localRotation;
            var spineAtStart = spine.localRotation;
            for (var i = 0; i < 60; i++) _director.Tick(Step);
            var headFast = Quaternion.Angle(headAtStart, head.localRotation);
            var spineFast = Quaternion.Angle(spineAtStart, spine.localRotation);
            var slow = _director.PhaseTime - start;

            _director.Cancel();
            yield return null;

            // Same action, same elapsed phase time, half the frames: the offsets must land in the same
            // place, so the deflection is a function of time and not of how often it was written.
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.ToHold(_director);
            var slowStart = _director.PhaseTime;
            var headSlowStart = head.localRotation;
            var spineSlowStart = spine.localRotation;
            for (var i = 0; i < slow / (2f * Step); i++) _director.Tick(2f * Step);
            var headSlow = Quaternion.Angle(headSlowStart, head.localRotation);
            var spineSlow = Quaternion.Angle(spineSlowStart, spine.localRotation);

            Assert.AreEqual(headFast, headSlow, 0.15f, "head deflection must be a function of elapsed time, not of frame count");
            Assert.AreEqual(spineFast, spineSlow, 0.15f, "spine deflection must be a function of elapsed time, not of frame count");
            Debug.Log("[perfmotion] one second of hold at 60 Hz vs 30 Hz: head " + headFast.ToString("F4") + " vs "
                + headSlow.ToString("F4") + " deg, spine " + spineFast.ToString("F4") + " vs " + spineSlow.ToString("F4") + " deg");
        }

        /// <summary>
        /// The whole point: a held pose visibly moves. The brief's illustrative test sampled exactly
        /// 2 s and demanded 0.3 deg; with these formulas the head covers only ~0.3 deg in that window,
        /// so the test would be a coin flip. Six seconds and a 0.5 deg floor measure the same intent
        /// without the knife edge.
        /// </summary>
        [UnityTest]
        public IEnumerator TheHoldNeverFreezes()
        {
            var head = _actor.Rig.Bone(HumanBodyBones.Head);
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.ToHold(_director);

            var spine = _actor.Rig.Bone(HumanBodyBones.Spine);
            var leftHand = _actor.Rig.Bone(HumanBodyBones.LeftHand);
            var rightHand = _actor.Rig.Bone(HumanBodyBones.RightHand);
            var first = head.localRotation;
            var firstSpine = spine.localRotation;
            var firstLeft = leftHand.localRotation;
            var firstRight = rightHand.localRotation;
            var maxAngle = 0f;
            var maxSpine = 0f;
            var maxLeft = 0f;
            var maxRight = 0f;
            var maxContact = 0f;
            for (var i = 0; i < 360; i++)   // 6 s of hold
            {
                _director.Tick(Step);
                maxAngle = Mathf.Max(maxAngle, Quaternion.Angle(first, head.localRotation));
                maxSpine = Mathf.Max(maxSpine, Quaternion.Angle(firstSpine, spine.localRotation));
                maxLeft = Mathf.Max(maxLeft, Quaternion.Angle(firstLeft, leftHand.localRotation));
                maxRight = Mathf.Max(maxRight, Quaternion.Angle(firstRight, rightHand.localRotation));
                maxContact = Mathf.Max(maxContact, _director.ContactError);
            }

            Assert.Greater(maxAngle, 0.5f, "a held head that never moves is a freeze-frame (measured " + maxAngle.ToString("F3") + " deg)");
            // One channel per bone is not a performance: dropping the spine write or either hand write
            // left this test green, because the head alone carries it.
            Assert.Greater(maxSpine, 0.3f, "her spine must lean while the pose holds (measured " + maxSpine.ToString("F3") + " deg)");
            Assert.Greater(maxLeft, 0.1f, "her left hand must close while the pose holds (measured " + maxLeft.ToString("F3") + " deg)");
            Assert.Greater(maxRight, 0.1f, "her right hand must close while the pose holds (measured " + maxRight.ToString("F3") + " deg)");
            Assert.LessOrEqual(maxContact, ActionDirector.ContactTolerance,
                "micro-motion must stay inside the contact budget (worst " + maxContact.ToString("F4") + " m)");
            Assert.Greater(_director.Held, 5.9f, "every one of those 6 s must still credit contact");
            Debug.Log("[perfmotion] hands, 6 s of hold: head " + maxAngle.ToString("F3") + " deg, spine "
                + maxSpine.ToString("F3") + " deg, hands " + maxLeft.ToString("F3") + "/" + maxRight.ToString("F3")
                + " deg, worst contact " + maxContact.ToString("F4") + " m of "
                + ActionDirector.ContactTolerance.ToString("F3") + " m");
            yield return null;   // iterator only: every assertion above is synchronous
        }

        /// <summary>Every offset is a sine of fixed amplitude: the caps are the amplitude, forever.</summary>
        [Test]
        public void TheOffsetsStayInsideTheirCaps()
        {
            var maxHeadX = 0f;
            var maxHeadZ = 0f;
            var maxSpineZ = 0f;
            var maxHand = 0f;
            var maxHis = 0f;

            for (var t = 0f; t < 120f; t += 0.37f)   // 120 s: the common period of head and spine is 100 s
            {
                var head = PerformanceMotion.HerHead(t);
                maxHeadX = Mathf.Max(maxHeadX, Mathf.Abs(head.x));
                maxHeadZ = Mathf.Max(maxHeadZ, Mathf.Abs(head.z));
                maxSpineZ = Mathf.Max(maxSpineZ, Mathf.Abs(PerformanceMotion.HerSpine(t).z));
                maxHand = Mathf.Max(maxHand, Mathf.Abs(PerformanceMotion.HerHand(true, t).z));
                maxHand = Mathf.Max(maxHand, Mathf.Abs(PerformanceMotion.HerHand(false, t).z));
                maxHis = Mathf.Max(maxHis, Mathf.Abs(PerformanceMotion.HisSpine(t).x));
            }

            // Literal numbers, not the implementation's own constants: retuning a cap must break this.
            Assert.LessOrEqual(maxHeadX, 1.2f + 1e-3f, "head X must never leave +/-1.2 deg");
            Assert.LessOrEqual(maxHeadZ, 0.6f + 1e-3f, "head Z must never leave +/-0.6 deg");
            Assert.LessOrEqual(maxSpineZ, 0.8f + 1e-3f, "her spine must never lean past +/-0.8 deg");
            Assert.LessOrEqual(maxHand, 2.0f + 1e-3f, "hands must never tighten past 2 deg");
            Assert.LessOrEqual(maxHis, 0.6f + 1e-3f, "his spine must never leave +/-0.6 deg");
        }

        /// <summary>Periodic to the last decimal: each function returns to its own start on its own period.</summary>
        [Test]
        public void TheOffsetsArePeriodic()
        {
            Assert.AreEqual(PerformanceMotion.HerSpine(0f).z, PerformanceMotion.HerSpine(1f / 0.11f).z, 1e-2f, "spine period is 1/0.11 s");
            var head0 = PerformanceMotion.HerHead(0f);
            var head100 = PerformanceMotion.HerHead(100f);
            Assert.AreEqual(head0.x, head100.x, 1e-2f, "0.07 Hz returns to its start every 100 s");
            Assert.AreEqual(head0.z, head100.z, 1e-2f, "0.05 Hz returns to its start every 100 s");
            Assert.AreEqual(PerformanceMotion.HerHand(true, 0f).z, PerformanceMotion.HerHand(true, 3.7f).z, 1e-2f, "hands breathe every 3.7 s");
            Assert.AreEqual(PerformanceMotion.HisSpine(0f).x, PerformanceMotion.HisSpine(6.25f).x, 1e-2f, "his spine breathes every 6.25 s");
        }

        /// <summary>
        /// Smooth and bounded: no frame may jump further than the amplitude times the phase step. This
        /// is what a per-frame delta of "at least something, at most the cap" is really guarding.
        /// </summary>
        [Test]
        public void NoSingleFrameJumps()
        {
            var headCapX = 1.2f * Tau * 0.07f * Step;
            var headCapZ = 0.6f * Tau * 0.05f * Step;
            var spineCap = 0.8f * Tau * 0.11f * Step;
            var handCap = 2f * Tau / 3.7f * Step;
            var hisCap = 0.6f * Tau * 0.16f * Step;

            var maxHeadX = 0f;
            var maxHeadZ = 0f;
            var maxSpine = 0f;
            var maxHand = 0f;
            var maxHis = 0f;
            var minHeadX = float.PositiveInfinity;

            for (var t = 0f; t < 12f; t += Step)
            {
                var head = PerformanceMotion.HerHead(t + Step) - PerformanceMotion.HerHead(t);
                var spine = PerformanceMotion.HerSpine(t + Step) - PerformanceMotion.HerSpine(t);
                var hand = PerformanceMotion.HerHand(true, t + Step) - PerformanceMotion.HerHand(true, t);
                var his = PerformanceMotion.HisSpine(t + Step) - PerformanceMotion.HisSpine(t);
                maxHeadX = Mathf.Max(maxHeadX, Mathf.Abs(head.x));
                maxHeadZ = Mathf.Max(maxHeadZ, Mathf.Abs(head.z));
                maxSpine = Mathf.Max(maxSpine, Mathf.Abs(spine.z));
                minHeadX = Mathf.Min(minHeadX, Mathf.Abs(head.x));
                maxHand = Mathf.Max(maxHand, Mathf.Abs(hand.z));
                maxHis = Mathf.Max(maxHis, Mathf.Abs(his.x));
            }

            Assert.LessOrEqual(maxHeadX, headCapX + 1e-4f, "head X jumped more than one 60 Hz step of 0.07 Hz allows");
            Assert.LessOrEqual(maxHeadZ, headCapZ + 1e-4f, "head Z jumped more than one 60 Hz step of 0.05 Hz allows");
            Assert.LessOrEqual(maxSpine, spineCap + 1e-4f, "spine jumped more than one 60 Hz step of 0.11 Hz allows");
            Assert.LessOrEqual(maxHand, handCap + 1e-4f, "hands jumped more than one 60 Hz step of 3.7 s allows");
            Assert.LessOrEqual(maxHis, hisCap + 1e-4f, "his spine jumped more than one 60 Hz step of 0.16 Hz allows");

            // §5.5 asks for motion on every held frame, not merely a bounded total: a step function
            // (jump once, then sit still) would satisfy every cap above and still be a freeze-frame.
            Assert.Greater(minHeadX, 0f, "the head must move on every 60 Hz frame, however slightly");
        }

        /// <summary>A rig that is not there (the bare-capsule fixtures, a torn-down actor) is a no-op.</summary>
        [Test]
        public void NoRigNoMotion()
        {
            Assert.DoesNotThrow(() => PerformanceMotion.Apply(null, ActionId.Hands, 1f));
            Assert.DoesNotThrow(() => PerformanceMotion.Apply(null, ActionId.SitTogether, 12f));
            Assert.DoesNotThrow(() => PerformanceMotion.ApplyHis(null, 1f));
            Assert.DoesNotThrow(() => PerformanceMotion.Restore(null));
            Assert.DoesNotThrow(PerformanceMotion.Forget);
        }

        /// <summary>
        /// Two minutes of holding. The head and spine oscillate around the frame the hold began on, so
        /// at the 100 s common period they must be back where they started; an offset stacked on the
        /// previous frame's value instead would have walked away by tens of degrees.
        /// </summary>
        [UnityTest]
        public IEnumerator TheHoldDoesNotDriftOverTwoMinutes()
        {
            var head = _actor.Rig.Bone(HumanBodyBones.Head);
            var spine = _actor.Rig.Bone(HumanBodyBones.Spine);
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.ToHold(_director);

            var headStart = head.localRotation;
            var spineStart = spine.localRotation;
            var maxHead = 0f;
            var maxSpine = 0f;
            var headAtPeriod = 0f;
            var spineAtPeriod = 0f;
            var maxContact = 0f;

            for (var i = 0; i < 7200; i++)   // 120 s
            {
                _director.Tick(Step);
                maxHead = Mathf.Max(maxHead, Quaternion.Angle(headStart, head.localRotation));
                maxSpine = Mathf.Max(maxSpine, Quaternion.Angle(spineStart, spine.localRotation));
                maxContact = Mathf.Max(maxContact, _director.ContactError);
                // 100 s: 1/0.07, 1/0.11 and 1/0.16 all divide it, so the head and the spine come back to
                // their start frames. The hands do not - 3.7 s does not divide 100 - which is why only
                // the head and the spine are read here, and why the bound is 0.02 and not exact.
                if (i == 5999)
                {
                    headAtPeriod = Quaternion.Angle(headStart, head.localRotation);
                    spineAtPeriod = Quaternion.Angle(spineStart, spine.localRotation);
                }
            }

            // Peak-to-peak: both figures are measured from the entry frame, so a clean oscillation
            // reaches twice its half-swing, plus one 60 Hz step of slack for the sample hitting near it.
            Assert.LessOrEqual(maxHead, 2.4f + 0.05f, "the head's peak-to-peak swing must stay at twice its half-swing (drifted to " + maxHead.ToString("F3") + " deg)");
            Assert.LessOrEqual(maxSpine, 1.6f + 0.05f, "the spine's peak-to-peak swing must stay at twice its half-swing (drifted to " + maxSpine.ToString("F3") + " deg)");
            Assert.LessOrEqual(headAtPeriod, 0.02f, "after 100 s of holding the head must be back on its first frame (off by " + headAtPeriod.ToString("F4") + " deg)");
            Assert.LessOrEqual(spineAtPeriod, 0.02f, "after 100 s of holding the spine must be back on its first frame (off by " + spineAtPeriod.ToString("F4") + " deg)");
            Assert.LessOrEqual(maxContact, ActionDirector.ContactTolerance,
                "the breath must never break the contact the solve holds (worst " + maxContact.ToString("F4") + " m)");
            Assert.Greater(_director.Held, 119f, "the contact must hold for the whole two minutes");
            Debug.Log("[perfmotion] 120 s of hold: max head " + maxHead.ToString("F4") + " deg, max spine "
                + maxSpine.ToString("F4") + " deg, worst contact " + maxContact.ToString("F4")
                + " m, at 100 s head off by " + headAtPeriod.ToString("F5")
                + " deg and spine by " + spineAtPeriod.ToString("F5") + " deg");
            yield return null;   // iterator only
        }

        /// <summary>
        /// The kiss has no IK: nothing pulls her head back after the offsets move it, and the pose
        /// starts only 1.34 cm from his face against a 2 cm tolerance. This is the test that says
        /// whether the breath may run at full amplitude in the one action that cannot absorb it.
        /// </summary>
        [UnityTest]
        public IEnumerator TheKissKeepsContactThroughTheBreath()
        {
            _player.SetPosture(PlayerPosture.Kneeling);
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss), _director.LastFaults);
            ActionTestDrive.ToHold(_director);

            var maxGap = 0f;
            var minGap = float.PositiveInfinity;
            for (var i = 0; i < 7200; i++)   // 120 s: the common period of every frequency in the breath
            {
                _director.Tick(Step);
                maxGap = Mathf.Max(maxGap, _director.ContactError);
                minGap = Mathf.Min(minGap, _director.ContactError);
            }

            Assert.LessOrEqual(maxGap, ActionDirector.ContactTolerance,
                "the breath must not break the kiss (worst gap " + maxGap.ToString("F4") + " m against a 0.02 m tolerance)");
            // A cap on its own is satisfied by switching the breath off in this action, which would
            // make the one performance with no IK the one performance that freezes. Pin both ends.
            Assert.Greater(minGap, 0.010f, "the breath must not drive her face into his (closest " + minGap.ToString("F4") + " m)");
            Assert.Greater(maxGap - minGap, 0.002f,
                "the kiss must still move - a scale of zero would keep the gap constant and pass the cap");
            Assert.GreaterOrEqual(_director.Held, 119f, "the hold must credit the whole two minutes");
            Debug.Log("[perfmotion] kiss, 120 s of hold: gap runs " + minGap.ToString("F4") + " to "
                + maxGap.ToString("F4") + " m of " + ActionDirector.ContactTolerance.ToString("F3") + " m");
            yield return null;   // iterator only
        }

        /// <summary>
        /// Her spine has a driver of its own (AvatarIdle sways it every Update). The breath has to take
        /// the bone over, or the idle rewrites it between two ticks and the pose flickers; Cancel must
        /// hand it back, since the idle is what keeps her alive once the action is over.
        /// </summary>
        [UnityTest]
        public IEnumerator TheIdleStepsOffTheBreathingSpine()
        {
            var spine = _actor.Rig.Bone(HumanBodyBones.Spine);
            var home = spine.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.ToHold(_director);

            // 40 frames of hold with the idle ticking in between.
            for (var i = 0; i < 40; i++)
            {
                _director.Tick(Step);
                yield return null;
            }

            Assert.IsTrue(ActionPose.Claims(HumanBodyBones.Spine),
                "the breath owns the spine while it holds, so AvatarIdle must be told to skip it");

            var written = spine.localRotation;
            yield return null;   // the only writer left this frame is AvatarIdle.Update
            Assert.Less(Quaternion.Angle(written, spine.localRotation), 0.02f,
                "AvatarIdle rewrote the spine the breath owns");

            _director.Cancel();
            yield return null;
            yield return null;
            Assert.IsFalse(ActionPose.Claims(HumanBodyBones.Spine), "Cancel must hand the spine back to the idle");
            // The worst the breath ever leaves on the bone is one full lean (0.8 deg), so 1.2 was a
            // bound a missing hand-back would still pass. 0.05 is the entry pose or nothing.
            Assert.Less(Quaternion.Angle(home, spine.localRotation), 0.05f, "after Cancel the spine must be back at its entry pose");
        }

        /// <summary>
        /// The unit-level twin of the hold test. The director fixture never builds a player body, so
        /// <see cref="PerformanceMotion.ApplyHis"/> and the hand-back in
        /// <see cref="PerformanceMotion.Restore"/> had no coverage at all: delete either and every
        /// other test in this file stayed green. A second avatar stands in for his skeleton - the API
        /// only asks for a rig, and this is the cheapest real one.
        /// </summary>
        [UnityTest]
        public IEnumerator TheBreathWritesEveryBoneItClaims()
        {
            var her = _actor.Rig;
            var his = AvatarActor.Spawn();
            try
            {
                var bones = new[]
                {
                    her.Bone(HumanBodyBones.Spine), her.Bone(HumanBodyBones.Head),
                    her.Bone(HumanBodyBones.LeftHand), her.Bone(HumanBodyBones.RightHand),
                };
                var labels = new[] { "her spine", "her head", "her left hand", "her right hand" };
                var entry = new Quaternion[bones.Length];
                for (var b = 0; b < bones.Length; b++) entry[b] = bones[b].localRotation;

                var hisSpine = his.Rig.Bone(HumanBodyBones.Spine);
                var hisEntry = hisSpine.localRotation;

                var moved = new float[bones.Length];
                var hisMoved = 0f;
                for (var t = 0f; t < 12f; t += 0.05f)   // more than one period of every channel
                {
                    PerformanceMotion.Apply(her, ActionId.Hands, t);
                    PerformanceMotion.ApplyHis(his.Rig, t);
                    for (var b = 0; b < bones.Length; b++)
                        moved[b] = Mathf.Max(moved[b], Quaternion.Angle(entry[b], bones[b].localRotation));
                    hisMoved = Mathf.Max(hisMoved, Quaternion.Angle(hisEntry, hisSpine.localRotation));
                }

                // Half the cap, so a channel written at the wrong amplitude (or not at all) fails.
                Assert.Greater(moved[0], 0.3f, labels[0] + " was never written");
                Assert.Greater(moved[1], 0.5f, labels[1] + " was never written");
                Assert.Greater(moved[2], 0.1f, labels[2] + " was never written");
                Assert.Greater(moved[3], 0.1f, labels[3] + " was never written");
                Assert.Greater(hisMoved, 0.3f, "his spine was never written");

                PerformanceMotion.Restore(his.Rig);
                Assert.Less(Quaternion.Angle(hisEntry, hisSpine.localRotation), 0.01f,
                    "Restore must hand his spine back, or the last held frame stays in his skeleton forever");
                Debug.Log("[perfmotion] writes over 12 s: spine " + moved[0].ToString("F3") + ", head "
                    + moved[1].ToString("F3") + ", hands " + moved[2].ToString("F3") + "/" + moved[3].ToString("F3")
                    + ", his spine " + hisMoved.ToString("F3") + " deg");
            }
            finally
            {
                ActionPose.Forget();
                if (his != null) Object.Destroy(his.gameObject);
            }
            yield return null;
        }
    }
}
