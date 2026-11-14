using System;
using System.Collections;
using System.Collections.Generic;
using Mas3D.Actions;
using Mas3D.Room;
using Mas3D.UI;
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Mas3D.Tests
{
    /// <summary>
    /// End-to-end PlayMode verification for Task 2.2 (RoomBootstrap door wiring & default-bedroom
    /// isolation) and Task 4.1 (real Flat.prefab doorway collision, F / non-UI click toggling,
    /// #door-prompt HUD updates, full continuous multi-room walk-through, and input/busy gates).
    /// </summary>
    public class FlatDoorTests
    {
        const string FlatPrefabPath = "Assets/_Local/room/Flat.prefab";
        const float Step = 1f / 60f;

        GameObject _flat;
        GameObject _playerGo;
        GameObject _cameraGo;
        GameObject _interactorGo;
        DialoguePanelTests.DialoguePanelHarness _panelHarness;
        PlayerMotor _motor;
        PlayerCameraRig _rig;
        Camera _camera;
        DoorInteractor _interactor;
        bool _prevBackfaces;

        [SetUp]
        public void SetUp()
        {
            _prevBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.queriesHitBackfaces = _prevBackfaces;
            if (_interactorGo != null) UnityEngine.Object.DestroyImmediate(_interactorGo);
            if (_playerGo != null) UnityEngine.Object.DestroyImmediate(_playerGo);
            if (_cameraGo != null) UnityEngine.Object.DestroyImmediate(_cameraGo);
            if (_flat != null) UnityEngine.Object.DestroyImmediate(_flat);
            if (_panelHarness != null) _panelHarness.Dispose();
            _interactorGo = null;
            _playerGo = null;
            _cameraGo = null;
            _flat = null;
            _panelHarness = null;
            _motor = null;
            _rig = null;
            _camera = null;
            _interactor = null;
            SceneFlow.UnloadMainIfLoaded();
        }

        void BuildFlatHarness(bool withUiAndCamera = true)
        {
#if UNITY_EDITOR
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FlatPrefabPath);
            Assert.IsNotNull(prefab, "缺少平层预制体: " + FlatPrefabPath);
            _flat = UnityEngine.Object.Instantiate(prefab);
#else
            Assert.Ignore("FlatDoorTests 需要在 Editor 环境下加载 Flat.prefab");
#endif
            _flat.name = "Flat-UnderTest";
            _flat.transform.position = Vector3.zero;
            _flat.SetActive(true);
            Physics.SyncTransforms();

            _playerGo = new GameObject("FlatDoorTests/Player");
            _playerGo.tag = "Player";
            _playerGo.AddComponent<CharacterController>();
            _motor = _playerGo.AddComponent<PlayerMotor>();
            _motor.enabled = false;
            _motor.KeyboardInput = true;
            Assert.AreEqual(1.70f, _motor.CapsuleHeight, 1e-4f, "站立身高规格应为 1.70 m");
            Assert.AreEqual(0.30f, _motor.CapsuleRadius, 1e-4f, "胶囊半径规格应为 0.30 m");

            _rig = _playerGo.AddComponent<PlayerCameraRig>();
            _rig.enabled = false;

            if (withUiAndCamera)
            {
                _cameraGo = new GameObject("FlatDoorTests/Camera");
                _camera = _cameraGo.AddComponent<Camera>();
                _camera.nearClipPlane = 0.05f;
                _camera.farClipPlane = 120f;
                _camera.fieldOfView = 60f;
                _camera.pixelRect = new Rect(0f, 0f, 1280f, 720f);

                _panelHarness = DialoguePanelTests.DialoguePanelHarness.Create();
                _interactorGo = new GameObject("FlatDoorTests/Interactor");
                _interactor = _interactorGo.AddComponent<DoorInteractor>();
                _interactor.enabled = false;
                _interactor.Bind(_motor, _rig, null, _panelHarness.Panel, InteractiveDoor.FindActive(_flat));
                _interactor.CameraOverride = _camera;
            }
        }

        InteractiveDoor FindDoor(string doorId)
        {
            var doors = InteractiveDoor.FindActive(_flat);
            for (var i = 0; i < doors.Length; i++)
            {
                if (string.Equals(doors[i].DoorId, doorId, StringComparison.Ordinal)) return doors[i];
            }
            Assert.Fail("在 Flat.prefab 中找不到门: " + doorId);
            return null;
        }

        void PlacePlayer(Vector3 feet, float yaw)
        {
            _motor.Teleport(feet, yaw);
            Physics.SyncTransforms();
            for (var i = 0; i < 6; i++)
            {
                _motor.Tick(Step, Vector3.zero);
            }
            Physics.SyncTransforms();
        }

        Vector3 WalkFlat(Vector3 direction, float seconds)
        {
            var steps = Mathf.CeilToInt(seconds / Step);
            var dir = new Vector2(direction.x, direction.z);
            if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
            for (var i = 0; i < steps; i++)
            {
                _motor.Tick(Step, dir);
            }
            Physics.SyncTransforms();
            return _playerGo.transform.position;
        }

        void WalkToward(Vector3 targetXZ, float maxSeconds = 4f, float stopRadius = 0.18f)
        {
            var steps = Mathf.CeilToInt(maxSeconds / Step);
            for (var i = 0; i < steps; i++)
            {
                var cur = _playerGo.transform.position;
                var delta = new Vector2(targetXZ.x - cur.x, targetXZ.z - cur.z);
                if (delta.magnitude <= stopRadius) break;
                _motor.Tick(Step, delta.normalized);
            }
            Physics.SyncTransforms();
        }

        IEnumerator SettleDoor(InteractiveDoor door)
        {
            for (var i = 0; i < 60 && door.IsMoving; i++)
            {
                door.Tick(0.02f, GetPlayerCapsuleBounds());
                yield return null;
            }
            Assert.IsFalse(door.IsMoving, door.DoorId + " 应在预期时间内摆动到位");
            Physics.SyncTransforms();
        }

        Bounds GetPlayerCapsuleBounds()
        {
            var p = _playerGo.transform.position;
            return new Bounds(p + Vector3.up * (_motor.CapsuleHeight * 0.5f),
                new Vector3(_motor.CapsuleRadius * 2f, _motor.CapsuleHeight, _motor.CapsuleRadius * 2f));
        }

        void AimCameraAtDoor(InteractiveDoor door, Vector3 cameraOffset)
        {
            var target = door.InteractionCenter;
            var eye = _playerGo.transform.position + Vector3.up * _motor.EyeHeight + cameraOffset;
            _camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation((target - eye).normalized, Vector3.up));
            Physics.SyncTransforms();
        }

        static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.name, name, StringComparison.Ordinal)) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        [UnityTest]
        public IEnumerator BootWithDefaultBedroomCreatesNoDoorInteractorAndHidesPrompt()
        {
            SceneFlow.Require();
            yield return SceneFlow.LoadMain();

            var boot = UnityEngine.Object.FindAnyObjectByType<RoomBootstrap>();
            yield return SceneFlow.WaitForBoot(boot);

            Assert.IsNotNull(boot.FlatRoom, "CliPlayer 应保留非激活 FlatRoom 接线");
            Assert.IsFalse(boot.FlatRoom.activeInHierarchy, "默认卧室启动不得激活平层");
            Assert.IsNull(boot.Doors, "默认无门卧室启动不得新建 DoorInteractor");

            var panel = UnityEngine.Object.FindAnyObjectByType<DialoguePanel>();
            Assert.IsNotNull(panel);
            Assert.AreEqual(string.Empty, panel.DoorPromptText);
            var prompt = panel.Document.rootVisualElement.Q<Label>("door-prompt");
            Assert.IsNotNull(prompt);
            Assert.AreEqual(DisplayStyle.None, prompt.style.display.value);

            yield return SceneFlow.UnloadMain();
        }

        [UnityTest]
        public IEnumerator FlatPrefabHasThreeInteriorDoorsAndPersistentDoubleSidedColliders()
        {
            BuildFlatHarness(withUiAndCamera: false);
            yield return null;

            var doors = InteractiveDoor.FindActive(_flat);
            Assert.AreEqual(3, doors.Length, "Flat.prefab 应恰有 3 扇显式室内交互门");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in doors)
            {
                ids.Add(d.DoorId);
                var b = d.GetWorldBounds();
                Assert.Greater(b.size.y, 1.85f, d.DoorId + " 门洞净高应达到 ~1.94 m 以容纳 1.70 m 站立玩家");
                Assert.Greater(b.size.z, 0.85f, d.DoorId + " 门扇净宽应达到 ~0.91 m 以容纳 Ø0.64 m 胶囊");
            }
            Assert.IsTrue(ids.Contains("Door_BathBedroom"));
            Assert.IsTrue(ids.Contains("Door_BedroomLiving"));
            Assert.IsTrue(ids.Contains("Door_LivingKitchen"));

            var frontDoor = FindChildRecursive(_flat.transform, "mesh_269.001");
            Assert.IsNotNull(frontDoor, "外墙大门 mesh_269.001 应保留在场景中");
            Assert.IsNull(frontDoor.GetComponentInParent<InteractiveDoor>(), "外墙大门不得挂上 InteractiveDoor");

            var meshColliders = _flat.GetComponentsInChildren<MeshCollider>(true);
            Assert.Greater(meshColliders.Length, 400);
            foreach (var mc in meshColliders)
            {
                Assert.IsNotNull(mc.sharedMesh, "MeshCollider 丢失网格引用（包含 (inner) 子资产未持久化）: " + mc.name);
            }
        }

        [UnityTest]
        public IEnumerator ClosedDoorsBlockStandingPlayerFromBothMinusXAndPlusX()
        {
            BuildFlatHarness(withUiAndCamera: false);
            yield return null;

            var doors = InteractiveDoor.FindActive(_flat);
            Assert.AreEqual(3, doors.Length);

            foreach (var door in doors)
            {
                var c = door.InteractionCenter;
                Assert.IsFalse(door.IsOpen);
                Assert.IsTrue(door.AreCollidersEnabled);

                // Approach from -X side walking +X: must stay strictly on -X side of the door plane.
                PlacePlayer(new Vector3(c.x - 0.85f, 0.05f, c.z), 90f);
                var afterPlusX = WalkFlat(Vector3.right, 1.2f);
                Assert.Less(afterPlusX.x, c.x - 0.15f,
                    $"{door.DoorId} 闭合时从 -X 撞击必须被挡住，实际到达 x={afterPlusX.x:F3}, doorX={c.x:F3}");

                // Approach from +X side walking -X: must stay strictly on +X side of the door plane.
                PlacePlayer(new Vector3(c.x + 0.85f, 0.05f, c.z), -90f);
                var afterMinusX = WalkFlat(Vector3.left, 1.2f);
                Assert.Greater(afterMinusX.x, c.x + 0.15f,
                    $"{door.DoorId} 闭合时从 +X 撞击必须被挡住，实际到达 x={afterMinusX.x:F3}, doorX={c.x:F3}");
            }
        }

        [UnityTest]
        public IEnumerator ApproachPressFAndNonUiClickToggleDoorAndUpdatePrompt()
        {
            BuildFlatHarness(withUiAndCamera: true);
            yield return null;

            var door = FindDoor("Door_BedroomLiving");
            var c = door.InteractionCenter;
            PlacePlayer(new Vector3(c.x - 1.10f, 0.05f, c.z), 90f);
            AimCameraAtDoor(door, Vector3.zero);

            var promptLabel = _panelHarness.Document.rootVisualElement.Q<Label>("door-prompt");
            Assert.IsNotNull(promptLabel);

            _interactor.RefreshTargetFromView();
            Assert.AreSame(door, _interactor.CurrentTarget);
            Assert.AreEqual(DoorInteractor.PromptOpen, _panelHarness.Panel.DoorPromptText);
            Assert.AreEqual(DisplayStyle.Flex, promptLabel.style.display.value);

            // Toggle open with F key seam.
            Assert.IsTrue(_interactor.PressInteract(), "靠近且面向闭合门按 F 必须开门");
            yield return SettleDoor(door);
            Assert.IsTrue(door.IsOpen);
            Assert.AreEqual(InteractiveDoor.OpenAngle, door.CurrentAngle, 0.5f);
            Assert.IsTrue(door.AreCollidersEnabled);

            // Re-aim at doorway/open leaf and verify prompt changes to close hint.
            AimCameraAtDoor(door, Vector3.zero);
            _interactor.RefreshTargetFromView();
            Assert.AreEqual(DoorInteractor.PromptClose, _panelHarness.Panel.DoorPromptText);

            // Toggle closed via simulated left click on non-UI screen projection of the open door leaf.
            var screenPoint3D = _camera.WorldToScreenPoint(door.InteractionCenter);
            var screenPoint = new Vector2(screenPoint3D.x, screenPoint3D.y);
            Assert.Greater(screenPoint3D.z, 0f);
            Assert.IsFalse(_panelHarness.Panel.ContainsScreenPoint(screenPoint), "门投影点击点不应落在对话面板内");

            Assert.IsTrue(_interactor.TryClickAtScreenPoint(screenPoint), "左键点击门体非 UI 区域必须关门");
            yield return SettleDoor(door);
            Assert.IsFalse(door.IsOpen);
            Assert.AreEqual(InteractiveDoor.ClosedAngle, door.CurrentAngle, 0.5f);

            _interactor.RefreshTargetFromView();
            Assert.AreEqual(DoorInteractor.PromptOpen, _panelHarness.Panel.DoorPromptText);

            // Walk out of 2.6 m reach: prompt must clear cleanly.
            PlacePlayer(new Vector3(c.x - 3.60f, 0.05f, c.z), 90f);
            _interactor.RefreshTargetFromView();
            Assert.IsNull(_interactor.CurrentTarget);
            Assert.AreEqual(string.Empty, _panelHarness.Panel.DoorPromptText);
            Assert.AreEqual(DisplayStyle.None, promptLabel.style.display.value);
        }

        [UnityTest]
        public IEnumerator OpeningAllThreeDoorsAllowsContinuousWalkFromBathThroughKitchen()
        {
            BuildFlatHarness(withUiAndCamera: true);
            yield return null;

            var door1 = FindDoor("Door_BathBedroom");
            var door2 = FindDoor("Door_BedroomLiving");
            var door3 = FindDoor("Door_LivingKitchen");

            var c1 = door1.InteractionCenter;
            var c2 = door2.InteractionCenter;
            var c3 = door3.InteractionCenter;

            // Start inside Bathroom on -X side of Door 1.
            PlacePlayer(new Vector3(c1.x - 0.90f, 0.05f, c1.z), 90f);
            AimCameraAtDoor(door1, Vector3.zero);

            // Verify closed Door 1 blocks before interaction.
            WalkToward(new Vector3(c1.x + 0.5f, 0f, c1.z), 1.0f);
            Assert.Less(_playerGo.transform.position.x, c1.x - 0.15f, "门 1 开启前不得穿过");

            // Open Door 1 via F seam and pass through into Bedroom.
            AimCameraAtDoor(door1, Vector3.zero);
            Assert.IsTrue(_interactor.PressInteract());
            yield return SettleDoor(door1);

            WalkToward(new Vector3(c1.x + 1.0f, 0f, c1.z), 2.0f);
            Assert.Greater(_playerGo.transform.position.x, c1.x + 0.65f,
                "门 1 开启后 1.70 m 站立玩家应顺利走进卧室, x=" + _playerGo.transform.position.x);

            // Walk across Bedroom to -X approach of Door 2.
            WalkToward(new Vector3(c2.x - 0.90f, 0f, c2.z), 4.0f);
            Assert.Greater(_playerGo.transform.position.x, c2.x - 1.30f, "卧室内部走道应直达门 2 前方");

            // Confirm closed Door 2 blocks before opening.
            WalkToward(new Vector3(c2.x + 0.5f, 0f, c2.z), 0.8f);
            Assert.Less(_playerGo.transform.position.x, c2.x - 0.15f, "门 2 开启前不得穿过");

            // Open Door 2 and enter Living Room.
            AimCameraAtDoor(door2, Vector3.zero);
            Assert.IsTrue(_interactor.PressInteract());
            yield return SettleDoor(door2);

            WalkToward(new Vector3(c2.x + 0.85f, 0f, c2.z), 2.0f);
            Assert.Greater(_playerGo.transform.position.x, c2.x + 0.55f,
                "门 2 开启后应顺利进入客厅, x=" + _playerGo.transform.position.x);

            // Navigate around the Living Room center sofa/table via the -Z walkway to Door 3's -X approach.
            WalkToward(new Vector3(-0.35f, 0f, c2.z), 1.5f);
            WalkToward(new Vector3(-0.35f, 0f, -1.60f), 2.5f);
            WalkToward(new Vector3(5.00f, 0f, -1.60f), 5.0f);
            WalkToward(new Vector3(5.10f, 0f, -0.95f), 2.0f);
            WalkToward(new Vector3(c3.x - 0.90f, 0f, c3.z), 2.0f);
            Assert.Greater(_playerGo.transform.position.x, c3.x - 1.40f,
                "应沿客厅走道连续抵达门 3 前方, 当前 x=" + _playerGo.transform.position.x + " z=" + _playerGo.transform.position.z);

            // Confirm closed Door 3 blocks before opening.
            WalkToward(new Vector3(c3.x + 0.5f, 0f, c3.z), 0.8f);
            Assert.Less(_playerGo.transform.position.x, c3.x - 0.15f, "门 3 开启前不得穿过");

            // Open Door 3 and enter Dining/Open Kitchen.
            AimCameraAtDoor(door3, Vector3.zero);
            Assert.IsTrue(_interactor.PressInteract());
            yield return SettleDoor(door3);

            WalkToward(new Vector3(c3.x + 1.0f, 0f, c3.z), 2.0f);
            Assert.Greater(_playerGo.transform.position.x, c3.x + 0.65f,
                "门 3 开启后应顺利走进餐厅/开放厨房, x=" + _playerGo.transform.position.x);
        }

        [UnityTest]
        public IEnumerator TypingFocusBusyAndInsidePanelClickBlockDoorInteraction()
        {
            BuildFlatHarness(withUiAndCamera: true);
            yield return null;

            var door = FindDoor("Door_BedroomLiving");
            var c = door.InteractionCenter;
            PlacePlayer(new Vector3(c.x - 1.10f, 0.05f, c.z), 90f);
            AimCameraAtDoor(door, Vector3.zero);

            var doorScreen3 = _camera.WorldToScreenPoint(c);
            var doorScreen = new Vector2(doorScreen3.x, doorScreen3.y);

            // 1. Focused dialogue input blocks both F key and click.
            _panelHarness.Panel.FocusInput();
            yield return null;
            Assert.IsTrue(_panelHarness.Panel.IsInputFocused);
            _interactor.RefreshTargetFromView();
            Assert.AreEqual(string.Empty, _panelHarness.Panel.DoorPromptText);
            Assert.IsFalse(_interactor.PressInteract(), "聚焦聊天输入框时按 F 不得开关门");
            Assert.IsFalse(_interactor.TryClickAtScreenPoint(doorScreen), "聚焦聊天输入框时点击不得开关门");
            Assert.IsFalse(door.IsOpen);

            _panelHarness.Panel.BlurInput();
            yield return null;
            Assert.IsFalse(_panelHarness.Panel.IsInputFocused);

            // 2. Inside-panel click is blocked even when player is standing right in front of the door.
            var insidePanel = FindPointInsidePanel(_panelHarness.Panel);
            Assert.IsFalse(_interactor.TryClickAtScreenPoint(insidePanel), "点击聊天面板内部不得穿透触发开门");
            Assert.IsFalse(door.IsOpen);

            // 3. Performing / busy state (ActionDirector.Current / Motor.KeyboardInput = false) blocks interaction.
            _motor.KeyboardInput = false;
            _interactor.IsBusyOverride = () => true;
            _interactor.RefreshTargetFromView();
            Assert.AreEqual(string.Empty, _panelHarness.Panel.DoorPromptText);
            Assert.IsFalse(_interactor.PressInteract(), "双人演出期间按 F 不得开关门");
            Assert.IsFalse(_interactor.TryClickAtScreenPoint(doorScreen), "双人演出期间点击不得开关门");
            Assert.IsFalse(door.IsOpen);

            // Restore normal control -> door interacts again.
            _interactor.IsBusyOverride = null;
            _motor.KeyboardInput = true;
            Assert.IsTrue(_interactor.PressInteract(), "解除拦截后应可正常按 F 开门");
            Assert.IsTrue(door.IsOpen);
        }

        static Vector2 FindPointInsidePanel(DialoguePanel panel)
        {
            var width = Screen.width > 0 ? Screen.width : 1280;
            var height = Screen.height > 0 ? Screen.height : 720;
            for (var y = 0; y < height; y += 16)
            {
                for (var x = 0; x < width; x += 16)
                {
                    var pt = new Vector2(x, y);
                    if (panel.ContainsScreenPoint(pt)) return pt;
                }
            }
            Assert.Fail("未能在屏幕内找到落在 DialoguePanel 中的测试点");
            return Vector2.zero;
        }
    }
}
