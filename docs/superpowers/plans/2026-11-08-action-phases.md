# Action Performances, Plan 1: The Three Phases Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give all five actions a visible entry performance, an alive hold, and an exit performance, and bend the player's legs into the stance the action names.

**Architecture:** `ActionDirector` gains a `None/Enter/Hold/Leave` clock; entry walks the player along a `PlayerPath` into the plan's spot while the arms are blended in, the hold keeps breathing through a deterministic micro-motion layer, and Escape plays a leave before the existing instant `Cancel()` restores everything. No generated assets yet — every phase is procedural, so all five actions ship three phases at the end of this plan.

**Tech Stack:** Unity 6000.6.7f1, URP, C# runtime scripts in `apps/unity/Assets/Scripts`, NUnit PlayMode tests driven by the Unity CLI (`%LOCALAPPDATA%\Unity\bin\unity.exe`).

**Spec:** `docs/superpowers/specs/2026-11-08-action-performances-design.md`

## Global Constraints

- Do **not** run `git commit` or `git push` (AGENTS.md). Leave the worktree dirty; the Lead collects the diff and YG decides the commit.
- No new Unity packages. No `AnimationClip`, no `Animator`: the avatar's `Animator` stays disabled (`AvatarRig.cs:92`).
- Every new runtime file is ASCII-only C# under `apps/unity/Assets/Scripts`; test files live under `apps/unity/Assets/Tests/PlayMode`.
- Phase durations are the spec's numbers, verbatim: hands 2.6 / 1.6 s; pat-her 0.9 / 0.7; pat-me 0.9 / 0.7; kiss 1.4 / 1.0; sit-together 2.2 / 1.4.
- `Cancel()` keeps restoring instantly and `ClearPoseCount` still counts exactly one restore per action. Escape alone goes through `RequestLeave()`.
- Contact tolerance stays `ActionDirector.ContactTolerance = 0.02f`; `Held` accrues only in `Hold`.
- The player's capsule (`CharacterController`) is never swept into the bed: `Approach` only walks, `Settle` teleports.
- All angle math uses degrees at the API edge (`Vector3` Euler tables, `yaw` in degrees) and radians only inside `Mathf.Sin`.
- Four existing suites must not regress: `PlayerBodyTests`, `PlayerFacingTests`, `BakedBodyTests`, `PlayerSelfTestBodyTests`.

## Review Focus

1. **Escape pressed mid-entry** — a half-walked player must leave from where they stand and come home to the exact stored spot, never snap mid-blend.
2. **Contact during a blend** — while IK weight is under 1 the palms may not touch; `Held` must stay 0 and `ContactError` must not be read as a failure.
3. **A hold left running for minutes** — micro-motion is a bounded sine, never an integrating offset; no drift, no NaN, no frame-rate dependence.
4. **Bare rig in tests** — actions are entered with no `PlayerBody` (the common fixture); leg work must no-op instead of throwing.
5. **Crouch/kneel/sit on the real body** — soles must stay within 1.5 cm of the floor while knees fold; the eye height is owned by `PlayerMotor`, not recomputed here.

---

### Task 1: The phase clock and the exit request

**Files:**
- Create: `apps/unity/Assets/Scripts/Actions/ActionPhase.cs`
- Create: `apps/unity/Assets/Scripts/Actions/PerformanceTiming.cs`
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs:128-166` (fields/properties/Update), `:239-243` (entry state), `:337-354` (Tick), `:290-331` (Cancel reset)
- Create: `apps/unity/Assets/Tests/PlayMode/ActionPhaseTests.cs`
- Create: `apps/unity/Assets/Tests/PlayMode/ActionTestDrive.cs`
- Modify: the eight action test files listed in the spec §6.1 (only the entry/hold timing assertions)

**Interfaces:**
- Consumes: `ActionDirector.TryEnterAction(string)`, `Tick(float)`, `Cancel()`, `Current`, `Held`, `ClearPoseCount` as they exist today.
- Produces:
  - `public enum ActionPhase { None, Enter, Hold, Leave }` in `Mas3D.Actions`.
  - `public static float PerformanceTiming.Enter(string id)` and `public static float PerformanceTiming.Leave(string id)` — seconds, `0f` for an unknown id.
  - `public static float PerformanceTiming.IkWeight(ActionPhase phase, float u)` with `u` in 0..1: Enter = smoothstep of `(u-0.7)/0.3`, Hold = 1, Leave = `1 - smoothstep(u/0.3)`, None = 0.
  - `ActionDirector.Phase` (`ActionPhase`), `PhaseTime`, `PhaseDuration`, `PhaseWeight` (float, read-only).
  - `public bool RequestLeave()` — false while idle or already leaving.
  - `ActionTestDrive.ToHold(ActionDirector d, float maxSeconds = 6f)`, `.ToLeaveEnd(ActionDirector d, float maxSeconds = 6f)`, `.Hold(ActionDirector d, float seconds)`, all static, stepping `1f/60f`.

- [ ] **Step 1: Write the failing tests**

`ActionPhaseTests.cs` — four tests on a hands fixture (copy the fixture from `ActionHandsTests.cs:20-55`):

```csharp
[Test] public void EnterRunsForItsNominalTimeThenHolds()
{
    var d = HandsDirector();
    Assert.That(d.TryEnterAction(ActionId.Hands), Is.True);
    Assert.That(d.Phase, Is.EqualTo(ActionPhase.Enter));
    ActionTestDrive.Hold(d, 2.5f);
    Assert.That(d.Phase, Is.EqualTo(ActionPhase.Enter), "2.5 s of a 2.6 s entry");
    ActionTestDrive.Hold(d, 0.2f);
    Assert.That(d.Phase, Is.EqualTo(ActionPhase.Hold));
    Assert.That(d.PhaseTime, Is.LessThan(ActionTestDrive.Step), "only the overdue fraction crosses the flip");
    Assert.That(d.Held, Is.EqualTo(0f), "the frame that flips into the hold credits no contact");
}

[Test] public void HeldOnlyCountsInsideHold()
{
    var d = HandsDirector();
    d.TryEnterAction(ActionId.Hands);
    ActionTestDrive.Hold(d, 2.55f);
    Assert.That(d.Held, Is.EqualTo(0f), "an entry never credits contact");
    ActionTestDrive.Hold(d, 1.2f);
    Assert.That(d.Held, Is.GreaterThan(0.5f));
}

[Test] public void EscapeRequestsALeaveAndTheEndOfItRestores()
{
    var d = HandsDirector();
    d.TryEnterAction(ActionId.Hands);
    ActionTestDrive.ToHold(d);
    Assert.That(d.RequestLeave(), Is.True);
    Assert.That(d.Phase, Is.EqualTo(ActionPhase.Leave));
    ActionTestDrive.Hold(d, 0.8f);
    Assert.That(d.Current, Is.EqualTo(ActionId.Hands), "half a leave is still a leave");
    ActionTestDrive.Hold(d, 1.0f);
    Assert.That(d.Current, Is.Null);
    Assert.That(d.Phase, Is.EqualTo(ActionPhase.None));
    Assert.That(d.ClearPoseCount, Is.EqualTo(1));
}

[Test] public void CancelStaysInstantaneous()
{
    var d = HandsDirector();
    d.TryEnterAction(ActionId.Hands);
    ActionTestDrive.Hold(d, 0.3f);
    d.Cancel();
    Assert.That(d.Current, Is.Null);
    Assert.That(d.ClearPoseCount, Is.EqualTo(1));
}
```

And a pure timing test:

```csharp
[Test] public void TheWeightCurveIsFlatUntilSeventyPercent()
{
    Assert.That(PerformanceTiming.IkWeight(ActionPhase.Enter, 0.7f), Is.EqualTo(0f).Within(1e-4));
    Assert.That(PerformanceTiming.IkWeight(ActionPhase.Enter, 1f), Is.EqualTo(1f).Within(1e-4));
    Assert.That(PerformanceTiming.IkWeight(ActionPhase.Leave, 0f), Is.EqualTo(1f).Within(1e-4));
    Assert.That(PerformanceTiming.IkWeight(ActionPhase.Leave, 0.3f), Is.EqualTo(0f).Within(1e-4));
    Assert.That(PerformanceTiming.IkWeight(ActionPhase.Hold, 0f), Is.EqualTo(1f).Within(1e-4));
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `$unity = Join-Path $env:LOCALAPPDATA 'Unity\bin\unity.exe'; & $unity test D:\Mas3D\apps\unity --mode PlayMode --output D:\Mas3D\apps\unity\TestResults\phase-red.xml --report-format nunit --filter Mas3D.Tests.ActionPhaseTests`
Expected: build error `CS0246` for `ActionPhase` / `ActionTestDrive` (a red build is the failing test here).

- [ ] **Step 3: Implement the clock**

`ActionPhase.cs` is the enum. `PerformanceTiming.cs` is two `switch` expressions over `ActionId.Hands/PatHer/PatMe/Kiss/SitTogether` returning the spec's seconds, plus:

```csharp
public static float IkWeight(ActionPhase phase, float u)
{
    u = Mathf.Clamp01(u);
    switch (phase)
    {
        case ActionPhase.Enter: return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - 0.7f) / 0.3f));
        case ActionPhase.Hold: return 1f;
        case ActionPhase.Leave: return 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.3f));
        default: return 0f;
    }
}
```

In `ActionDirector`: fields `ActionPhase _phase; float _phaseTime; float _phaseDuration; float _leaveFrom = 1f;`; properties reading them, with `PhaseWeight` a block that computes `u = _phaseDuration > 0f ? _phaseTime / _phaseDuration : 0f` and returns `_leaveFrom * PerformanceTiming.IkWeight(ActionPhase.Leave, u)` while leaving and `PerformanceTiming.IkWeight(_phase, u)` otherwise. `TryEnterAction` sets `_phase = ActionPhase.Enter; _phaseTime = 0f; _phaseDuration = PerformanceTiming.Enter(id); _leaveFrom = 1f;` next to `_current = id;`. `Update` changes the Escape branch to `RequestLeave()`, which captures `_leaveFrom = PhaseWeight` **before** it flips the phase (the leave curve starts at 1, so an escape mid-entry would otherwise snap the weight up). `Tick` advances the clock after the drive switch and before the contact credit:

```csharp
if (_phase == ActionPhase.Enter)
{
    _phaseTime += dt;
    if (_phaseTime >= _phaseDuration)
    {
        // The overdue fraction carries into the hold (the micro-motion layer drives off this clock),
        // and the flip frame returns before the contact credit: it is neither an entry nor a hold.
        _phaseTime -= _phaseDuration;
        _phase = ActionPhase.Hold;
        _phaseDuration = 0f;
        return;
    }
}
else if (_phase == ActionPhase.Leave)
{
    _phaseTime += dt;
    if (_phaseTime >= _phaseDuration) { Cancel(); return; }
}
else if (_phase == ActionPhase.Hold)
{
    _phaseTime += dt;   // the hold keeps its own clock: it neither stops nor rewinds
}
// credit only in the hold
if (_phase == ActionPhase.Hold && _contactError <= ContactTolerance) _held += dt;
else if (_phase == ActionPhase.Hold) _held = 0f;
```

`Cancel` and `RestoreEntering` reset `_phase = ActionPhase.None; _phaseTime = 0f; _phaseDuration = 0f; _leaveFrom = 1f;` inside their existing `finally` / tail. Do **not** touch the IK weight yet (Task 2 blends it).

- [ ] **Step 4: Migrate the old timing assertions**

Add `ActionTestDrive` (three helpers, each stepping `1f/60f` and asserting the phase changed before `maxSeconds`). Then in `ActionHandsTests`, `ActionPatTests`, `ActionKissTests`, `ActionSitTogetherTests`, `ActionDirectorTests`, `ActionHotkeysTests`, `ActionSceneTests`, `ActionSmokeTests`: wherever a test ticks past an entry and then asserts contact or `Held`, insert `ActionTestDrive.ToHold(d);` first. Tests that call `Cancel()` or assert refusals stay untouched.

- [ ] **Step 5: Run the focused tests, then the whole action suite**

Run the same command with `--filter Mas3D.Tests.Action` and expect 0 failed; then the whole suite with no filter (there is no `Mas3D.Tests.PlayMode` filter that matches anything) and expect the same skip count as before (two `[Explicit]` cases).

- [ ] **Step 6: Do not commit; stage the review package only**

`git add apps/unity/Assets/Scripts/Actions/ActionPhase.cs apps/unity/Assets/Scripts/Actions/PerformanceTiming.cs apps/unity/Assets/Scripts/Actions/ActionDirector.cs apps/unity/Assets/Tests/PlayMode/ActionPhaseTests.cs apps/unity/Assets/Tests/PlayMode/ActionTestDrive.cs` plus each migrated test file, then leave it staged for the Lead.

---

### Task 2: Entry walks, leave lets go

**Files:**
- Create: `apps/unity/Assets/Scripts/Actions/PlayerPath.cs`
- Modify: `apps/unity/Assets/Scripts/PlayerMotor.cs:233-243` (add `MoveTo`), `apps/unity/Assets/Scripts/Actions/ActionDirector.cs:179-255` (entry), `:337-354` (drive with weight)
- Test: `apps/unity/Assets/Tests/PlayMode/ActionEntryTests.cs`

**Interfaces:**
- Consumes: the Task 1 clock and `PhaseWeight`; `PlayerMotor.SetPosture`, `Teleport`, `Head`.
- Produces:
  - `public struct PlayerPath { public Vector3 From, To; public float FromYaw, ToYaw; public float Length; public static PlayerPath Between(Vector3 from, float fromYaw, Vector3 to, float toYaw); public void Evaluate(float u, out Vector3 feet, out float yaw); }` — `u` smoothstepped, yaw via `Mathf.LerpAngle`.
  - `public void PlayerMotor.MoveTo(Vector3 feet, float yawDegrees, float dt)` — rotates with `TurnRate`, then `_controller.Move(feet - transform.position)` with `_vertical = 0f`.
  - `public const float ActionDirector.MaxApproach = 2.5f;` and `public const float ApproachArrive = 0.05f;`
  - `public bool ActionDirector.IsSettle(string id)` — true for `ActionId.Kiss` and `ActionId.SitTogether`.
  - `public Vector3 ActionDirector.LastSpot` — the spot the last entry resolved, for tests.

- [ ] **Step 1: Write the failing tests**

`ActionEntryTests.cs`:

```csharp
[Test] public void HandsEntryWalksThePlayerInInsteadOfTeleporting()
{
    var d = HandsDirector();
    d.Player.transform.position = d.Actor.Rig.Bone(HumanBodyBones.Hips).position
        + new Vector3(1.2f, 0f, 0f);            // 1.2 m off the spot, still inside the room
    var start = d.Player.transform.position;
    Assert.That(d.TryEnterAction(ActionId.Hands), Is.True);
    ActionTestDrive.Hold(d, 0.2f);
    Assert.That(Vector3.Distance(d.Player.transform.position, start), Is.LessThan(0.4f),
        "one fifth of the entry, not a teleport");
    ActionTestDrive.ToHold(d);
    Assert.That(Vector3.Distance(d.Player.transform.position, d.LastSpot), Is.LessThan(0.06f));
}

[Test] public void KissAndSitTogetherDoNotTravel()
{
    foreach (var id in new[] { ActionId.Kiss, ActionId.SitTogether })
    {
        var d = DirectorFor(id);
        var start = d.Player.transform.position;
        Assert.That(d.TryEnterAction(id), Is.True, id);
        ActionTestDrive.ToHold(d);
        Assert.That(Vector3.Distance(d.Player.transform.position, start), Is.LessThan(0.001f), id);
    }
}

[Test] public void EscapeDuringEntryLeavesFromWhereTheWalkReached()
{
    var d = HandsDirector();
    d.Player.transform.position += new Vector3(1.2f, 0f, 0f);
    var home = d.Player.transform.position;
    d.TryEnterAction(ActionId.Hands);
    ActionTestDrive.Hold(d, 1.3f);
    var mid = d.Player.transform.position;
    Assert.That(d.RequestLeave(), Is.True);
    ActionTestDrive.Hold(d, 0.2f);
    Assert.That(Vector3.Distance(d.Player.transform.position, mid), Is.LessThan(0.001f),
        "the leave performs in place");
    ActionTestDrive.ToLeaveEnd(d);
    Assert.That(Vector3.Distance(d.Player.transform.position, home), Is.LessThan(0.02f));
}
```

Fixtures: `HandsDirector()` as in Task 1; `DirectorFor(id)` sets the posture the plan requires (`Crouching` for pat-me, `Kneeling` for kiss) and returns a fresh director. Give the hands fixture a room floor so the walk has something to stand on.

- [ ] **Step 2: Run them and confirm they fail**

Same CLI, `--filter Mas3D.Tests.ActionEntryTests`. Expected: `HandsEntryWalks...` fails on the 0.4 m assertion (the current entry teleports) and the build is otherwise green.

- [ ] **Step 3: Implement PlayerPath and PlayerMotor.MoveTo**

`PlayerPath.Evaluate`: `var s = u*u*(3f-2f*u); feet = Vector3.Lerp(From, To, s); yaw = Mathf.LerpAngle(FromYaw, ToYaw, s);`. `Length` is the flat distance.

`MoveTo` mirrors `Tick`'s turn line (`Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0f, yawDegrees, 0f), TurnRate * dt)`) then moves the controller by the remaining delta, zeroing `_vertical` and `_planar` so gravity does not fight the walk.

- [ ] **Step 4: Rewire entry**

In `TryEnterAction`, after `ResolvePlan` / `Reachable`: store `LastSpot = plan.Spot`. Instead of the unconditional `Player.Teleport(plan.Spot, plan.Yaw)`:

```csharp
_pathActive = !IsSettle(id);
if (_pathActive)
{
    var from = Player.transform.position;
    if (Vector3.Distance(from, plan.Spot) > MaxApproach)
    {
        var back = from - plan.Spot;
        back.y = 0f;
        from = plan.Spot + back.normalized * MaxApproach;
        Player.Teleport(from, plan.Yaw);
    }
    Player.SetPosture(plan.Posture, plan.SeatTopY);
    _path = PlayerPath.Between(from, Player.transform.eulerAngles.y, plan.Spot, plan.Yaw);
}
else
{
    Player.SetPosture(plan.Posture, plan.SeatTopY);
    Player.Teleport(plan.Spot, plan.Yaw);
}
if (Camera != null)
{
    Camera.SetCameraRadius(IsSettle(id) ? (id == ActionId.Kiss ? KissCameraRadius : RoomCameraRadius) : RoomCameraRadius);
    if (id == ActionId.Kiss) Camera.SetDistance(KissCameraDistance);
    if (!_pathActive) Camera.SetOrbit(plan.OrbitYaw, plan.OrbitPitch);
    Camera.MouseInput = false;
}
```

Approach eases the camera in `Tick` while `Phase == Enter`: `var p = Mathf.SmoothStep(0f, 1f, PhaseTime / PhaseDuration); Camera.SetOrbit(Mathf.LerpAngle(_wasCameraYaw, plan.OrbitYaw, p), Mathf.Lerp(_wasCameraPitch, plan.OrbitPitch, p));` and interpolates distance and radius the same way (kiss radius applies on frame one).

Each tick while `Phase == Enter` and `_pathActive`: `_path.Evaluate(PhaseTime / PhaseDuration, out feet, out yaw); Player.MoveTo(feet, yaw, dt);`. On the frame the phase flips to Hold, if the player is further than `ApproachArrive` from `_path.To`, `Player.Teleport(_path.To, _path.ToYaw)` and log `[stage] approach snapped {distance:F3}`.

Leave keeps the player where they stand (`_pathActive = false`) and reaches the same camera values in reverse by the weight curve.

- [ ] **Step 5: Blend the IK with PhaseWeight**

Wrap the arm solves: snapshot the six arm-bone `localRotation`s before `DriveArm`, solve, then `bone.localRotation = Quaternion.Slerp(solved, snapshot, 1f - w)` where `w = PhaseWeight`. For `DrivePatHer` blend the head the same way. `DriveKiss` / `DriveSitTogether` only measure, so they keep measuring (contact is simply not credited outside Hold).

- [ ] **Step 6: Run the focused tests**

`--filter Mas3D.Tests.ActionEntryTests` then `--filter Mas3D.Tests.Action` — expect green, and `ActionHandsTests` still passes through `ToHold`.

- [ ] **Step 7: Stage, do not commit**

`git add apps/unity/Assets/Scripts/Actions/PlayerPath.cs apps/unity/Assets/Scripts/PlayerMotor.cs apps/unity/Assets/Scripts/Actions/ActionDirector.cs apps/unity/Assets/Tests/PlayMode/ActionEntryTests.cs`

---

### Task 3: His legs fold into the stance

**Files:**
- Create: `apps/unity/Assets/Scripts/Actions/PlayerLegs.cs`
- Modify: `apps/unity/Assets/Scripts/Actions/ActionPose.cs` (the `ActionPoses` table class at line 122 holds the existing tables - append the three leg tables there, not in a new file), `apps/unity/Assets/Scripts/PlayerBody.cs:163-173` (`ApplyNow`, subtract the sole lift), `apps/unity/Assets/Scripts/Actions/ActionDirector.cs` (apply legs per frame, expose `Body`/`BodyRig`, restore on cancel)
- Test: `apps/unity/Assets/Tests/PlayMode/PlayerLegsTests.cs`

**Interfaces:**
- Consumes: `AvatarRig.Bone(HumanBodyBones)`, `PlayerMotor.Posture`, `PlayerBody.Avatar`.
- Produces:
  - `public static void PlayerLegs.Begin(AvatarRig rig)` — snapshot Left/Right UpperLeg, LowerLeg, Foot per rig (rig-keyed dictionary).
  - `public static void PlayerLegs.Apply(AvatarRig rig, PlayerPosture posture, float weight)` — writes `Slerp(snapshot, snapshot * Euler(table[bone]), weight)` and records `FootLift`.
  - `public static void PlayerLegs.Restore(AvatarRig rig)` and `public static void PlayerLegs.Forget()`.
  - `public static float PlayerLegs.FootLift(AvatarRig rig)` — metres the soles rose, 0 when nothing is applied.
  - `public static float PlayerLegs.KneeAngle(AvatarRig rig, bool left)` — degrees between thigh and shin.
  - Semantics: mirror `ActionPose.cs:52-114` exactly - `Begin` snapshots local rotations, `Apply` rebuilds every frame from that base (`base * Euler(offset)`, never compounding), `Restore` writes the base back verbatim, `Forget` drops it without touching bones. The one deliberate difference is that `PlayerLegs` keys its snapshot **by rig**: `ActionPose.Base` is a single static bone->quat map shared by every performer, so reusing it for the player legs would hand her bones the player's basis the moment both tables are live.
  - `ActionPoses.CrouchLegs / KneelLegs / SitLegs` — `IReadOnlyDictionary<HumanBodyBones, Vector3>` with six entries each.

**Fixture note (copy, do not invent).** `HandsDirector()` is already defined by Task 1 (see line 255); add `CrouchFixture()` beside it:
- real body: `_player.AddComponent<PlayerBody>()` + `Assert.IsTrue(_body.Spawn())`, guarded by `if (!PlayerBody.PrefabAvailable) Assert.Ignore(PlayerBody.BodyPrefabPath)` — the pattern is `PlayerBodyTests.cs:44-69`, and `RoomBootstrap.cs:114` does the same at runtime;
- director: the `ActionPatTests.cs:17-34` setup (`AvatarActor.Spawn()`, a "Player" with `CharacterController` + `PlayerMotor` + `EnsureReady()`, then `ActionDirector` with `KeyboardInput = false`, `DriveInUpdate = false`);
- `CrouchFixture()` = that plus `PlayerBody`, the player set to `PlayerPosture.Crouching`, and the action taken as `PatMe`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void TheTableBendsBothKnees()
{
    foreach (var table in new[] { ActionPoses.CrouchLegs, ActionPoses.KneelLegs, ActionPoses.SitLegs })
    {
        Assert.That(table.ContainsKey(HumanBodyBones.LeftLowerLeg), Is.True);
        Assert.That(table.ContainsKey(HumanBodyBones.LeftFoot), Is.True);
        Assert.That(Mathf.Abs(table[HumanBodyBones.LeftLowerLeg].x), Is.GreaterThan(40f));
    }
}

[Test] public void CrouchingBendsThePlayersLegs()
{
    var d = CrouchFixture();               // real Me body + PlayerMotor + her, pat-me posture
    d.TryEnterAction(ActionId.PatMe);
    ActionTestDrive.ToHold(d);
    Assert.That(PlayerLegs.KneeAngle(d.Body.Avatar, true), Is.GreaterThan(25f));
    Assert.That(PlayerLegs.KneeAngle(d.Body.Avatar, false), Is.GreaterThan(25f));
}

[Test] public void FootStaysOnTheFloor()
{
    var d = CrouchFixture();
    var floor = d.Player.transform.position.y;
    d.TryEnterAction(ActionId.PatMe);
    ActionTestDrive.ToHold(d);
    var sole = d.Body.Avatar.Bone(HumanBodyBones.LeftFoot).position.y;
    Assert.That(sole - floor, Is.InRange(-0.015f, 0.015f));
}

[Test] public void CrouchKeepsTheFootUnderTheHips()
{
    var d = CrouchFixture();
    d.TryEnterAction(ActionId.PatMe);
    ActionTestDrive.ToHold(d);
    var hips = d.Body.Avatar.Bone(HumanBodyBones.Hips).position;
    var foot = d.Body.Avatar.Bone(HumanBodyBones.LeftFoot).position;
    Assert.That(Mathf.Abs(foot.x - hips.x), Is.LessThan(0.10f));
    Assert.That(foot.z, Is.GreaterThan(hips.z), "the toe points forward, not through the shin");
}

[Test] public void ALegacyRigWithoutABodyDoesNotThrow()
{
    var d = HandsDirector();               // bare player, no PlayerBody
    Assert.DoesNotThrow(() => { d.TryEnterAction(ActionId.PatHer); ActionTestDrive.ToHold(d); d.Cancel(); });
}
```

- [ ] **Step 2: Run and confirm red**

`--filter Mas3D.Tests.PlayerLegsTests` — expect `CS0117` for the missing tables and helpers.

- [ ] **Step 3: Write the tables and PlayerLegs**

Starting values (Euler degrees in the bone's own space; the sign that folds the knee forward is the one that passes `CrouchKeepsTheFootUnderTheHips` below — confirm in Step 5):

```csharp
CrouchLegs: LeftUpperLeg (52,0,0), LeftLowerLeg (-98,0,0), LeftFoot (46,0,0)   // mirrored on the right
KneelLegs:  LeftUpperLeg (84,0,0), LeftLowerLeg (-150,0,0), LeftFoot (66,0,0)
SitLegs:    LeftUpperLeg (86,0,0), LeftLowerLeg (-86,0,0), LeftFoot (0,0,0)
```

`FootLift` = `max(0, soleYAfter - soleYBefore)` measured against the snapshot; `PlayerBody.ApplyNow` becomes `root.localPosition = new Vector3(0f, _feetOffset + (eye - standing) - PlayerLegs.FootLift(Avatar), 0f);`.

`ActionDirector` gains `public PlayerBody Body => Player != null ? Player.GetComponent<PlayerBody>() : null;` and `public AvatarRig BodyRig => Body != null ? Body.Avatar : null;`, calls `PlayerLegs.Begin(BodyRig)` right after `ActionPose.Begin`, applies the table for the plan's posture every frame with weight `PhaseWeight`, and calls `PlayerLegs.Restore(BodyRig)` in `ClearPose`.

- [ ] **Step 4: Run green**

`--filter Mas3D.Tests.PlayerLegsTests` then the whole `Action` filter.

- [ ] **Step 5: Eyeball the crouch and kneel**

The editor capture harness (`Mas3D.Editor.CliMotion`) poses its own actors and never runs `ActionDirector`, so it cannot show `PlayerLegs`. Take the shots from PlayMode instead: copy the RenderTexture pattern out of `PlayerFacingShotTests`, enter pat-me, drive to the hold with `ActionTestDrive`, and write `Shots/legs-crouch.png`; repeat with the kiss for `Shots/legs-kneel.png`. Open both PNGs; the knees must fold toward the room, not backwards through the shins. If the shot test is too slow today, say so in the report and leave the visual check to Task 5's strip - do not claim an eyeball check you did not make.

- [ ] **Step 6: Stage, do not commit**

`git add apps/unity/Assets/Scripts/Actions/PlayerLegs.cs apps/unity/Assets/Scripts/Actions/ActionPoses.cs apps/unity/Assets/Scripts/PlayerBody.cs apps/unity/Assets/Scripts/Actions/ActionDirector.cs apps/unity/Assets/Tests/PlayMode/PlayerLegsTests.cs`

---

### Task 4: The hold breathes

**Files:**
- Create: `apps/unity/Assets/Scripts/Actions/PerformanceMotion.cs`
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs` - one call per `Drive*` (`DriveHands` at 666, `DrivePatHer` at 719, `DrivePatMe` at 736, `DriveKiss` at 786, `DriveSitTogether` at 799 when this task starts; line numbers drift, so find them by name), placed between the pose table and the IK solve so the offsets are layered on the table rather than compounding from the previous frame
- Test: `apps/unity/Assets/Tests/PlayMode/PerformanceMotionTests.cs`

**Interfaces:**
- Produces: `public static class PerformanceMotion` with
  - `public static Vector3 HerHead(float t)`, `HerSpine(float t)`, `HerHand(bool left, float t)`, `HisSpine(float t)` — Euler offsets in degrees.
  - `public static void Apply(AvatarRig rig, string actionId, float t)` — adds the offsets to her bones' current `localRotation` (`current * Quaternion.Euler(offset)`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void TheHoldNeverFreezes()
{
    var d = HandsDirector();
    d.TryEnterAction(ActionId.Hands);
    ActionTestDrive.ToHold(d);
    var head = d.Actor.Rig.Bone(HumanBodyBones.Head);
    var first = head.rotation;
    ActionTestDrive.Hold(d, 2f);
    Assert.That(Quaternion.Angle(first, head.rotation), Is.GreaterThan(0.3f));
    Assert.That(d.ContactError, Is.LessThanOrEqualTo(ActionDirector.ContactTolerance));
}

[Test] public void TheMotionIsBoundedAndPeriodic()
{
    for (var t = 0f; t < 120f; t += 0.37f)
    {
        Assert.That(Mathf.Abs(PerformanceMotion.HerHead(t).x), Is.LessThan(1.3f));
        Assert.That(Mathf.Abs(PerformanceMotion.HerSpine(t).x), Is.LessThan(0.9f));
    }
    Assert.That(PerformanceMotion.HerSpine(0f), Is.EqualTo(PerformanceMotion.HerSpine(1f / 0.11f)).Within(1e-2));
}

[Test] public void NoBodyNoMotion()
{
    Assert.DoesNotThrow(() => PerformanceMotion.Apply(null, ActionId.Hands, 1f));
}
```

- [ ] **Step 2: Run and confirm red** (`CS0103 PerformanceMotion`)

- [ ] **Step 3: Implement**

Her spine `0.8f * Sin(2π*0.11*t)` on X; head `1.2f * Sin(2π*0.07*t + 0.9f)` on X and `0.6f * Sin(2π*0.05*t + 2.1f)` on Z; hands `2f * Mathf.Max(0f, Sin(2π*t/3.7f))**2` on Z (a squared sine: it peaks every 3.7 s and never jumps); his spine `0.5f * Sin(2π*0.16*t)`.

Apply inside each `Drive*`, immediately **after** its `ActionPose.Apply(...)` and **before** the IK solve (otherwise the table rebuilds Chest/Spine and wipes the breath), and only while `_phase == ActionPhase.Hold`: `PerformanceMotion.Apply(Actor.Rig, _current, PhaseTime);` plus, when `Body` exists, one call for his rig that adds `HisSpine(PhaseTime)` to his `Spine`. Add the same call to `DriveHands`, `DrivePatHer`, `DrivePatMe`, `DriveKiss` and `DriveSitTogether`.

- [ ] **Step 4: Run green**, then the whole `Action` filter.

- [ ] **Step 5: Stage, do not commit.**

---

### Task 5: Verification, marker, and the spec record

**Files:**
- Modify: `apps/unity/Assets/Scripts/Boot/PlayerSelfTest.cs` (after the token marker, ~line 101), `tools/player-smoke.ps1:231-252` (`$wants`), `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md` (append §5.29), `docs/superpowers/specs/2026-11-08-action-performances-design.md` (§9 tick S1–S3)
- Create: `apps/unity/Assets/Tests/PlayMode/ActionPerformanceShotTests.cs` (`[Explicit]`)

**Interfaces:**
- Produces: `public static string PlayerSelfTest.StageSummary(ActionDirector d)` → `stage ok enter={Enter:F3} hold={Held:F3} leave={Leave:F3} clip=procedural`; smoke pattern `\[selftest\] stage ok enter=2\.[0-9]{3} hold=[0-9]+\.[0-9]{3} leave=1\.[0-9]{3} clip=procedural`.

- [ ] **Step 1: Write the failing test**

`ActionPerformanceShotTests`: drive hands to Hold, capture `Shots/stage-a.png`; after `Hold(d, 2f)` capture `stage-b.png`; `RequestLeave` and, at 60% of the leave, capture `stage-c.png`. Assert pairwise mean pixel difference > 3% (read the RenderTexture the same way `PlayerFacingShotTests` does) and write `Shots/hands-phases.png` as a 3-up strip. Expected red: the two hold frames are identical, so b-vs-a is 0%.

- [ ] **Step 2: Run red** (`--filter Mas3D.Tests.ActionPerformanceShotTests`), then implement the marker and rerun.

- [ ] **Step 3: Marker and smoke line**

In `PlayerSelfTest`, right after the hands-action block has reached its hold and before `Cancel()`, add `Mark(StageSummary(actionDirector));`. `StageSummary` formats with `CultureInfo.InvariantCulture` and reports `clip=procedural` in this plan. Add the smoke line above to `$wants` after `action hands`; update the "five marker lines" comment to seven.

- [ ] **Step 4: Mutation red**

Temporarily replace the Enter branch in `Tick` with `_phase = ActionPhase.Hold; Player.Teleport(_path.To, _path.ToYaw);`; run `tools/player-smoke.ps1` and expect `HandsEntryWalksThePlayerInInsteadOfTeleporting` red, and `marker missing lines: stage ok` once the mutated build runs. Restore the branch and re-run green.

- [ ] **Step 5: Six gates**

`Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & D:\Mas3D\tools\check.ps1 -Python D:\Python\python.exe` → expect `summary: 6/6 ok` (about 3–4 min).

- [ ] **Step 6: Write §5.29 and tick the spec's S1–S3**, preserving the measured numbers (durations, knee angles, the 3% pixel deltas) and the two limitations this plan keeps: no walk-cycle animation (the player glides), and the leave plays in place then snaps home on the last frame.

- [ ] **Step 7: Stage, do not commit.**

---

## Self-review

- Spec coverage: §5.1 → Task 1; §5.3/§5.6/§5.8 → Task 2; player legs → Task 3; §5.5 → Task 4; §5.9 and judgments 1, 1b, 2, 3, 4, 5, 8, 9, 10 → Tasks 1–5. §5.4 and judgments 6–7 are Plan 2 by design.
- Step scan: every step names a file and a checkable result; no "handle edge cases" lines.
- Types: `ActionPhase`, `PerformanceTiming`, `PlayerPath`, `PlayerLegs`, `BodyRig` are introduced once and reused under the same names.
- Review Focus: five lines, each pinned to Task 2 (1–2), Task 4 (3), Task 3 (4–5).
- Proportion: decisions and assertions only; the two short algorithms the tests cannot determine are given as code.
