# Action Performances, Plan 2: The Clip Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hands action's programmatic entry and exit with generated motion baked into committed JSON tracks, keep the procedural performance as the fallback, and prove from the built player that the clips shipped.

**Architecture:** The editor-side retargeter that already turns a Kimodo clip into her pose is extracted into a shared `MotionRetarget`; `MotionBaker` runs it once per frame and serializes 20 bones of local rotations into two `TextAsset` JSON tracks; at runtime a `MotionTrack` samples those tracks between the pose table and the IK layer. Nothing in the shipped player parses glTF, VRM or clips at runtime.

**Tech Stack:** Unity 6000.6.7f1, C#, editor scripts under `apps/unity/Assets/Editor`, UnityEngine.JsonUtility, NUnit EditMode + PlayMode tests.

**Spec:** `docs/superpowers/specs/2026-11-08-action-performances-design.md` (§5.4, §5.9, §6 judgments 6–7, §7)

## Global Constraints

- Do **not** run `git commit` or `git push`; the Lead collects the diff and YG decides the commit.
- Plan 1 must already be in the worktree: `ActionPhase`, `PerformanceTiming`, `PlayerPath`, `PlayerLegs`, `PerformanceMotion` and the `stage ok` marker all exist.
- The clip channel never uses `AnimationClip` or `Animator`; it is a `TextAsset` read once at Awake.
- Asset budgets: each track 200–400 KB, 20 bones, quaternions as four floats, 30 fps; `hands-enter.json` 78 frames (2.6 s), `hands-leave.json` 48 frames (1.6 s, the source's last 48 frames reversed).
- The baked tracks are committed; the 254 KB source `reach42.json` is committed under `apps/unity/Motion/source/` because `Assets/_Local/` is gitignored.
- No runtime glTF/VRM loading: the shipped player reads only the two TextAssets the scene references.
- Missing or corrupt data is never fatal: the action plays procedurally and says so in the log and the marker.
- ASCII-only C# in new files; JSON assets are UTF-8 without BOM.

## Review Focus

1. **A clip that fails to parse** — the player must still get the full three-phase performance, with `clip=procedural` in the log, not a half-posed body.
2. **Frame 0 of the entry versus her rest posture** — the first sampled frame must not snap the shoulders; the weight ramp must start at the pose table's pose.
3. **The leave reversing** — playing the last 48 frames backwards must end where the entry began, or the teardown snap becomes visible.
4. **A rebuild on a machine without `Assets/_Local/`** — the baked JSON is committed, so baking is not required to build, run or test.
5. **Scene reference loss** — `BuildScene` must re-assign the two TextAssets every rebuild, or a scene regeneration silently drops the clips.

---

### Task 1: One retargeter, two callers

**Files:**
- Create: `apps/unity/Assets/Editor/MotionRetarget.cs`
- Create: `apps/unity/Assets/Editor/MotionClip.cs` (the clip loader, moved out of `CliMotion`)
- Modify: `apps/unity/Assets/Editor/CliMotion.cs` (delete the moved members, call `MotionRetarget.PoseTo`)
- Test: `apps/unity/Assets/Tests/EditMode/MotionRetargetTests.cs`

**Interfaces:**
- Consumes: the existing private members of `CliMotion`: `Clip`/`CliClip`, `Load(string)`, `PoseTo(AvatarRig, int)` at `CliMotion.cs:547-687`, plus `Mapping`, `Order`, `ChildOf`, `Segments`, `SpineChain`, `SpineBones`, `IsSpineBone`, `IsLegBone`, `AlongPolyline`, `Rotate`, `actorYaw`, `CaptureSpineFractions`, and fields `_spineFix`, `_spineAxis`, `_spineUp`, `_lastTargets`, `_spineFractions`.
- Produces:
  - `public sealed class MotionClip` with `public float Fps; public string[] Joints; public int FrameCount; public bool Has(string joint); public Vector3 At(int frame, string joint); public static MotionClip Load(string path); public static MotionClip Parse(string json);`
  - `public struct RetargetSettings { public bool SpineFix; public float SpineAxis; public float SpineUp; public bool UseLegs; }`
  - `public static class MotionRetarget { public static void CaptureSpineFractions(AvatarRig rig, out float[] fractions); public static void PoseTo(AvatarRig rig, MotionClip clip, int frame, RetargetSettings settings, float[] spineFractions); }`

- [ ] **Step 1: Capture the baseline before touching anything**

Run twice, from the same camera:
`Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & D:\Mas3D\tools\unity.ps1 run -- -executeMethod Mas3D.Editor.CliMotion.Capture -times '1.2' -shot refactor-before -join 1 -bake 1 -exposure 0.5 -eye '-0.10,1.35,2.35' -aim '-0.28,1.15,-0.12' -fov 36`
Then hash `apps/unity/Shots/refactor-before-036.png` and save the hash; that PNG is the acceptance oracle.

- [ ] **Step 2: Write the failing test**

`MotionRetargetTests.cs`: load a small fixture clip committed at `apps/unity/Assets/Tests/EditMode/Fixtures/mini-clip.json` (3 joints `Hips/Spine1/Head`, 2 frames, written by hand from the schema: `{"fps":30,"joints":["Hips","Spine1","Head"],"frameCount":2,"jointCount":3,"positions":[0,0.5,0, 0,0.6,0, 0,0.8,0, 0,0.5,0, 0,0.6,0.05, 0,0.8,0.1]}`). Assertions: `MotionClip.Parse(json).FrameCount == 2`, `Has("Spine1")` true, `At(1,"Head").z == 0.1f` within 1e-4, and `At(0, "Nope")` returns `Vector3.zero` rather than throwing.

- [ ] **Step 3: Run it red** — `CS0246 MotionClip`.

- [ ] **Step 4: Move, do not rewrite**

Move the members listed above verbatim into the two new files; `CliMotion` keeps its statics and becomes:

```csharp
static void PoseTo(AvatarRig rig, int frame)
{
    if (_clip == null) return;
    MotionRetarget.PoseTo(rig, _clip, frame,
        new RetargetSettings { SpineFix = _spineFix, SpineAxis = _spineAxis, SpineUp = _spineUp, UseLegs = _useLegs },
        _spineFractions);
}
```

`_lastTargets` (used by the hand IK after the pose) becomes an output: add `public static Vector3 LastTarget(HumanBodyBones bone)` to `MotionRetarget` backed by the same dictionary, and have `CliMotion` read it.

- [ ] **Step 5: Re-render and compare**

Run the Step 1 command with `-shot refactor-after` and compare the SHA256 of `refactor-after-036.png` against the saved hash. Expect **identical**. If it differs, the move was not verbatim; diff the moved members against `git diff -U3 apps/unity/Assets/Editor/CliMotion.cs` before changing any math.

- [ ] **Step 6: Stage, do not commit.**

---

### Task 2: Bake the two tracks

**Files:**
- Create: `apps/unity/Assets/Editor/MotionBaker.cs`
- Create (generated): `apps/unity/Assets/Motion/hands-enter.json`, `apps/unity/Assets/Motion/hands-leave.json`, `apps/unity/Motion/source/reach42.json`
- Test: `apps/unity/Assets/Tests/EditMode/MotionBakerTests.cs`

**Interfaces:**
- Consumes: `MotionClip.Load`, `MotionRetarget.PoseTo`, `MotionRetarget.CaptureSpineFractions`, `AvatarRig`, `AvatarActor`, `PlayerMotor`; the source clip at `apps/unity/Assets/_Local/motion/reach42.json`.
- Produces:
  - `public static class MotionBaker { public const string SourcePath = "Assets/_Local/motion/reach42.json"; public const string EnterPath = "Assets/Motion/hands-enter.json"; public const string LeavePath = "Assets/Motion/hands-leave.json"; public static void BakeHands(); }`
  - Track schema: `{"fps":30,"frameCount":N,"bones":["Hips",…],"keys":[x,y,z,w,…]}` — `keys` is `frameCount * bones.Length * 4` floats, frames major.

- [ ] **Step 1: Write the failing EditMode test**

```csharp
[Test] public void TheBakedTracksCarryTwentyBones()
{
    var enter = AssetDatabase.LoadAssetAtPath<TextAsset>(MotionBaker.EnterPath);
    var leave = AssetDatabase.LoadAssetAtPath<TextAsset>(MotionBaker.LeavePath);
    Assert.That(enter, Is.Not.Null, MotionBaker.EnterPath + " missing: run MotionBaker.BakeHands");
    Assert.That(leave, Is.Not.Null, MotionBaker.LeavePath + " missing");
    var e = JsonUtility.FromJson<TrackShape>(enter.text);
    Assert.That(e.bones.Length, Is.EqualTo(20));
    Assert.That(e.frameCount, Is.EqualTo(78));
    Assert.That(e.keys.Length, Is.EqualTo(78 * 20 * 4));
    Assert.That(JsonUtility.FromJson<TrackShape>(leave.text).frameCount, Is.EqualTo(48));
}
```

`TrackShape` is a private `[Serializable]` class in the test file.

- [ ] **Step 2: Run red** (files missing).

- [ ] **Step 3: Implement the baker**

`BakeHands` opens a fresh scene, instantiates her prefab (`Assets/_Local/avatars/Monkia.vrm` via `AssetDatabase.LoadAssetAtPath<GameObject>`), seats her with `AvatarActor.SitAt(AvatarActor.DefaultSeatId)`, captures spine fractions and the head rest, then for each frame calls `MotionRetarget.PoseTo` and records `rig.Bone(bone).localRotation` for the 20 bones of the track. Fix the bone list once:

```csharp
static readonly HumanBodyBones[] Bones =
{
    Hips, Spine, Chest, UpperChest, Neck, Head,
    LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand,
    RightShoulder, RightUpperArm, RightLowerArm, RightHand,
    LeftUpperLeg, LeftLowerLeg, LeftFoot, RightUpperLeg, RightLowerLeg, RightFoot,
};
```

Enter = source frames 0..77. Leave = frames 119..72 reversed (48 frames). Write both with `JsonUtility.ToJson` and `File.WriteAllText(path, json, new UTF8Encoding(false))`; copy the source with `File.Copy` into `apps/unity/Motion/source/reach42.json` (create the folder with `Directory.CreateDirectory`). Log `[baker] enter=78 frames 20 bones bytes=N`. Add `public static void BakeHandsCli()` that calls `BakeHands()` inside a try/catch and exits `EditorApplication.Exit(0)` on success, `1` on exception — that is the entry point the CLI calls.

- [ ] **Step 4: Bake and run green**

`Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & D:\Mas3D\tools\unity.ps1 run -- -executeMethod Mas3D.Editor.MotionBaker.BakeHands`
then the EditMode filter `Mas3D.Tests.EditMode.MotionBakerTests`; expect 1 passed and both files between 200 KB and 400 KB (log the byte counts).

- [ ] **Step 5: Pin the track to bone space**

Add an EditMode assertion: the first enter frame's `Hips` quaternion equals her seated `Hips` localRotation within 2°. That proves the track stores local rotations in her skeleton's space, not the clip's world space.

- [ ] **Step 6: Stage, do not commit** (include the generated JSON and the source copy).

---

### Task 3: The runtime track

**Files:**
- Create: `apps/unity/Assets/Scripts/Actions/MotionTrack.cs`
- Test: `apps/unity/Assets/Tests/PlayMode/MotionTrackTests.cs`

**Interfaces:**
- Produces: `public sealed class MotionTrack` — a plain class, not a `MonoBehaviour` (constructible in tests, no scene wiring):
  - `public MotionTrack(TextAsset data, bool reverse)`
  - `public bool Ready { get; }`, `public int FrameCount { get; }`, `public float Duration { get; }`
  - `public void Sample(float t)` — clamps, picks two frames, `Quaternion.Slerp` into a reused buffer.
  - `public bool TryGet(HumanBodyBones bone, out Quaternion local)`
  - `public void Apply(AvatarRig rig, float weight)` — `Slerp(current, sampled, weight)`, then records the bone in its own `Claimed` set.
  - `public static bool Claims(Bone)` is per-instance: `public bool Claims(HumanBodyBones bone)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void TheSameTimeSamplesTheSamePose()
{
    var track = new MotionTrack(EnterAsset(), false);
    Assert.That(track.Ready, Is.True);
    track.Sample(0.75f); track.TryGet(HumanBodyBones.Head, out var a);
    track.Sample(0.75f); track.TryGet(HumanBodyBones.Head, out var b);
    Assert.That(Quaternion.Angle(a, b), Is.LessThan(1e-3f));
}

[Test] public void SamplingClampsAtBothEnds()
{
    var track = new MotionTrack(EnterAsset(), false);
    track.Sample(-5f); track.TryGet(HumanBodyBones.Hips, out var first);
    track.Sample(0f);  track.TryGet(HumanBodyBones.Hips, out var zero);
    Assert.That(Quaternion.Angle(first, zero), Is.LessThan(1e-3f));
    track.Sample(999f); track.TryGet(HumanBodyBones.Hips, out var last);
    track.Sample(track.Duration); track.TryGet(HumanBodyBones.Hips, out var end);
    Assert.That(Quaternion.Angle(last, end), Is.LessThan(1e-3f));
}

[Test] public void NoDataIsNotReadyAndNotFatal()
{
    var track = new MotionTrack(null, false);
    Assert.That(track.Ready, Is.False);
    Assert.DoesNotThrow(() => { track.Sample(1f); track.Apply(null, 1f); });
}
```

`EnterAsset()` loads `Assets/Motion/hands-enter.json` via `AssetDatabase`, so these run in PlayMode but still under the editor.

- [ ] **Step 2: Run red** (`CS0246`).

- [ ] **Step 3: Implement**

Parse once in the constructor with `JsonUtility.FromJson<Track>` (private `[Serializable]` shape with `fps/frameCount/bones/keys`). `Sample`: `var f = Mathf.Clamp(t, 0f, Duration) * fps; var i = Mathf.Min((int)f, FrameCount - 2); var u = f - i;` then per bone `Slerp` between `keys[i]` and `keys[i+1]` — with `Reverse`, index from the end instead. `Apply` never touches a bone missing from `bones`, and clears `Claimed` at the start of each apply.

- [ ] **Step 4: Run green**, then the whole PlayMode `Action` filter (nothing else should notice the new class).

- [ ] **Step 5: Stage, do not commit.**

---

### Task 4: Wire it into the hands performance

**Files:**
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs` (two data fields, track lifetime, the layer between the pose table and the IK, the fallback log), `apps/unity/Assets/Scripts/RoomBootstrap.cs:141-158` (pass the assets), `apps/unity/Assets/Editor/CliPlayer.cs` (assign them when rebuilding the scene), `apps/unity/Assets/Scripts/Boot/PlayerSelfTest.cs` (marker), `tools/player-smoke.ps1` (`clip=clip`)
- Create: `apps/unity/Assets/Tests/PlayMode/ActionClipTests.cs`

**Interfaces:**
- Consumes: `MotionTrack`, Task 1's retargeter is not needed at runtime.
- Produces:
  - `public TextAsset HandsEnterData; public TextAsset HandsLeaveData;` on `ActionDirector` and on `RoomBootstrap`.
  - `public bool ActionDirector.ClipActive`, `public string ActionDirector.ClipSource => ClipActive ? "clip" : "procedural"`.
  - The layer order in `Drive*`: pose table → `_enterTrack.Apply(rig, Phase == Enter ? 1f : 0f)` (or `_leaveTrack` in Leave) → `PerformanceMotion` → IK.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void HandsUsesTheClipWhenItIsWired()
{
    var d = HandsDirector();
    d.HandsEnterData = EnterAsset();
    d.HandsLeaveData = LeaveAsset();
    d.TryEnterAction(ActionId.Hands);
    Assert.That(d.ClipActive, Is.True);
    Assert.That(d.ClipSource, Is.EqualTo("clip"));
    ActionTestDrive.Hold(d, 0.1f);
    var chest = d.Actor.Rig.Bone(HumanBodyBones.Chest).localRotation;
    ActionTestDrive.Hold(d, 0.5f);
    Assert.That(Quaternion.Angle(chest, d.Actor.Rig.Bone(HumanBodyBones.Chest).localRotation),
        Is.GreaterThan(0.2f), "the track is moving her torso");
}

[Test] public void MissingClipFallsBackToThePoseTable()
{
    var d = HandsDirector();                 // no data assigned
    d.TryEnterAction(ActionId.Hands);
    Assert.That(d.ClipActive, Is.False);
    Assert.That(d.ClipSource, Is.EqualTo("procedural"));
    ActionTestDrive.ToHold(d);
    Assert.That(d.ContactError, Is.LessThanOrEqualTo(ActionDirector.ContactTolerance));
    LogAssert.Expect(LogType.Log, new Regex(@"\[stage\] hands enter=procedural"));
}
```

- [ ] **Step 2: Run red** (`CS1061 ClipActive`).

- [ ] **Step 3: Implement**

Build the tracks lazily in `TryEnterAction`: `_enterTrack = new MotionTrack(HandsEnterData, false); _leaveTrack = new MotionTrack(HandsLeaveData, true);` dispatch on `_current == ActionId.Hands` and `_phase`, and log once per performance: `[stage] hands enter={0} exit={1}` with `clip`/`procedural`. Claim her bones for `AvatarIdle` by extending the existing claim delegate: `AvatarIdle.BoneClaim = b => ActionPose.Claims(b) || (_enterTrack != null && _enterTrack.Claims(b)) || (_leaveTrack != null && _leaveTrack.Claims(b));` restored to `ActionPose.Claims` in `Cancel`.

In `RoomBootstrap`, after the `Actions` block: `Actions.HandsEnterData = HandsEnterData; Actions.HandsLeaveData = HandsLeaveData;`. In `CliPlayer.BuildScene` (after `BuildPlayer`), find the `RoomBootstrap` and assign both with `AssetDatabase.LoadAssetAtPath<TextAsset>`, then save the scene — this is what keeps the reference across regenerations.

Marker: `StageSummary` appends `clip={0}` with `d.ClipSource`; the smoke line changes to `clip=clip` and stays in `$wants`.

- [ ] **Step 4: Rebuild the scene and the player**

`& D:\Mas3D\tools\unity.ps1 player` then `& D:\Mas3D\tools\unity.ps1 build -Out build/Mas3D.exe` then `& D:\Mas3D\tools\player-smoke.ps1`; expect the marker line `stage ok … clip=clip` and all other lines.

- [ ] **Step 5: Mutation red**

Delete (or null) `hands-leave.json` only, rebuild, and expect the smoke to fail on `marker missing lines: stage ok` with the fallback line in the log; restore and re-run green.

- [ ] **Step 6: Stage, do not commit.**

---

### Task 5: Verification and the spec record

**Files:**
- Modify: `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md` (append §5.30), `docs/superpowers/specs/2026-11-08-action-performances-design.md` (§9 tick S4–S5, §8 note that the clip channel is procedural-fallback)
- Create: nothing

- [ ] **Step 1: Whole suite** — PlayMode filter `Mas3D.Tests.PlayMode` and EditMode filter `Mas3D.Tests.EditMode`; expect the same counts as before plus the new tests, 0 failed.

- [ ] **Step 2: Six gates** — `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & D:\Mas3D\tools\check.ps1 -Python D:\Python\python.exe` → `summary: 6/6 ok` in about 3–4 minutes.

- [ ] **Step 3: Size and determinism check** — record the two track byte counts and re-bake once; the files must be byte-identical (deterministic retarget), otherwise log the diff and stop.

- [ ] **Step 4: Write §5.30 and tick S4–S5**, including: the measured byte counts, the 20-bone list, the 78/48 frame split, the fallback evidence, and the one thing that is still procedural (his body has no clip).

- [ ] **Step 5: Stage, do not commit.**

---

## Self-review

- Spec coverage: §5.4 → Tasks 1–4; judgment 6 → Task 4; judgment 7 → Task 3; judgment 10's `clip=clip` → Task 4; §7 asset policy → Tasks 2 and 5. S1–S3 live in Plan 1.
- Step scan: each step names the file and the observable result; the one implementation detail left open (the `Reverse` indexing) is bounded by two clamp tests.
- Types: `MotionClip`, `RetargetSettings`, `MotionRetarget`, `MotionTrack`, `ClipActive`, `ClipSource` are defined once and reused unchanged.
- Review Focus: five lines pinned to Task 4 (1, 5), Task 2 (4), Task 3 (2), and Task 2/3 (3).
- Proportion: signatures and assertions; the only verbatim block is the 20-bone list, which is a decision, not code.
