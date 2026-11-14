using NUnit.Framework;
using UnityEngine;
using Mas3D.Room;

namespace Mas3D.Tests
{
    /// <summary>
    /// Unit tests for InteractiveDoor (Task 1.1) and DoorInteractor (Task 1.2).
    /// </summary>
    public class DoorTests
    {
        static GameObject CreateDoorObject(string name, Vector3 position, Vector3 boxCenter, Vector3 boxSize, out InteractiveDoor door)
        {
            var pivot = new GameObject(name);
            pivot.transform.position = position;

            var leaf = new GameObject("leaf");
            leaf.transform.SetParent(pivot.transform, false);
            var box = leaf.AddComponent<BoxCollider>();
            box.center = boxCenter;
            box.size = boxSize;

            door = pivot.AddComponent<InteractiveDoor>();
            door.DoorId = name;
            door.RefreshColliders();
            return pivot;
        }

        [Test]
        public void InitialStateIsClosedWithCollidersEnabled()
        {
            Assert.AreEqual(0f, InteractiveDoor.ClosedAngle, 1e-4f);
            Assert.AreEqual(-90f, InteractiveDoor.OpenAngle, 1e-4f);

            var root = CreateDoorObject("Door_Test", Vector3.zero, new Vector3(0f, 1f, -0.45f), new Vector3(0.1f, 2f, 0.9f), out var door);
            try
            {
                Assert.IsFalse(door.IsOpen);
                Assert.IsFalse(door.IsMoving);
                Assert.IsFalse(door.WaitingClearance);
                Assert.IsTrue(door.AreCollidersEnabled);
                Assert.AreEqual(0f, door.CurrentAngle, 1e-4f);
                Assert.AreEqual(0f, root.transform.localEulerAngles.y, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ToggleOpensSmoothlyToMinus90AndDisablesCollidersDuringSwing()
        {
            var root = CreateDoorObject("Door_Test", Vector3.zero, new Vector3(0f, 1f, -0.45f), new Vector3(0.1f, 2f, 0.9f), out var door);
            try
            {
                door.SwingDuration = 0.4f;
                door.Toggle();

                Assert.IsTrue(door.IsOpen);
                Assert.IsTrue(door.IsMoving);
                Assert.IsFalse(door.AreCollidersEnabled, "摆动期必须关闭门体碰撞以免卡住或弹飞玩家");

                door.Tick(0.2f);
                Assert.IsTrue(door.IsMoving);
                Assert.Less(door.CurrentAngle, -5f);
                Assert.Greater(door.CurrentAngle, -85f);
                Assert.IsFalse(door.AreCollidersEnabled, "尚未到位时碰撞保持关闭");

                door.Tick(0.25f);
                Assert.IsFalse(door.IsMoving);
                Assert.AreEqual(-90f, door.CurrentAngle, 1e-3f);
                Assert.AreEqual(270f, Mathf.Repeat(root.transform.localEulerAngles.y, 360f), 1e-2f);
                Assert.IsTrue(door.AreCollidersEnabled, "摆动到位且无重叠时恢复碰撞");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ToggleMidSwingReversesCleanlyToClosedAngle()
        {
            var root = CreateDoorObject("Door_Test", Vector3.zero, new Vector3(0f, 1f, -0.45f), new Vector3(0.1f, 2f, 0.9f), out var door);
            try
            {
                door.SwingDuration = 0.4f;
                door.Toggle();
                door.Tick(0.2f);
                Assert.IsTrue(door.IsOpen);

                door.Toggle();
                Assert.IsFalse(door.IsOpen);
                Assert.IsTrue(door.IsMoving);
                Assert.IsFalse(door.AreCollidersEnabled);

                door.Tick(0.3f);
                Assert.IsFalse(door.IsMoving);
                Assert.AreEqual(0f, door.CurrentAngle, 1e-3f);
                Assert.IsTrue(door.AreCollidersEnabled);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CollidersStayDisabledWhilePlayerBoundsOverlapLeafAndRestoreAfterExit()
        {
            var root = CreateDoorObject("Door_Test", Vector3.zero, new Vector3(0f, 1f, -0.45f), new Vector3(0.1f, 2f, 0.9f), out var door);
            try
            {
                door.SwingDuration = 0.2f;
                door.Toggle();
                door.Tick(0.3f);
                Assert.IsFalse(door.IsMoving);
                Assert.IsTrue(door.AreCollidersEnabled);

                // Close back to 0 degrees while a player capsule overlaps the closed doorway (0, 0.85, -0.45).
                var overlappingPlayer = new Bounds(new Vector3(0f, 0.85f, -0.45f), new Vector3(0.64f, 1.70f, 0.64f));
                door.Toggle();
                door.Tick(0.3f, overlappingPlayer);

                Assert.IsFalse(door.IsOpen);
                Assert.IsFalse(door.IsMoving);
                Assert.IsTrue(door.WaitingClearance, "玩家包围盒仍在门叶内时应延迟恢复碰撞");
                Assert.IsFalse(door.AreCollidersEnabled);

                door.Tick(0.05f, overlappingPlayer);
                Assert.IsTrue(door.WaitingClearance);
                Assert.IsFalse(door.AreCollidersEnabled);

                var clearPlayer = new Bounds(new Vector3(1.5f, 0.85f, -0.45f), new Vector3(0.64f, 1.70f, 0.64f));
                door.Tick(0.02f, clearPlayer);
                Assert.IsFalse(door.WaitingClearance);
                Assert.IsTrue(door.AreCollidersEnabled, "玩家离开门叶包围盒后必须立刻恢复碰撞");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void InteriorRegistryCoversOnlyTheThreeInteriorDoorsAndExcludesExteriorDoor()
        {
            Assert.AreEqual(3, InteractiveDoor.InteriorSpecs.Length);
            Assert.IsTrue(InteractiveDoor.IsInteriorDoorMesh("mesh_341"));
            Assert.IsTrue(InteractiveDoor.IsInteriorDoorMesh("mesh_340"));
            Assert.IsTrue(InteractiveDoor.IsInteriorDoorMesh("mesh_238"));
            Assert.IsTrue(InteractiveDoor.IsInteriorDoorMesh("mesh_237"));
            Assert.IsTrue(InteractiveDoor.IsInteriorDoorMesh("mesh_438"));
            Assert.IsTrue(InteractiveDoor.IsInteriorDoorMesh("mesh_297"));

            Assert.IsFalse(InteractiveDoor.IsInteriorDoorMesh("mesh_269.001"), "外墙防盗门不得纳入室内交互门名单");
            Assert.IsFalse(InteractiveDoor.IsInteriorDoorMesh("mesh_271"), "外墙防盗门把手不得纳入室内交互门名单");
            Assert.IsFalse(InteractiveDoor.IsInteriorDoorMesh("mesh_538"), "门框不得纳入摆动门名单");

            var room = new GameObject("FlatRoot");
            var activeDoorObj = CreateDoorObject("Door_BathBedroom", Vector3.zero, Vector3.zero, Vector3.one, out var activeDoor);
            activeDoorObj.transform.SetParent(room.transform, false);

            var inactiveDoorObj = CreateDoorObject("Door_Inactive", Vector3.zero, Vector3.zero, Vector3.one, out _);
            inactiveDoorObj.transform.SetParent(room.transform, false);
            inactiveDoorObj.SetActive(false);

            var exteriorDoor = new GameObject("mesh_269.001");
            exteriorDoor.transform.SetParent(room.transform, false);
            exteriorDoor.AddComponent<BoxCollider>();

            try
            {
                var found = InteractiveDoor.FindActive(room);
                Assert.AreEqual(1, found.Length);
                Assert.AreSame(activeDoor, found[0]);
            }
            finally
            {
                Object.DestroyImmediate(room);
            }
        }

        static GameObject CreateInteractorHarness(out DoorInteractor interactor, out InteractiveDoor doorA, out InteractiveDoor doorB)
        {
            var root = new GameObject("DoorInteractorHarness");
            var doorAObj = CreateDoorObject("Door_A", new Vector3(0f, 0f, 2.0f), new Vector3(0f, 1f, 0f), new Vector3(0.9f, 2f, 0.1f), out doorA);
            doorAObj.transform.SetParent(root.transform, false);

            var doorBObj = CreateDoorObject("Door_B", new Vector3(0f, 0f, 4.0f), new Vector3(0f, 1f, 0f), new Vector3(0.9f, 2f, 0.1f), out doorB);
            doorBObj.transform.SetParent(root.transform, false);

            var rigObj = new GameObject("Interactor");
            rigObj.transform.SetParent(root.transform, false);
            interactor = rigObj.AddComponent<DoorInteractor>();
            interactor.enabled = false;
            interactor.SetDoors(new[] { doorA, doorB });
            return root;
        }

        [Test]
        public void InteractKeyIsFAndDoesNotConflictWithInputModesOrActionHotkeys()
        {
            Assert.AreEqual(KeyCode.F, DoorInteractor.InteractKey);

            var reserved = new[]
            {
                KeyCode.E, KeyCode.Escape,
                KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
                KeyCode.C, KeyCode.Z, KeyCode.X,
            };
            foreach (var key in reserved)
            {
                Assert.AreNotEqual(key, DoorInteractor.InteractKey, "门交互热键不得与既有输入/动作快捷键冲突: " + key);
            }
        }

        [Test]
        public void InteractorSelectsDoorWithinReachAndLookDirectionAndClearsWhenAway()
        {
            var root = CreateInteractorHarness(out var interactor, out var doorA, out var doorB);
            try
            {
                Assert.AreEqual(2.6f, DoorInteractor.ReachDistance, 1e-4f);

                // Standing at origin looking +Z: doorA is at z=2.0 (in reach), doorB at z=4.0 (out of reach).
                interactor.RefreshTarget(new Vector3(0f, 1f, 0f), Vector3.forward);
                Assert.AreSame(doorA, interactor.CurrentTarget);
                Assert.AreEqual("按 F 开门", interactor.CurrentPrompt);

                // Looking away (-Z): neither door qualifies.
                interactor.RefreshTarget(new Vector3(0f, 1f, 0f), Vector3.back);
                Assert.IsNull(interactor.CurrentTarget);
                Assert.AreEqual(string.Empty, interactor.CurrentPrompt);

                // Move out of reach (> 2.6 m) while looking at doorA.
                interactor.RefreshTarget(new Vector3(0f, 1f, -1.0f), Vector3.forward);
                Assert.IsNull(interactor.CurrentTarget);
                Assert.AreEqual(string.Empty, interactor.CurrentPrompt);

                // Bring doorB inside 2.6m at an off-axis angle while doorA is directly ahead: aligned doorA wins.
                doorB.transform.position = new Vector3(1.1f, 0f, 0.9f);
                interactor.RefreshTarget(new Vector3(0f, 1f, 0f), Vector3.forward);
                Assert.AreSame(doorA, interactor.CurrentTarget);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PressInteractTogglesDoorAndPromptBetweenOpenAndClose()
        {
            var root = CreateInteractorHarness(out var interactor, out var doorA, out _);
            try
            {
                var origin = new Vector3(0f, 1f, 0f);
                interactor.RefreshTarget(origin, Vector3.forward);
                Assert.AreEqual("按 F 开门", interactor.CurrentPrompt);

                Assert.IsTrue(interactor.PressInteract(origin, Vector3.forward));
                Assert.IsTrue(doorA.IsOpen);

                interactor.RefreshTarget(origin, Vector3.forward);
                Assert.AreEqual("按 F 关门", interactor.CurrentPrompt);

                Assert.IsTrue(interactor.PressInteract(origin, Vector3.forward));
                Assert.IsFalse(doorA.IsOpen);

                interactor.RefreshTarget(origin, Vector3.forward);
                Assert.AreEqual("按 F 开门", interactor.CurrentPrompt);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PressInteractIsBlockedWhenTypingBusyOrKeyboardDisabled()
        {
            var root = CreateInteractorHarness(out var interactor, out var doorA, out _);
            try
            {
                var origin = new Vector3(0f, 1f, 0f);

                interactor.IsTypingOverride = () => true;
                interactor.RefreshTarget(origin, Vector3.forward);
                Assert.AreEqual(string.Empty, interactor.CurrentPrompt);
                Assert.IsFalse(interactor.PressInteract(origin, Vector3.forward));
                Assert.IsFalse(doorA.IsOpen);

                interactor.IsTypingOverride = null;
                interactor.IsBusyOverride = () => true;
                interactor.RefreshTarget(origin, Vector3.forward);
                Assert.AreEqual(string.Empty, interactor.CurrentPrompt);
                Assert.IsFalse(interactor.PressInteract(origin, Vector3.forward));
                Assert.IsFalse(doorA.IsOpen);

                interactor.IsBusyOverride = null;
                interactor.KeyboardInput = false;
                interactor.RefreshTarget(origin, Vector3.forward);
                Assert.AreEqual(string.Empty, interactor.CurrentPrompt);
                Assert.IsFalse(interactor.PressInteract(origin, Vector3.forward));
                Assert.IsFalse(doorA.IsOpen);

                interactor.KeyboardInput = true;
                interactor.RefreshTarget(origin, Vector3.forward);
                Assert.AreEqual("按 F 开门", interactor.CurrentPrompt);
                Assert.IsTrue(interactor.PressInteract(origin, Vector3.forward));
                Assert.IsTrue(doorA.IsOpen);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
