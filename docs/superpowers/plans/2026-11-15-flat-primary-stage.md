# Flat Primary Stage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Elevate the baked 5-zone `Flat.prefab` apartment into Mas3D's default playable stage with living-sofa face-to-face spawn, configured non-bathroom room anchors/seats, cross-room `EdgeTable` pathing with single-arm `TwoBoneIk` door-handle pushing, adapted 1–5 actions, and a restrained idle room-rotation routine while preserving `-bedroom 1` compatibility for legacy single-bedroom tests.

**Architecture:** Extend `AnchorTable` with optional active-table selection and graph fields (`waypoints`, `links`, `defaultSpawn`, `pose`, `standGap`, `intimate`) so legacy `anchors.json` stays untouched while `CliFlatAnchors.Solve` generates capsule-verified `anchors-flat.json`. `RoomBootstrap.Boot()` defaults to `FlatRoom` unless `-bedroom 1` / `ShowBedroom()` is requested, routes `EdgeTable` / `AvatarWalker` across doorways with `InteractiveDoor.SetOpen(true)` + `TwoBoneIk.Solve` tracking `HandleMesh`, keeps `ActionDirector` safely gated on the living sofa, and runs `AvatarRoutine.Tick(dt)` when idle.

**Tech Stack:** Unity 6000.6.7f1, URP, UniVRM `0.131.4`, C# runtime/editor scripts under `apps/unity/Assets`, NUnit EditMode/PlayMode tests via Unity CLI (`%LOCALAPPDATA%\Unity\bin\unity.exe`), `tools/unity.ps1`, `tools/check.ps1`.

**Spec:** `docs/superpowers/specs/2026-11-15-flat-primary-stage-design.md` (includes the 4-step roadmap and the `[Blocked]` semantic facial expression `LumenAct` track at <https://github.com/AaBb-coder/LumenAct>, blocked until 118 Blender shape keys are added manually).

## Global Constraints

- **OpenSpec before implementation (`AGENTS.md`):** Before modifying any product code under `apps/unity/Assets`, synchronize this approved plan into an OpenSpec change (e.g. `openspec/changes/flat-primary-stage/`) and run `openspec validate --strict`.
- **No self-commit (`AGENTS.md`):** Do **not** run `git commit` or `git push`; leave workspace changes for user review and commit approval.
- **Zero new Unity packages:** Do not modify `apps/unity/Packages/manifest.json` (`com.unity.ai.navigation` stays out).
- **Single Unity instance:** Close any interactive Unity Editor locking `apps/unity` before invoking `tools/unity.ps1` or Unity CLI commands.
- **Asset & ignore boundaries:** Keep `mita-room.glb` and `mita-flat.glb` read-only; keep `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` (`Assert.Ignore`) untouched.
- **Geometry truth over guessed coordinates:** Every anchor, seat, spawn, waypoint, and link in `anchors-flat.json` must be emitted and `0.30 m`-capsule-swept against baked `Flat.prefab` colliders (zero bathroom nodes/links; living 3-seat sofa `mesh_211.002` / `mesh_218.002` clear of coffee table).
- **Deferred work stays out:** No per-room lighting/shell shadow/window scenery rewrite (Step 2), no `Kimodo` `.bvh` / full-body door stepping / `MotionPlan` consumer (Step 3), no coffee/book props (Step 4), and no `LumenAct` 118-shape-key integration.

## Review Focus

1. **`AvatarActor.Start()` vs `RoomBootstrap.Boot()` startup race (Task 4):** Loading `Main.unity` must never let `AvatarActor.Start()` sit her on old bedroom `anchors.json` before `Boot()` activates flat mode; verify seated Y/XZ matches living sofa `S1` on default launch and matches bedroom bench `S1` under `ShowBedroom()`.
2. **Cross-test active-table state leak (Task 1 & Task 4):** Unloading `Main.unity` after a flat test (`ActionSmokeTests`) must reset `AnchorTable` and `EdgeTable` cache so standalone legacy bedroom tests (`EdgeTableTests`, `AvatarWalkTests`) still load `mas3d/anchors.json` cleanly.
3. **Already-open door & player blocking doorway mid-swing (Task 3):** Passing through `Door_BedroomLiving` when already open (`IsOpen == true`) must skip handle IK and walk straight through; when closed and the player's `CharacterController` waits in the doorway exit (`ProbeRadius = 0.30f`), she must finish swinging the door and stop cleanly without pushing into the player.
4. **Dialogue `Thinking` / action preemption mid-routine-walk (Task 5):** Starting a dialogue turn or pressing `1–5` while `AvatarRoutine` has initiated a cross-room walk must obey `"mouth wins over legs"` (`TurnPhase.Thinking` halts walk cleanly, disables further routine moves until idle, and never forces mid-walk action entry).
5. **Peripheral seat / non-living-sofa `1–5` action refusal (Task 4):** Invoking `sit-together`, `kiss`, or `pat-me` while she is seated on bedroom reading chair or kitchen bar stool (or standing mid-corridor) must return `false`, touch zero player position/posture or `ClearPoseCount`, and surface a clear refusal message.

## File Structure

- `apps/unity/Assets/Scripts/AnchorTable.cs`: Active table state (`SetActiveRelativePath`, `ResetActive`, `FlatRelativePath`) + optional schema types (`WaypointEntry`, `LinkEntry`, `SpawnEntry`, `SeatEntry.pose`/`standGap`/`intimate`/`room`).
- `apps/unity/Assets/Scripts/Boot/BootArgs.cs`: Parse optional `-bedroom [0|1]` switch alongside `-nobody`.
- `apps/unity/Assets/Editor/CliFlatAnchors.cs` (New): Offline `Flat.prefab` (`1.5x`) geometry & collider solver writing `Anchors/anchors-flat.json` and `StreamingAssets/mas3d/anchors-flat.json` + running `0.30 m` capsule clearance sweep.
- `apps/unity/Assets/Scripts/Room/EdgeTable.cs`: Support dynamic build from `AnchorTable.links`/`waypoints` (`DoorId` on `WalkEdge`, `Room` on `WalkNode`) + `Rebuild()`, falling back to hardcoded bedroom topology when `links` is null/empty.
- `apps/unity/Assets/Scripts/Room/InteractiveDoor.cs`: Add `HandleWorldPosition()` returning live handle collider/renderer world center.
- `apps/unity/Assets/Scripts/Avatar/AvatarWalker.cs`: Add doorway opening sub-state (`OpeningDoor`) when stepping onto an edge with `DoorId`: pause, face doorway, `door.SetOpen(true)`, drive single-arm `TwoBoneIk.Solve` to `HandleWorldPosition()`, resume once `door.IsOpen && !door.IsMoving`.
- `apps/unity/Assets/Scripts/Avatar/AvatarRoutine.cs` (New): Restrained idle autonomous scheduler (`Tick(float dt)`, `Enabled`, `DriveInUpdate`, non-bathroom pool, dialogue/action/player-focus gating).
- `apps/unity/Assets/Scripts/RoomBootstrap.cs`: Default to Flat mode when `FlatRoom != null` unless `-bedroom 1` or `ShowBedroom()` is set; set active `AnchorTable`, rebuild `EdgeTable`, spawn player at `defaultSpawn`, seat `Actor` at flat `S1`, bind `AvatarRoutine`, clean up in `OnDestroy()`.
- `apps/unity/Assets/Scripts/Avatar/AvatarActor.cs`: Defer premature `Start()` seating while an unbooted `RoomBootstrap` owns startup; respect `SeatEntry.pose == "bed"` semantics.
- `apps/unity/Assets/Scripts/Actions/ActionDirector.cs` & `apps/unity/Assets/Scripts/Actions/ActionSeats.cs`: Read active `AnchorTable`; support living 3-seat sofa `S1`/`S2` pairing (`BedPair`); cleanly refuse incompatible peripheral poses.
- `apps/unity/Assets/Editor/CliPlayer.cs`: Copy `Anchors/anchors-flat.json` into `StreamingAssets/mas3d/` alongside `anchors.json` during `BuildScene()`.
- Tests (New / Modified): `BootArgsTests.cs`, `FlatAnchorTests.cs`, `FlatNavigationTests.cs`, `FlatActionTests.cs`, `RoutineTests.cs`, `SceneFlow.cs`, `FlatCeilingTests.cs`, `FlatDoorTests.cs`, `BedroomSkyCardTests.cs`, plus legacy bedroom scene callers updated to `SceneFlow.LoadBedroom()` and `ActionSmokeTests` verified in default Flat mode.

---

### Task 1: Active `AnchorTable` Schema & `-bedroom` `BootArgs` Flag

**Files:**
- Modify: `apps/unity/Assets/Scripts/AnchorTable.cs:1-91`
- Modify: `apps/unity/Assets/Scripts/Boot/BootArgs.cs:35-95`
- Test: `apps/unity/Assets/Tests/EditMode/BootArgsTests.cs`
- Create: `apps/unity/Assets/Tests/EditMode/FlatAnchorTableTests.cs`

**Interfaces:**
- Consumes: `JsonUtility.FromJson<AnchorTable>`, `BootArgs.Parse(string[])`.
- Produces:
  - `AnchorTable.FlatRelativePath = "mas3d/anchors-flat.json"`
  - `AnchorTable.ActiveRelativePath` (`string`, defaults to `DefaultRelativePath` `"mas3d/anchors.json"` until changed)
  - `AnchorTable.SetActiveRelativePath(string relativePath)` / `AnchorTable.ResetActive()`
  - `AnchorEntry.room` (`string`)
  - `SeatEntry`: `room` (`string`), `pose` (`string`), `standGap` (`float`), `intimate` (`bool`)
  - `WaypointEntry`: `id`, `room`, `x`, `z` + `Vector3 Feet(float y = 0.02f)`
  - `LinkEntry`: `from`, `to`, `doorId`
  - `SpawnEntry`: `room`, `x`, `y`, `z`, `facingY`, `seatId`
  - `AnchorTable`: `waypoints` (`WaypointEntry[]`), `links` (`LinkEntry[]`), `defaultSpawn` (`SpawnEntry`)
  - `BootArgs.Bedroom` (`bool`) parsed from `-bedroom` / `-bedroom 1` / `-bedroom 0` (case-insensitive).

- [ ] **Step 1: Write the failing EditMode tests for active table switching, extended JSON deserialization, and `-bedroom` flag**

In `BootArgsTests.cs`, add `ParsesBedroomSwitchWithAndWithoutExplicitValue` (`-bedroom`, `-bedroom 1`, `-bedroom 0`, absent). In `FlatAnchorTableTests.cs`, write two tests:
1. `SetActiveRelativePathSwitchesDefaultLoadAndResetRestoresBedroom`: sets active path to a temp JSON path / `FlatRelativePath`, verifies `AnchorTable.ActiveRelativePath`, calls `ResetActive()`, asserts `"mas3d/anchors.json"`.
2. `DeserializesOptionalGraphFieldsWhileKeepingLegacyAnchorsJsonValid`: deserializes JSON with `waypoints`, `links` (`doorId`), `defaultSpawn`, `pose`, `standGap`, `intimate`, then deserializes existing `mas3d/anchors.json` and checks backward compatibility (`links == null` and `Anchor("A2") != null`).

- [ ] **Step 2: Run EditMode tests to confirm failure**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode EditMode --output d:\Mas3D\apps\unity\TestResults\Task1.xml --report-format nunit --filter "Mas3D.Tests.BootArgsTests|Mas3D.Tests.FlatAnchorTableTests"
```
Expected: FAIL compile error (`BootArgs.Bedroom`, `AnchorTable.SetActiveRelativePath`, `WaypointEntry`, `LinkEntry` missing).

- [ ] **Step 3: Implement `AnchorTable` extensions and `BootArgs.Bedroom`**

- Update `apps/unity/Assets/Scripts/AnchorTable.cs`: add serializable classes `WaypointEntry`, `LinkEntry`, `SpawnEntry`, extend `AnchorEntry` and `SeatEntry` with optional fields (`JsonUtility`-friendly primitives), add `static string _activeRelativePath`, `ActiveRelativePath`, `FlatRelativePath = "mas3d/anchors-flat.json"`, `SetActiveRelativePath(string)`, `ResetActive()`, and resolve `Load(path = null)` from `ActiveRelativePath` when `path == null`.
- Update `apps/unity/Assets/Scripts/Boot/BootArgs.cs`: reuse the standalone switch-scanning helper used for `-nobody` so `-bedroom [0|1]` parses cleanly without consuming unrelated `-bridge` pairs; expose `public readonly bool Bedroom`.

- [ ] **Step 4: Run targeted EditMode tests + all `BootArgsTests` to verify pass**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode EditMode --output d:\Mas3D\apps\unity\TestResults\Task1.xml --report-format nunit --filter "Mas3D.Tests.BootArgsTests|Mas3D.Tests.FlatAnchorTableTests"
```
Expected: PASS (`failed="0"`).

---

### Task 2: `CliFlatAnchors.Solve` Solver & Multi-Room `EdgeTable` Graph

**Files:**
- Create: `apps/unity/Assets/Editor/CliFlatAnchors.cs`
- Modify: `apps/unity/Assets/Scripts/Room/EdgeTable.cs:1-351`
- Modify: `tools/unity.ps1:25-115` (add optional `-Flat` or `flatanchors` / keep `anchors` clean — add `'flatanchors'` action invoking `Mas3D.Editor.CliFlatAnchors.Solve`)
- Generated outputs: `apps/unity/Anchors/anchors-flat.json`, `apps/unity/Assets/StreamingAssets/mas3d/anchors-flat.json`
- Test: `apps/unity/Assets/Tests/EditMode/FlatAnchorTests.cs`
- Test: `apps/unity/Assets/Tests/EditMode/EdgeTableTests.cs` (ensure existing bedroom `EdgeTableTests` stay green + add graph rebuild/door-link checks)

**Interfaces:**
- Consumes: `AnchorTable` (`waypoints`, `links`, `defaultSpawn`, `seats`, `anchors`), `InteractiveDoor.InteriorSpecs`, `Assets/_Local/room/Flat.prefab`.
- Produces:
  - `CliFlatAnchors.Solve()` (`[MenuItem]` / batch entrypoint): instantiates `Flat.prefab`, attaches physics colliders on flat meshes, solves & snaps non-bathroom nodes and doorway portal pairs (`Door_BedroomLiving`, `Door_LivingKitchen`) using `Physics.CheckCapsule` + downward ground hit + clearance radii (`>= 0.35 m` wall / `>= 0.30 m` capsule), verifies `0` capsule-sweep collisions across all non-doorway edges (and across doorway edges with only that `DoorId`'s leaf colliders temporarily ignored), writes `Anchors/anchors-flat.json` and copies to `StreamingAssets/mas3d/anchors-flat.json`.
  - `WalkNode.Room` (`string`), `WalkEdge.DoorId` (`string`)
  - `EdgeTable.Rebuild()` (`public static void`) clearing cached graph so next lookup builds from current `AnchorTable.Load()`.
  - `EdgeTable.FindEdge(string fromId, string toId)` (`public static WalkEdge`) returning directed/matching edge carrying `DoorId`.

- [ ] **Step 1: Write failing tests for dynamic `EdgeTable` (`links`/`waypoints`/`DoorId`) and `anchors-flat.json` schema/room invariants**

In `apps/unity/Assets/Tests/EditMode/FlatAnchorTests.cs`:
1. `GeneratedFlatTableHasLivingSpawnThreeLifestyleZonesTwoDoorsAndZeroBathroomNodes`: loads `StreamingAssets/mas3d/anchors-flat.json`, checks `defaultSpawn` is in `"living"` facing `S1`, checks `S1` (living 3-seat sofa) is `intimate == true` and `S2` (living paired/secondary slot) exists with `pose != "bed"`, checks bedroom/living/kitchen zones each have >= 2 anchors/seats (`reading`, `window`, `coffee`/`bar`, `sofa`), asserts zero nodes/waypoints have `room == "bathroom"` or `doorId == "Door_BathBedroom"`, and asserts `"Door_BedroomLiving"` and `"Door_LivingKitchen"` each appear on doorway links.
2. `FlatGraphFindsShortestCrossRoomPathFromLivingToBedroomAndKitchen`: switches `AnchorTable.SetActiveRelativePath(AnchorTable.FlatRelativePath)`, calls `EdgeTable.Rebuild()`, asserts `EdgeTable.Path("S1", ...bedroomNode...)` contains a `Door_BedroomLiving` edge, asserts `EdgeTable.Path("S1", ...kitchenNode...)` contains a `Door_LivingKitchen` edge, then resets active table in `[TearDown]`.

- [ ] **Step 2: Run EditMode tests to confirm failure**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode EditMode --output d:\Mas3D\apps\unity\TestResults\Task2.xml --report-format nunit --filter "Mas3D.Tests.FlatAnchorTests|Mas3D.Tests.EdgeTableTests"
```
Expected: FAIL (`EdgeTable.Rebuild`, `WalkEdge.DoorId` missing, `anchors-flat.json` not found).

- [ ] **Step 3: Upgrade `EdgeTable` to support config-driven multi-room graphs + `Rebuild()`**

In `apps/unity/Assets/Scripts/Room/EdgeTable.cs`:
- Add `Room` property on `WalkNode` and `DoorId` property on `WalkEdge`, plus `public static WalkEdge FindEdge(string fromId, string toId)` and `public static void Rebuild()`.
- In `Build()`: load `table = AnchorTable.Load()`. If `table != null && table.links != null && table.links.Length > 0`:
  - Add `WalkNode` for every `anchor`, every `seat` (offset along `facingY` using `seat.standGap > 0f ? seat.standGap : SeatStandGap`, preserving `FeetHeight`), and every `waypoint`.
  - Add `WalkEdge(from, to, link.doorId)` for every `link` in `table.links`.
  - Skip old hardcoded `CornerW1`/`CornerW2`/`Links` completely!
- Otherwise preserve the existing legacy bedroom hardcoded build verbatim (including `EdgeTableTests` contracts).

- [ ] **Step 4: Implement `CliFlatAnchors.Solve` and run solver to generate & sweep `anchors-flat.json`**

In `apps/unity/Assets/Editor/CliFlatAnchors.cs` (and register `'flatanchors'` in `tools/unity.ps1`):
- Load `Assets/_Local/room/Flat.prefab` into a temporary empty scene (`NewSceneMode.Additive` or headless `NewScene`), attach/ensure physics colliders active (including `Physics.SyncTransforms()`), detect:
  - Living 3-seat sofa (`mesh_218.002` seat cushion top & `-Z` facing `180f`) -> seat `S1` (main sofa center, `intimate = true`, `standGap` calibrated clear of living coffee-table colliders along `-Z`), snap `defaultSpawn` facing `S1`, compute `S2` paired companion slot (`BedPairOffset = 0.70f` along sofa axis), plus living window gaze anchor and green loveseat anchor;
  - Bedroom (`x < Door_BedroomLiving.x`) pink armchair reading seat, desk front, bed side, `-Z` window gaze anchor;
  - Dining/Kitchen (`x > Door_LivingKitchen.x`) bar stool seat / dining table seat + kitchen counter/window gaze anchor;
  - Doorway approach/exit node pairs flanking `Door_BedroomLiving` (`x = Door.x -/+ 0.72f`, aligned with `HandleMesh` side along `z`) and `Door_LivingKitchen` (`x = Door.x -/+ 0.72f`); never place nodes at `x < Door_BathBedroom.x`.
- Snap every standing/waypoint coordinate onto nearest grid point (`0.05 m`) passing `Physics.CheckCapsule` (`radius 0.30 m`, bottom `y + 0.32f`, top `y + 1.40f`) + floor raycast (`y` near `0`).
- Perform full segment `Physics.CapsuleCast` sweep (`radius 0.30 m`) over all `links` (temporarily disabling ONLY the `DoorId` leaf colliders when sweeping that doorway link) and fail with exit code 1 if ANY collision occurs or any anchor fails reachability from `S1`.
- Add `powershell -ExecutionPolicy Bypass -File tools/unity.ps1 flatanchors` step in plan and run it to write `Anchors/anchors-flat.json` + `StreamingAssets/mas3d/anchors-flat.json`.

- [ ] **Step 5: Run EditMode tests (`FlatAnchorTests` + `EdgeTableTests`) to verify pass**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 flatanchors
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode EditMode --output d:\Mas3D\apps\unity\TestResults\Task2.xml --report-format nunit --filter "Mas3D.Tests.FlatAnchorTests|Mas3D.Tests.EdgeTableTests"
```
Expected: `[flat-anchors]` logs 0 collisions across all swept flat edges; EditMode suite PASS (`failed="0"`).

---

### Task 3: Cross-Room Doorway Traversal & Single-Arm `TwoBoneIk` Push in `AvatarWalker`

**Files:**
- Modify: `apps/unity/Assets/Scripts/Room/InteractiveDoor.cs:1-321`
- Modify: `apps/unity/Assets/Scripts/Avatar/AvatarWalker.cs:85-564`
- Create: `apps/unity/Assets/Tests/PlayMode/FlatNavigationTests.cs`

**Interfaces:**
- Consumes: `WalkEdge.DoorId`, `EdgeTable.FindEdge(string, string)`, `InteractiveDoor` (`IsOpen`, `IsMoving`, `SetOpen(bool, bool)`), `TwoBoneIk.Solve(Transform, Transform, Vector3, Vector3)`, `AvatarIdle.BoneClaim`.
- Produces:
  - `InteractiveDoor.HandleWorldPosition()` (`public Vector3`): returns live bounds center of `HandleMesh` transform/renderer (fallback `InteractionCenter`).
  - `AvatarWalker.OpeningDoor` (`public bool`): true while paused at a closed doorway pushing/waiting for door clearance.
  - `AvatarWalker.ActiveDoor` (`public InteractiveDoor`): non-null during `OpeningDoor`.
  - `AvatarWalker.DoorLookupRoot` (`public GameObject`): optional room root hint (`RoomBootstrap` assigns `Room`) for scoped `InteractiveDoor` lookup; falls back to `FindObjectsByType<InteractiveDoor>`.
  - `AvatarWalker.DriveDoorReachIk()`: internal/LateUpdate & end-of-Tick arm IK solver targeting `ActiveDoor.HandleWorldPosition()`, claiming `UpperArm`/`LowerArm` safely while active and releasing cleanly once open/stopped.

- [ ] **Step 1: Write failing PlayMode tests for full flat sweep, closed-door handle IK + push, already-open pass-through, and doorway player-blocking**

Create `apps/unity/Assets/Tests/PlayMode/FlatNavigationTests.cs` instantiating baked `Flat.prefab` + spawning `AvatarActor`:
1. `EveryFlatEdgeSweepsZeroCollisionsIgnoringMatchingDoorLeaf`: loads flat table, rebuilds `EdgeTable`, sweeps every `WalkEdge` with `Physics.CapsuleCast(radius = 0.30f)` (ignoring only the target `DoorId` leaf colliders on doorway links), asserts 0 hits and verifies zero bathroom nodes exist.
2. `WalkingFromLivingToBedroomThroughClosedDoorUsesHandleIkOpensDoorAndArrives`: keeps `Door_BedroomLiving` closed (`IsOpen == false`), starts `Walker.GoTo(bedroomNode)`, steps frames via `Tick(1f/60f)` + `door.Tick(1f/60f)` (and verifies in real `Update`/`LateUpdate`), asserts `Walker.OpeningDoor` triggers `door.SetOpen(true)`, asserts wrist bone moves measurably toward `door.HandleWorldPosition()` compared to idle rest during the swing, and asserts `Walker.Arrived == true && Walker.At == bedroomNode && door.IsOpen == true`.
3. `AlreadyOpenDoorPassesThroughWithoutStoppingToPush` & `PlayerInDoorwayBlocksCrossRoomStepAfterOpening`: covers **Review Focus #3** (`Door_LivingKitchen` open skips push; player `CharacterController` placed on far side of closed `Door_BedroomLiving` pauses walk (`Blocked == true`) without intersecting player).

- [ ] **Step 2: Run PlayMode `FlatNavigationTests` to confirm failure**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode PlayMode --output d:\Mas3D\apps\unity\TestResults\Task3.xml --report-format nunit --filter "Mas3D.Tests.FlatNavigationTests"
```
Expected: FAIL (`HandleWorldPosition`, `OpeningDoor`, `DoorLookupRoot` missing).

- [ ] **Step 3: Implement `HandleWorldPosition()` and doorway IK push in `AvatarWalker`**

- In `apps/unity/Assets/Scripts/Room/InteractiveDoor.cs`: cache or find child transform matching `HandleMesh`; add `public Vector3 HandleWorldPosition()` reading live `Renderer.bounds.center` (fallback collider bounds center / `InteractionCenter`).
- In `apps/unity/Assets/Scripts/Avatar/AvatarWalker.cs`:
  - Fix `TakeTheSeatIfNeeded` (`:351`) so `"bed"` seating checks `seat.pose == "bed"` (falling back to `ActionDirector.BedSeatId` only when active table has no `links`), preventing living sofa slots from triggering mattress pose.
  - Before moving along segment `_route[_index - 1] -> _route[_index]`, query `edge = EdgeTable.FindEdge(prev.Id, node.Id)`. If `!string.IsNullOrEmpty(edge?.DoorId)` and matching `door` has `!door.IsOpen || door.IsMoving`:
    - Enter `OpeningDoor = true`, snapshot arm local rotations if not already captured, smooth-turn toward doorway handle/midpoint, call `door.SetOpen(true, immediate: false)` once, ramp `_doorIkWeight` over swing (~0.12 s rise), wait until `door.IsOpen && !door.IsMoving`, ramp down/restore arm pose, then proceed along the segment.
  - Apply `DriveDoorReachIk()` both at end of `Tick()` (supporting manual `DriveInUpdate = false` test stepping) and in `LateUpdate()` (so live play wins after `AvatarIdle` breathing writes upper torso!). Use right arm (`RightUpperArm`/`RightLowerArm`) when handle is on actor's right/front-right, else left arm, with downward/outward elbow pole.
  - Clear doorway arm claim on `StopQuietly`, `StopAt`, `OnDisable`, or arrival.

- [ ] **Step 4: Run `FlatNavigationTests` + `AvatarWalkTests` + `EdgeTableTests` to verify pass and no regressions**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode PlayMode --output d:\Mas3D\apps\unity\TestResults\Task3.xml --report-format nunit --filter "Mas3D.Tests.FlatNavigationTests|Mas3D.Tests.AvatarWalkTests"
```
Expected: PASS (`failed="0"`).

---

### Task 4: Default Flat Boot, Face-to-Face Living Spawn, Living-Sofa Actions & Test Migration

**Files:**
- Modify: `apps/unity/Assets/Scripts/RoomBootstrap.cs:30-378`
- Modify: `apps/unity/Assets/Scripts/Avatar/AvatarActor.cs:69-185`
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs:400-465, 980-1075`
- Modify: `apps/unity/Assets/Scripts/Actions/ActionSeats.cs:11-44`
- Modify: `apps/unity/Assets/Editor/CliPlayer.cs:90-170`
- Modify: `apps/unity/Assets/Tests/PlayMode/SceneFlow.cs:18-81`
- Modify: `apps/unity/Assets/Tests/PlayMode/FlatCeilingTests.cs`, `FlatDoorTests.cs`, `BedroomSkyCardTests.cs`, `AvatarSceneTests.cs`, `ActionSceneTests.cs`, `ActionPerformanceShotTests.cs`, `PlayerFacingShotTests.cs`, `BakedBodyTests.cs` (update bedroom-calibrated assertions to use `SceneFlow.LoadBedroom()` / `boot.ShowBedroom()`, and align `FlatCeilingTests` / `FlatDoorTests` with default-flat contract)
- Create: `apps/unity/Assets/Tests/PlayMode/FlatActionTests.cs`

**Interfaces:**
- Consumes: `AnchorTable.SetActiveRelativePath`, `AnchorTable.ResetActive`, `EdgeTable.Rebuild()`, `BootArgs.Bedroom`, `AnchorTable.defaultSpawn`.
- Produces:
  - `RoomBootstrap.ShowBedroom()` (`public void`): forces legacy single-bedroom boot (`-bedroom 1` equivalent).
  - `RoomBootstrap.IsFlatMode` (`public bool`), `RoomBootstrap.HasBooted` (`public bool`).
  - `SceneFlow.LoadBedroom()` (`internal static IEnumerator` + `RoomBootstrap` lookup / calls `ShowBedroom()` before `WaitForBoot`).
  - `ActionDirector` refusal message when `Actor` is walking or on a non-intimate peripheral seat/standing node (`!seat.intimate`): returns `false` without moving player/camera.

- [ ] **Step 1: Write failing tests for default flat boot, startup race prevention, stale table cleanup, living-sofa 1–5 actions, and peripheral-seat refusal**

In `apps/unity/Assets/Tests/PlayMode/FlatActionTests.cs`:
1. `DefaultMainBootEntersFlatWithPlayerAndActorFaceToFaceAtLivingSofa`: calls `SceneFlow.LoadMain()` + `WaitForBoot(boot)` (no flags), asserts `boot.IsFlatMode == true`, `boot.Room == boot.FlatRoom`, `boot.FlatRoom.activeInHierarchy == true`, default bedroom/lights/sky-card inactive, `boot.Doors != null`, `boot.Actor.SeatId == "S1"` at living sofa world coordinates (verifies **Review Focus #1** — no bedroom-Y fallback!), and `Vector3.Dot(boot.Player.transform.forward, boot.Actor.transform.position - boot.Player.transform.position) > 0.5f`.
2. `UnloadingFlatMainResetsActiveAnchorTableAndEdgeGraphForLegacyBedroomTests`: loads & unloads `Main.unity`, verifies `AnchorTable.ActiveRelativePath == AnchorTable.DefaultRelativePath` and `EdgeTable.Node("A5") != null` (**Review Focus #2**).
3. `AllFiveActionsCompleteAndCancelOnLivingSofa`: enters `hands`, `pat-her`, `pat-me`, `kiss`, `sit-together` in flat mode, holds each cleanly, calls `Cancel()` and asserts player returns `< 0.05 m` without coffee-table collision.
4. `ActionsRefuseCleanlyWhenActorIsWalkingOrAtPeripheralSeat`: places `Actor` at bedroom reading chair (`seat.intimate == false`) and tests `TryEnterAction(ActionId.SitTogether)` / `ActionId.Kiss` / `ActionId.Hands`, asserts `false`, zero teleport, zero `ClearPoseCount` change, non-empty `LastFaults` (**Review Focus #5**).

Also invert `FlatCeilingTests` & `FlatDoorTests` default assertions (`DefaultBootEntersFlatAndDeactivatesBedroom` + `ShowBedroomRestoresLegacyBedroomWithNoDoorInteractor`).

- [ ] **Step 2: Run `FlatActionTests` + `FlatCeilingTests` + `FlatDoorTests` to confirm failure**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode PlayMode --output d:\Mas3D\apps\unity\TestResults\Task4-red.xml --report-format nunit --filter "Mas3D.Tests.FlatActionTests|Mas3D.Tests.FlatCeilingTests|Mas3D.Tests.FlatDoorTests"
```
Expected: FAIL (`RoomBootstrap.ShowBedroom`, `HasBooted`, `LoadBedroom` missing; `Boot()` still activates bedroom by default).

- [ ] **Step 3: Implement `RoomBootstrap` default flat flip, `AvatarActor.Start()` race guard, `ActionDirector` active-table/living-sofa support, `CliPlayer` flat anchor copy, and test suite migration**

- `apps/unity/Assets/Scripts/Avatar/AvatarActor.cs`: in `Start()`, check `var boot = FindAnyObjectByType<RoomBootstrap>(); if (boot != null && !boot.HasBooted) return;` so `RoomBootstrap.Boot()` owns the initial seating deterministically once the active room & table are chosen.
- `apps/unity/Assets/Scripts/RoomBootstrap.cs`:
  - Add `ShowBedroom()` (`_bedroomForced = true`), `IsFlatMode`, `HasBooted`, keep `ShowFlat()` clearing `_bedroomForced = false`.
  - Compute `flatMode = FlatRoom != null && !_bedroomForced && !StartupArgs.Bedroom` (retaining `-flat` compatibility as explicit noop when flat is already default).
  - When `flatMode`: `AnchorTable.SetActiveRelativePath(AnchorTable.FlatRelativePath)`, `EdgeTable.Rebuild()`, disable default bedroom/lights/sky-card, activate `FlatRoom`, load `Anchors = AnchorTable.Load()`, place `Player` from `Anchors.defaultSpawn` (fallback `FlatSpawn`/`FlatYaw`), seat `Actor` onto flat `S1` (`Seat.Stand` reset first if needed, then `SitAt(spawnSeatId)` -> re-capture `Idle` -> `FitBody`), bind `Walker` (`Walker.DoorLookupRoot = FlatRoom`), `Actions`, `Doors`.
  - When `!flatMode`: `AnchorTable.ResetActive()`, `EdgeTable.Rebuild()`, run legacy bedroom/house behavior.
  - Add `OnDestroy()` restoring `AnchorTable.ResetActive(); EdgeTable.Rebuild();` so unloading `Main.unity` never leaks flat tables into subsequent tests.
- `apps/unity/Assets/Scripts/Actions/ActionDirector.cs` & `ActionSeats.cs`:
  - Load active table (`AnchorTable.Load()`, which respects active flat/bedroom path); in `ResolvePlan`, reject if `Walker != null && Walker.Walking` (`"她正在走动，请等她停下"`).
  - For `SitTogether`, look up `BedSeatId` first (`S2` on flat sofa or bedroom mattress; if absent fallback to `S1` using `ActionSeats.BedPair`); if active table carries `intimate` flags and current/target seat has `!seat.intimate` (`table.links != null && !seat.intimate`), refuse cleanly with `"需要先回到客厅大沙发"` without touching player/camera.
  - On `Cancel()` / `RestoreEntering()` for `SitTogether`, re-seat `Actor` on her flat home seat (`S1`).
- `apps/unity/Assets/Editor/CliPlayer.cs`: copy `Anchors/anchors-flat.json` (if present) alongside `Anchors/anchors.json` into `Assets/StreamingAssets/mas3d/` during `BuildScene()`.
- Update `SceneFlow.cs` (`LoadBedroom()`), migrate legacy single-bedroom tests (`BedroomSkyCardTests`, `AvatarSceneTests`, `ActionSceneTests`, `ActionPerformanceShotTests`, `PlayerFacingShotTests`, `BakedBodyTests`) to `SceneFlow.LoadBedroom()`, and verify `ActionSmokeTests` passes natively on default Flat `Main.unity` (or note any action-timing calibration adjustment on `FlatRoom` furniture bounds if needed).

- [ ] **Step 4: Rebuild `Main.unity` and verify default flat tests, `ActionSmokeTests`, and legacy bedroom suites**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 player
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode PlayMode --output d:\Mas3D\apps\unity\TestResults\Task4-green.xml --report-format nunit --filter "Mas3D.Tests.FlatActionTests|Mas3D.Tests.FlatCeilingTests|Mas3D.Tests.FlatDoorTests|Mas3D.Tests.ActionSmokeTests|Mas3D.Tests.BedroomSkyCardTests|Mas3D.Tests.AvatarSceneTests|Mas3D.Tests.ActionSceneTests"
```
Expected: PASS (`failed="0"`).

---

### Task 5: Restrained Idle Routine (`AvatarRoutine`) Across Non-Bathroom Flat Zones

**Files:**
- Create: `apps/unity/Assets/Scripts/Avatar/AvatarRoutine.cs`
- Modify: `apps/unity/Assets/Scripts/RoomBootstrap.cs` (add `public AvatarRoutine Routine;` wired in Flat mode, disabled in Bedroom mode)
- Create: `apps/unity/Assets/Tests/EditMode/RoutineTests.cs` (pure `Tick(dt)` gating & non-bathroom pool)
- Modify: `apps/unity/Assets/Tests/PlayMode/FlatNavigationTests.cs` (add PlayMode routine preemption & test opt-out check)

**Interfaces:**
- Consumes: `AvatarWalker` (`Walking`, `At`, `GoTo(string)`), `ActionDirector` (`Current`), `ConversationController` (`Phase`), `DialoguePanel` (`IsInputFocused`), active `AnchorTable` (`anchors`, `seats`).
- Produces:
  - `public sealed class AvatarRoutine : MonoBehaviour`
  - Properties: `Enabled` (`bool`, default `true`), `DriveInUpdate` (`bool`, default `true`), `IdleSeconds` (`float`, default `60f`), `Walker`, `Actions`, `Conversation`, `Panel`, `NextTarget` (`string`), `SecondsUntilNext` (`float`).
  - Methods: `Tick(float dt)`, `ResetTimer()`, `PickNextNodeId()` (reads active `AnchorTable` entries excluding `"bathroom"` and excluding current `Walker.At` / `Actor.SeatId`, iterating deterministically so consecutive triggers traverse distinct lifestyle spots across Bedroom/Living/Kitchen without rapid bounce).

- [ ] **Step 1: Write failing EditMode & PlayMode tests for `AvatarRoutine`**

In `RoutineTests.cs` (EditMode):
1. `DoesNotTriggerBeforeIdleIntervalAndSkipsBathroomNodes`: sets active flat table, drives `Tick(1f)` for `59f` (asserts zero walk requests), advances past `IdleSeconds`, asserts target chosen is non-null, `room != "bathroom"`, and rotates across distinct rooms without repeating immediate node.
2. `ResetOrPausesWhenEnabledFalseOrDialogueOrActionsOrTypingActive`: tests `Enabled = false` opt-out, `TurnPhase.Thinking`, `Panel.IsInputFocused`, and active `Actions.Current` — asserts timer stays held/reset and never calls `GoTo`.
In `FlatNavigationTests.cs` (PlayMode):
3. `RoutineStartsCrossRoomWalkAfterIdleWaitAndPreemptsOnThinkingPhase`: covers **Review Focus #4** — triggers routine walk toward another room, injects `TurnPhase.Thinking` mid-walk, verifies walk stops gracefully at next safe node/step cleanly and routine stays paused while `Thinking`.

- [ ] **Step 2: Run `RoutineTests` and `FlatNavigationTests` to confirm failure**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode EditMode --output d:\Mas3D\apps\unity\TestResults\Task5-edit.xml --report-format nunit --filter "Mas3D.Tests.RoutineTests"
```
Expected: FAIL (`AvatarRoutine` type missing).

- [ ] **Step 3: Implement `AvatarRoutine` and wire it in `RoomBootstrap`**

- Implement `apps/unity/Assets/Scripts/Avatar/AvatarRoutine.cs`:
  - Expose `Enabled`, `DriveInUpdate`, `IdleSeconds` (default `60f`), `PhaseSource` / `BusySource` optional delegates for pure EditMode testability alongside scene component references (`Walker`, `Actions`, `Conversation`, `Panel`).
  - Cache non-bathroom candidate node IDs ordered smoothly across lifestyle regions from `AnchorTable.Load()` (resetting cleanly on `ResetPool()` / room boot).
  - In `Update()` call `Tick(Time.deltaTime)` only if `DriveInUpdate`.
  - In `Tick(float dt)`: return and call `ResetTimer()` whenever `!Enabled || Walker == null || Walker.Walking || DialogueBusy() || TypingActive() || ActionActive()`; increment elapsed timer once idle; when `elapsed >= IdleSeconds`, pick next distinct non-bathroom spot, call `Walker.GoTo(nextId)`, reset timer.
- Wire in `RoomBootstrap.Boot()`: create/reuse `AvatarRoutine` on `Actor`, set `Enabled = flatMode`, bind references; in `ActionSmokeTests` / deterministic scene capture harness disable `boot.Routine.Enabled = false` where smoke expects static actor before dialogue/action checks.

- [ ] **Step 4: Run EditMode `RoutineTests` + PlayMode `FlatNavigationTests` to verify pass**

Run:
```powershell
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode EditMode --output d:\Mas3D\apps\unity\TestResults\Task5-edit.xml --report-format nunit --filter "Mas3D.Tests.RoutineTests"
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" --no-banner --non-interactive test d:\Mas3D\apps\unity --mode PlayMode --output d:\Mas3D\apps\unity\TestResults\Task5-play.xml --report-format nunit --filter "Mas3D.Tests.FlatNavigationTests"
```
Expected: PASS (`failed="0"`).

---

### Task 6: Scene Bake & End-to-End Multi-Gate Verification

**Files:**
- Regenerate: `apps/unity/Anchors/anchors-flat.json`, `apps/unity/Assets/StreamingAssets/mas3d/anchors-flat.json`, `apps/unity/Assets/Scenes/Main.unity`
- Verify: EditMode, PlayMode, and Smoke suites via `tools/unity.ps1` and `tools/check.ps1`

**Interfaces:**
- Consumes: `CliFlatAnchors.Solve`, `CliPlayer.BuildScene`, `tools/check.ps1`.
- Produces: Clean baked `Main.unity` booting directly into the living-room flat experience + passing full automated gates.

- [ ] **Step 1: Regenerate flat anchor graph and rebuild `Main.unity`**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 flatanchors
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 player
```
Expected: `[flat-anchors]` 0 swept collisions; `[player] scene written: Assets/Scenes/Main.unity`.

- [ ] **Step 2: Run full Unity & smoke regression (`check.ps1`)**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File tools/check.ps1 -SkipWeb -SkipBridge -SkipPlayer
```
Expected:
- `unity-edit PASS` (with `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` remaining skipped by existing `Assert.Ignore`)
- `unity-play PASS`
- `smoke PASS` (`BridgeSmokeTests` + `ActionSmokeTests` running on default Flat `Main.unity`, marker lines verified)
- `summary: 3/3 ok`, exit code `0`.

- [ ] **Step 3: Capture visual verification still of default living-room launch**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 capture
```
Read the generated `UiScene` / default first-person still via `read_file` to confirm visually that default launch faces Monika seated on the living-room 3-seat sofa without UI clipping or broken framing.

- [ ] **Step 4: Re-check OpenSpec state and wait for user review before any git commit**

Run:
```powershell
openspec validate --all --strict
```
Expected: all specs and active change valid (`0 failed`). Do **not** run `git commit` or `git push` until the user explicitly confirms acceptance (`AGENTS.md`).
