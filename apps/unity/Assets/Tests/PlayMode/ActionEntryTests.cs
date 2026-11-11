using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    /// <summary>
    /// The entry contract: an approach walks the player from wherever they stand to the mark, a
    /// settle places them on it in one frame, and Escape mid-walk leaves from the reached point and
    /// still comes home exactly.
    /// </summary>
    public class ActionEntryTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;
        GameObject _floor;
        GameObject _wall;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();

            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // the entry has to take a walkable player off the keyboard
            _player.EnsureReady();

            // The walk needs a surface: the controller sweeps the step, and without a floor the only
            // thing under the capsule is the path's own height.
            _floor = new GameObject("Floor");
            var box = _floor.AddComponent<BoxCollider>();
            box.size = new Vector3(12f, 0.2f, 12f);
            box.center = new Vector3(0f, -0.1f, 0f);

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
            if (_floor != null) Object.Destroy(_floor);
            if (_wall != null) Object.Destroy(_wall);
            yield return null;
        }

        /// <summary>The mark hands resolves to: her seat plus the plan's own 0.62 m.</summary>
        Vector3 HandsSpot()
        {
            var seat = AnchorTable.Load().Seat("S1");
            return new Vector3(seat.x, 0f, seat.z)
                + Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward * ActionDirector.HandsOffset;
        }

        /// <summary>A point <paramref name="metres"/> in front of that mark, on her facing line.</summary>
        Vector3 HandsStaging(float metres)
        {
            var seat = AnchorTable.Load().Seat("S1");
            var forward = Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward;
            return HandsSpot() + forward * metres;
        }

        [Test]
        public void HandsEntryWalksThePlayerInInsteadOfTeleporting()
        {
            var start = HandsStaging(1.2f);
            _player.transform.position = start;

            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.Hold(_director, 0.2f);

            // A step of the walk, not the whole distance. The old entry teleported the full 1.2 m on
            // the first frame, so this is the assertion that tells a walk from a snap.
            Assert.That(Vector3.Distance(_player.transform.position, start), Is.LessThan(0.4f),
                "0.2 s into a 2.6 s entry the player must still be on the way, not already at the mark");

            ActionTestDrive.ToHold(_director);
            Assert.That(Vector3.Distance(_player.transform.position, _director.LastSpot), Is.LessThan(0.06f),
                "the entry must end on the mark the director resolved");
            Assert.That(Vector3.Distance(_player.transform.position, HandsSpot()), Is.LessThan(0.06f),
                "and that mark is the seat's own 0.62 m point, not a spot of the director's choosing");
        }

        [Test]
        public void KissAndSitTogetherDoNotTravel()
        {
            foreach (var id in new[] { ActionId.Kiss, ActionId.SitTogether })
            {
                if (id == ActionId.Kiss) _player.SetPosture(PlayerPosture.Kneeling);
                else _director.Cancel();   // the kiss is still running; the next entry needs an idle director

                Assert.IsTrue(_director.TryEnterAction(id), id + ": " + _director.LastFaults);

                // A settle is on the mark on frame one; the entry may pose her, but it must not take
                // a single step after the placement.
                var settled = _player.transform.position;
                Assert.That(Vector3.Distance(settled, _director.LastSpot), Is.LessThan(0.001f),
                    id + " must start on the mark it resolved");

                ActionTestDrive.ToHold(_director);
                Assert.That(Vector3.Distance(_player.transform.position, settled), Is.LessThan(0.001f),
                    id + " is a settle: its entry must not move the player after the first frame");
            }
        }

        [Test]
        public void ABlockedWalkIsSnappedOntoTheMark()
        {
            // A wall between the walk and its mark: the capsule cannot be swept through it, so the
            // entry has to end with the documented snap and say the gap out loud. Without the snap
            // the hold would start with the feet short of where the pose measures them.
            var spot = HandsSpot();
            var start = HandsStaging(1.2f);
            _player.transform.position = start;

            _wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _wall.name = "Blocker";
            _wall.transform.position = spot + (start - spot).normalized * 0.35f + Vector3.up * 1f;
            _wall.transform.rotation = Quaternion.LookRotation((start - spot).normalized, Vector3.up);
            _wall.transform.localScale = new Vector3(2f, 2f, 0.2f);   // 2 m wide, 0.2 m thick: no way round

            LogAssert.Expect(LogType.Log, new Regex(@"^\[stage\] approach snapped \d+\.\d{3}$"));

            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.ToHold(_director);

            Assert.That(Vector3.Distance(_player.transform.position, spot), Is.LessThan(0.02f),
                "whatever the capsule could not sweep must be closed by the snap before the hold");
        }

        [Test]
        public void EscapeDuringEntryLeavesFromWhereTheWalkReached()
        {
            var home = HandsStaging(1.2f);
            _player.transform.position = home;

            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.Hold(_director, 1.3f);   // halfway: the interrupt lands mid-walk
            var mid = _player.transform.position;
            Assert.That(Vector3.Distance(mid, home), Is.GreaterThan(0.05f), "precondition: the walk has moved");
            Assert.That(Vector3.Distance(mid, HandsSpot()), Is.GreaterThan(0.2f),
                "1.3 s into a 2.6 s walk is still most of the way out; a teleport would already be on the mark");

            Assert.IsTrue(_director.RequestLeave(), "a walking entry must be interruptible");
            ActionTestDrive.Hold(_director, 0.2f);
            Assert.That(Vector3.Distance(_player.transform.position, mid), Is.LessThan(0.001f),
                "the leave plays in place; it must not finish the walk");

            ActionTestDrive.ToLeaveEnd(_director);
            Assert.That(Vector3.Distance(_player.transform.position, home), Is.LessThan(0.02f),
                "the end of the leave still puts the player back where the entry found them");
        }
    }
}
