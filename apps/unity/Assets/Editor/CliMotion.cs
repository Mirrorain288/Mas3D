using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using Mas3D.Avatar;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Mas3D.Editor
{
    /// <summary>
    /// Spike only: drives Monika from a Kimodo clip by aiming every mapped bone at its child's target
    /// position, so the VRM rig's own bone lengths decide the pose instead of a cross-skeleton rotation
    /// transfer. Not production code; nothing here is wired into the runtime.
    /// </summary>
    public static class CliMotion
    {
        public const string DefaultJson = "Assets/_Local/motion/hands.json";

        [Serializable]
        public class Clip
        {
            public float fps;
            public string[] joints;
            public int frameCount;
            public int jointCount;
            public float[] positions;
        }

        /// <summary>SOMA joint name to the Unity humanoid bone that should follow it.</summary>

        /// <summary>
        /// Fingers are worth driving: a spread T-pose hand reads as a mannequin even when the arm is right.
        /// SOMA names them LeftHandIndex1..4 (4 = tip); Unity has Proximal/Intermediate/Distal only, so the
        /// fourth joint is dropped and the chain stops at the distal bone.
        /// </summary>
        static void AddFingerChains()
        {
            var fingers = new[] { "Thumb", "Index", "Middle", "Ring", "Little" };
            var unity = new[] { "Thumb", "Index", "Middle", "Ring", "Little" };
            var segments = new[] { "Proximal", "Intermediate", "Distal" };
            foreach (var side in new[] { "Left", "Right" })
            {
                for (var f = 0; f < fingers.Length; f++)
                {
                    var count = fingers[f] == "Thumb" ? 3 : 4;
                    for (var s = 0; s < Mathf.Min(count, 3); s++)
                    {
                        var soma = side + "Hand" + fingers[f] + (s + 1);
                        var bone = side + unity[f] + segments[s];
                        if (Enum.TryParse(bone, out HumanBodyBones parsed)) Mapping.Add((soma, parsed));
                    }
                }

            }

            foreach (var side in new[] { "Left", "Right" })
            {
                ChildOf[(HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + "Hand")] =
                    (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + "MiddleProximal");
                ChildOf[(HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + "MiddleProximal")] =
                    (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + "MiddleIntermediate");
                ChildOf[(HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + "MiddleIntermediate")] =
                    (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + "MiddleDistal");
                foreach (var finger in new[] { "Thumb", "Index", "Ring", "Little" })
                {
                    ChildOf[(HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + finger + "Proximal")] =
                        (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + finger + "Intermediate");
                    ChildOf[(HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + finger + "Intermediate")] =
                        (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + finger + "Distal");
                }
            }
        }

        static readonly List<(string Soma, HumanBodyBones Bone)> Mapping = new List<(string Soma, HumanBodyBones Bone)>
        {
            ("Hips", HumanBodyBones.Hips),
            ("Spine1", HumanBodyBones.Spine),
            ("Spine2", HumanBodyBones.Chest),
            ("Chest", HumanBodyBones.UpperChest),
            ("Neck2", HumanBodyBones.Neck),
            ("Head", HumanBodyBones.Head),
            ("LeftShoulder", HumanBodyBones.LeftShoulder),
            ("LeftArm", HumanBodyBones.LeftUpperArm),
            ("LeftForeArm", HumanBodyBones.LeftLowerArm),
            ("LeftHand", HumanBodyBones.LeftHand),
            ("RightShoulder", HumanBodyBones.RightShoulder),
            ("RightArm", HumanBodyBones.RightUpperArm),
            ("RightForeArm", HumanBodyBones.RightLowerArm),
            ("RightHand", HumanBodyBones.RightHand),
            ("LeftLeg", HumanBodyBones.LeftUpperLeg),
            ("LeftShin", HumanBodyBones.LeftLowerLeg),
            ("LeftFoot", HumanBodyBones.LeftFoot),
            ("LeftToeBase", HumanBodyBones.LeftToes),
            ("RightLeg", HumanBodyBones.RightUpperLeg),
            ("RightShin", HumanBodyBones.RightLowerLeg),
            ("RightFoot", HumanBodyBones.RightFoot),
            ("RightToeBase", HumanBodyBones.RightToes),
        };

        /// <summary>
        /// Which clip segment each rig bone copies, parent before child. The segments are adjacent joints in
        /// the clip, which is the whole point: the rig has fewer spine bones than the clip, so copying the
        /// direction of a multi-joint span makes the chest bite off far more lean than the clip has.
        /// </summary>
        static readonly (HumanBodyBones Bone, string From, string To)[] Segments =
        {
            (HumanBodyBones.Hips, "Hips", "Spine1"),
            (HumanBodyBones.Spine, "Spine1", "Spine2"),
            (HumanBodyBones.Chest, "Spine2", "Chest"),
            (HumanBodyBones.UpperChest, "Chest", "Neck1"),
            (HumanBodyBones.LeftShoulder, "LeftShoulder", "LeftArm"),
            (HumanBodyBones.LeftUpperArm, "LeftArm", "LeftForeArm"),
            (HumanBodyBones.LeftLowerArm, "LeftForeArm", "LeftHand"),
            (HumanBodyBones.RightShoulder, "RightShoulder", "RightArm"),
            (HumanBodyBones.RightUpperArm, "RightArm", "RightForeArm"),
            (HumanBodyBones.RightLowerArm, "RightForeArm", "RightHand"),
            (HumanBodyBones.LeftUpperLeg, "LeftLeg", "LeftShin"),
            (HumanBodyBones.LeftLowerLeg, "LeftShin", "LeftFoot"),
            (HumanBodyBones.LeftFoot, "LeftFoot", "LeftToeBase"),
            (HumanBodyBones.RightUpperLeg, "RightLeg", "RightShin"),
            (HumanBodyBones.RightLowerLeg, "RightShin", "RightFoot"),
            (HumanBodyBones.RightFoot, "RightFoot", "RightToeBase"),
        };

        /// <summary>Parents before children: one pass, each aim recomputes the child from the moved parent.</summary>
        static readonly List<HumanBodyBones> Order = new List<HumanBodyBones>
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
            HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
        };

        static readonly Dictionary<HumanBodyBones, HumanBodyBones> ChildOf = new Dictionary<HumanBodyBones, HumanBodyBones>
        {
            { HumanBodyBones.Hips, HumanBodyBones.Spine },
            { HumanBodyBones.Spine, HumanBodyBones.Chest },
            { HumanBodyBones.Chest, HumanBodyBones.UpperChest },
            { HumanBodyBones.UpperChest, HumanBodyBones.Neck },
            { HumanBodyBones.Neck, HumanBodyBones.Head },
            { HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm },
            { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm },
            { HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand },
            { HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm },
            { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm },
            { HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand },
            { HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg },
            { HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot },
            { HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes },
            { HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg },
            { HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot },
            { HumanBodyBones.RightFoot, HumanBodyBones.RightToes },
        };

        static CliMotion()
        {
            AddFingerChains();
            foreach (var side in new[] { "Left", "Right" })
            {
                var prefix = side == "Left" ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal;
                var baseIndex = (int)prefix;
                // Fingers are laid out contiguously per side in HumanBodyBones: 5 fingers x 3 joints.
                for (var i = 0; i < 15; i++) Order.Add((HumanBodyBones)(baseIndex + i));
            }
        }

        static CliClip _clip;
        static Dictionary<HumanBodyBones, Vector3> _lastTargets = new Dictionary<HumanBodyBones, Vector3>();
        static bool _dumpPose;
        static Vector3? _eye;
        static Vector3? _aim;
        static bool _hideHim;
        static bool _ghost;
        static bool _poseError;

        /// <summary>Distance between each mapped rig bone and where the clip puts that joint, in metres.</summary>
        static void ReportPoseError(AvatarRig rig, int frame)
        {
            var hips = rig.Bone(HumanBodyBones.Hips);
            if (hips == null) return;
            var yaw = actorYaw(hips);
            var clipHips = _clip.At(frame, "Hips");
            float max = 0f, sum = 0f;
            var n = 0;
            var line = new System.Text.StringBuilder();
            foreach (var (soma, bone) in Mapping)
            {
                var t = rig.Bone(bone);
                if (t == null || !_clip.Has(soma)) continue;
                var v = _clip.At(frame, soma) - clipHips;
                var want = hips.position + Rotate(new Vector3(-v.x, v.y, v.z), yaw);
                var d = Vector3.Distance(t.position, want);
                max = Mathf.Max(max, d);
                sum += d * d;
                n++;
                line.Append($" {soma}={d:0.000}");
            }
            var rms = n > 0 ? Mathf.Sqrt(sum / n) : 0f;
            Debug.Log($"[pose] f{frame} max={max:0.000} rms={rms:0.000}{line}");
        }
        static readonly List<GameObject> _ghosts = new List<GameObject>();

        /// <summary>Drops a marker on every clip joint so the render can be checked against the source pose.</summary>
        static void DrawGhostCloud(AvatarRig rig, Func<string, Vector3> point)
        {
            foreach (var go in _ghosts) Object.DestroyImmediate(go);
            _ghosts.Clear();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", new Color(0.1f, 1f, 0.2f));
            mat.SetFloat("_Cull", 0f);
            for (var i = 0; i < _clip.Joints.Length; i++)
            {
                var p = point(_clip.Joints[i]);
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.name = "ghost";
                Object.DestroyImmediate(s.GetComponent<Collider>());
                s.transform.position = p;
                s.transform.localScale = Vector3.one * 0.035f;
                s.GetComponent<MeshRenderer>().sharedMaterial = mat;
                _ghosts.Add(s);
            }
        }

        static Vector3? ParseVec(string[] args, string key)
        {
            var raw = Arg(args, key, null);
            if (string.IsNullOrEmpty(raw)) return null;
            var parts = raw.Split(',');
            if (parts.Length != 3) return null;
            var f = new float[3];
            for (var i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return null;
            return new Vector3(f[0], f[1], f[2]);
        }

        static readonly HumanBodyBones[] ProbeBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
            HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        static Dictionary<HumanBodyBones, Vector3> _restPositions = new Dictionary<HumanBodyBones, Vector3>();
        static Quaternion _restHeadRot = Quaternion.identity;
        static Vector3 _restHeadFace = Vector3.forward;

        /// <summary>The head's resting world rotation and gaze, taken from the seated pose before any clip runs.</summary>
        static void CaptureHeadRest(AvatarRig rig)
        {
            var head = rig.Bone(HumanBodyBones.Head);
            if (head == null) return;
            _restHeadRot = head.rotation;
            _restHeadFace = head.rotation * Vector3.forward;
        }

        /// <summary>Snapshot before any limb is posed, so the dump can show what moved.</summary>
        static void CaptureRest(AvatarRig rig)
        {
            _restPositions.Clear();
            foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (b == HumanBodyBones.LastBone) continue;
                var t = rig.Bone(b);
                if (t != null) _restPositions[b] = t.position;
            }
        }

        /// <summary>
        /// The four spine bones, and the clip's own spine joints they borrow their shape from. The clip is
        /// SOMA: its lumbar joints are three very short links that lean backwards by 30-odd degrees, then a
        /// single long, almost upright chest-to-neck1 link carries the ribcage. Copying those directions
        /// joint-for-joint hands the whole VRM waist the extreme lean of the short links and she arches her
        /// back, so the spine is solved from the polyline instead (see PoseTo).
        /// </summary>
        static readonly HumanBodyBones[] SpineBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        };

        static readonly string[] SpineChain = { "Hips", "Spine1", "Spine2", "Chest", "Neck1" };

        static float[] _spineFractions;

        /// <summary>
        /// Where each spine bone starts and ends as a fraction of the whole hip-to-neck run, measured on the
        /// rig at rest. Those fractions are what map a rig bone onto a slice of the clip's spine polyline:
        /// without them the two skeletons' different proportions are what makes the retarget look wrong.
        /// </summary>
        static void CaptureSpineFractions(AvatarRig rig)
        {
            var hips = rig.Bone(HumanBodyBones.Hips);
            var neck = rig.Bone(HumanBodyBones.Neck);
            if (hips == null || neck == null) return;

            var cum = new float[SpineBones.Length + 1];
            var prev = hips;
            for (var i = 1; i < SpineBones.Length; i++)
            {
                var bone = rig.Bone(SpineBones[i]);
                if (bone == null) return;
                cum[i] = cum[i - 1] + Vector3.Distance(prev.position, bone.position);
                prev = bone;
            }
            cum[SpineBones.Length] = cum[SpineBones.Length - 1] + Vector3.Distance(prev.position, neck.position);
            var total = cum[cum.Length - 1];
            if (total < 1e-4f) return;

            var frac = new float[cum.Length];
            for (var i = 0; i < cum.Length; i++) frac[i] = cum[i] / total;
            _spineFractions = frac;
            Debug.Log("[spine] rest spans m=" + string.Join(",", cum.Select(c => c.ToString("0.000", CultureInfo.InvariantCulture)).ToArray())
                + " fractions=" + string.Join(",", frac.Select(f => f.ToString("0.000", CultureInfo.InvariantCulture)).ToArray()));
        }

        /// <summary>Point at a fraction of the way along a polyline, by accumulated length.</summary>
        static Vector3 AlongPolyline(Vector3[] points, float[] lengths, float total, float fraction)
        {
            var target = Mathf.Clamp01(fraction) * total;
            for (var i = 0; i < lengths.Length; i++)
            {
                if (target <= lengths[i] || i == lengths.Length - 1)
                {
                    var t = lengths[i] < 1e-6f ? 0f : Mathf.Clamp01(target / lengths[i]);
                    return Vector3.Lerp(points[i], points[i + 1], t);
                }
                target -= lengths[i];
            }
            return points[points.Length - 1];
        }

        /// <summary>Compares where each mapped bone ended up against the clip target it was aimed at.</summary>
        static void DumpPose(AvatarRig rig)
        {
            var hips = rig.Bone(HumanBodyBones.Hips);
            var headNow = rig.Bone(HumanBodyBones.Head);
            var faceNow = headNow == null ? Vector3.zero : headNow.rotation * Vector3.forward;
            Debug.Log("[pose] hips=" + Fmt(hips.position) + " scale=" + Fmt(rig.Root.lossyScale)
                + " targets=" + _lastTargets.Count + " headFace=" + Fmt(faceNow));
            foreach (var bone in ProbeBones)
            {
                var t = rig.Bone(bone);
                if (t == null) continue;
                var actual = t.position;
                var wanted = _lastTargets.TryGetValue(bone, out var w) ? w : new Vector3(float.NaN, 0, 0);
                var delta = float.IsNaN(wanted.x) ? "n/a" : (actual - wanted).magnitude.ToString("0.000", CultureInfo.InvariantCulture);
                var rest = _restPositions.TryGetValue(bone, out var rp) ? Fmt(rp) : "n/a";
                var parent = t.parent != null ? t.parent.name : "none";
                Debug.Log("[pose] " + bone + " actual=" + Fmt(actual) + " rest=" + rest + " target=" + Fmt(wanted) + " d=" + delta + " parent=" + parent);
            }
        }

        static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("0.000", CultureInfo.InvariantCulture) + ","
                + v.y.ToString("0.000", CultureInfo.InvariantCulture) + ","
                + v.z.ToString("0.000", CultureInfo.InvariantCulture) + ")";
        }

        sealed class CliClip
        {
            public float Fps;
            public string[] Joints;
            public int FrameCount;
            public int JointCount;
            public float[] Positions;
            public Dictionary<string, int> Index;

            public Vector3 At(int frame, string joint)
            {
                if (!Index.TryGetValue(joint, out var j)) return Vector3.zero;
                var offset = (frame * JointCount + j) * 3;
                return new Vector3(Positions[offset], Positions[offset + 1], Positions[offset + 2]);
            }

            public bool Has(string joint) => Index.ContainsKey(joint);
        }

        public static void Capture()
        {
            var args = Environment.GetCommandLineArgs();
            var json = Arg(args, "-motion", DefaultJson);
            var times = Arg(args, "-times", "0,15,30,45,60");
            var shot = Arg(args, "-shot", "motion");
            var orbit = ParseFloat(args, "-orbit", 86f);
            var fov = ParseFloat(args, "-fov", 34f);
            var hold = ParseFloat(args, "-hold", 0f) > 0.5f;
            var pov = ParseFloat(args, "-pov", 0f) > 0.5f;
            _useLegs = ParseFloat(args, "-legs", 1f) > 0.5f;
            var pin = ParseFloat(args, "-pin", 0f) > 0.5f;
            _pinLift = ParseFloat(args, "-pinLift", -0.10f);
            _marker = ParseFloat(args, "-marker", 0f) > 0.5f;
            _probe = ParseFloat(args, "-probeRig", 0f) > 0.5f;
            _offVrm = ParseFloat(args, "-offVrm", 0f) > 0.5f;
            _dumpBones = ParseFloat(args, "-dumpBones", 0f) > 0.5f;
            _bake = ParseFloat(args, "-bake", 0f) > 0.5f;
            _join = ParseFloat(args, "-join", 0f) > 0.5f;
            _noClip = ParseFloat(args, "-noClip", 0f) > 0.5f;
            _spineLog = ParseFloat(args, "-spineLog", 0f) > 0.5f;
            _spineAxis = ParseFloat(args, "-spineAxis", 1f);
            _spineFix = ParseFloat(args, "-spineFix", 1f) > 0.5f;
            _spineUp = ParseFloat(args, "-spineUp", 0.35f);
            _dumpPose = ParseFloat(args, "-dumpPose", 0f) > 0.5f;
            _eye = ParseVec(args, "-eye");
            _aim = ParseVec(args, "-aim");
            _hideHim = ParseFloat(args, "-hideHim", 0f) > 0.5f;
            _ghost = ParseFloat(args, "-ghost", 0f) > 0.5f;
            _poseError = ParseFloat(args, "-poseError", 0f) > 0.5f;

            if (!File.Exists(json))
            {
                Debug.LogError("[motion] missing clip " + json);
                EditorApplication.Exit(1);
                return;
            }

            _clip = Load(json);
            Debug.Log("[motion] clip frames=" + _clip.FrameCount + " joints=" + _clip.JointCount);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            var scene = "Assets/Scenes/Main.unity";
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            var boot = Object.FindFirstObjectByType<RoomBootstrap>();
            if (boot == null)
            {
                Debug.LogError("[motion] Main.unity has no RoomBootstrap");
                EditorApplication.Exit(1);
                return;
            }

            boot.Boot();
            var actor = boot.Actor;
            var rig = actor == null ? null : actor.Rig;
            if (rig == null)
            {
                Debug.LogError("[motion] no actor rig");
                EditorApplication.Exit(1);
                return;
            }

            actor.SitAt(actor.SeatId);
            CaptureHeadRest(rig);
            CaptureSpineFractions(rig);

            if (_offVrm)
            {
                if (rig.Instance != null) rig.Instance.enabled = false;
                if (boot.Player != null)
                {
                    var pb = boot.Player.GetComponent<PlayerBody>();
                    if (pb != null && pb.Avatar != null && pb.Avatar.Instance != null) pb.Avatar.Instance.enabled = false;
                }
                Debug.Log("[motion] Vrm10Instance disabled on both avatars");
            }

            var camera = boot.OutputCamera;
            Camera camera2 = camera;
            if (ParseFloat(args, "-freeCam", 0f) > 0.5f)
            {
                var go = new GameObject("CliShotCamera");
                camera2 = go.AddComponent<Camera>();
                camera2.clearFlags = CameraClearFlags.SolidColor;
                camera2.backgroundColor = camera.backgroundColor;
                camera2.cullingMask = camera.cullingMask;
                camera2.nearClipPlane = 0.05f;
                camera2.farClipPlane = 60f;
                camera2.allowHDR = false;
                var extra = camera2.GetUniversalAdditionalCameraData();
                extra.renderPostProcessing = true;
                camera2.enabled = false;
                Debug.Log("[motion] free camera in use");
            }

            var brain = Object.FindFirstObjectByType<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;
            boot.Rig.SetOrbit(orbit, 8f);

            // The first Camera.Render of a session picks up the wrong MToon materials (hair red, cloth cyan,
            // skin green). Burn one frame into a throwaway texture before anything is written to disk.
            var warmup = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = warmup;
            camera.Render();
            camera.targetTexture = null;
            Object.DestroyImmediate(warmup);

            _shotDist = ParseFloat(args, "-shotDist", 2.45f);
            _focusDrop = ParseFloat(args, "-focusDrop", 0.10f);
            _handOverlap = ParseFloat(args, "-handOverlap", 0.035f);
            var ev = ParseFloat(args, "-exposure", 0f);
            if (Mathf.Abs(ev) > 0.001f) AddExposure(ev);

            Directory.CreateDirectory("Shots");
            foreach (var token in times.Split(','))
            {
                if (!float.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    continue;
                var rate = _clip.Fps;
                var frame = Mathf.Clamp(Mathf.RoundToInt(seconds * rate), 0, _clip.FrameCount - 1);
                if (_dumpPose) { CaptureRest(rig); }
                if (!_noClip) PoseTo(rig, frame);
                if (_dumpPose) { DumpPose(rig); }
                if (_poseError) ReportPoseError(rig, frame);
                if (_join) JoinHands(boot, rig, Mathf.Clamp01((seconds - 0.35f) / 1.15f));
                if (pin) { PinHerHands(rig); if (_marker) { var hb = rig.Bone(HumanBodyBones.LeftHand); if (hb != null) AddMarker(hb.position); } }
                if (_marker && boot.Player != null) { var pb = boot.Player.GetComponent<PlayerBody>(); if (pb != null && pb.Avatar != null) { var ph = pb.Avatar.Bone(HumanBodyBones.RightHand); if (ph != null) AddMarker2(ph.position); } }
                var body = boot.Player == null ? null : boot.Player.GetComponent<PlayerBody>();
                var body0 = body;
                if (_probe) ProbeRig(rig, body != null ? body.Avatar : null);
                if (_dumpBones) { DumpBones(rig); _dumpBones = false; }
                if (hold && !_join) CloseTheGap(boot, rig);
                if (hold || _join) TwoShot(camera2, rig, body != null ? body.Avatar : null);
                if (_hideHim && body != null && body.Avatar != null)
                    foreach (var r in body.Avatar.GetComponentsInChildren<Renderer>()) r.enabled = false;
                if (_eye.HasValue)
                {
                    var chest = rig.Bone(HumanBodyBones.Chest) ?? rig.Bone(HumanBodyBones.Hips);
                    camera2.transform.position = _eye.Value;
                    camera2.transform.LookAt(_aim ?? chest.position);
                }

                if (_bake) { Rebake(rig); if (body != null) Rebake(body.Avatar); }

                var name = shot + "-" + frame.ToString("000", CultureInfo.InvariantCulture);
                WriteShot(camera2, name, fov);

                if (pov)
                {
                    var body2 = boot.Player == null ? null : boot.Player.GetComponent<PlayerBody>();
                    LookFromHisEyes(camera2, rig, body2 != null ? body2.Avatar : null);
                    WriteShot(camera2, name + "-pov", 60f);
                }
            }

            Debug.Log("[motion] wrote " + shot + " shots to Shots/");
            EditorApplication.Exit(0);
        }

        static CliClip Load(string path)
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonUtility.FromJson<Clip>(raw);
            var index = new Dictionary<string, int>();
            for (var i = 0; i < parsed.joints.Length; i++) index[parsed.joints[i]] = i;
            return new CliClip
            {
                Fps = parsed.fps > 0.01f ? parsed.fps : 30f,
                Joints = parsed.joints,
                FrameCount = parsed.frameCount,
                JointCount = parsed.jointCount,
                Positions = parsed.positions,
                Index = index,
            };
        }

        /// <summary>
        /// Keeps her where the seated pose put her and re-aims the bone chain so each mapped bone points at
        /// its target child. Targets arrive in the clip's own frame (Y up, metres, canonical origin), so the
        /// whole cloud is rebased onto her current hips before anything is solved.
        /// </summary>
        static bool IsSpineBone(HumanBodyBones bone)
        {
            return bone == HumanBodyBones.Hips || bone == HumanBodyBones.Spine
                || bone == HumanBodyBones.Chest || bone == HumanBodyBones.UpperChest;
        }

        static bool IsLegBone(HumanBodyBones bone)
        {
            return bone == HumanBodyBones.LeftUpperLeg || bone == HumanBodyBones.LeftLowerLeg
                || bone == HumanBodyBones.LeftFoot || bone == HumanBodyBones.LeftToes
                || bone == HumanBodyBones.RightUpperLeg || bone == HumanBodyBones.RightLowerLeg
                || bone == HumanBodyBones.RightFoot || bone == HumanBodyBones.RightToes;
        }

        static bool _useLegs = true;
        static float _pinLift = -0.10f;
        static bool _marker;
        static bool _probe;
        static bool _offVrm;
        static bool _dumpBones;
        static bool _bake;
        static bool _join;
        static Vector3 _meetPoint;
        static float _shotDist = 2.45f;
        static float _focusDrop = 0.10f;
        static float _handOverlap = 0.035f;
        static bool _noClip;
        static bool _spineLog;
        // How much of each vertebra's chord is replaced by the torso's own axis. The clip's lumbar links lean
        // back far harder than its overall posture does, so a straight chord hands a seated rig an arched back.
        static float _spineAxis = 1f;
        static bool _spineFix = true;
        // How much of that axis is straightened toward vertical, for when the clip's own slouch is not wanted.
        static float _spineUp = 0.35f;

        static void PoseTo(AvatarRig rig, int frame)
        {
            if (!_clip.Has("Hips"))
            {
                Debug.LogError("[motion] clip has no Hips joint");
                return;
            }

            var hips = rig.Bone(HumanBodyBones.Hips);
            if (hips == null) return;

            // The clip is authored in a right-handed frame: its character has the left on +X while facing
            // +Z. Unity's is left-handed and puts the left on -X, so X has to be flipped before the yaw or
            // every limb lands on the wrong side and the arms cross over the torso.
            var yaw = actorYaw(hips);

            var clipHips = _clip.At(frame, "Hips");

            Vector3 Dir(string from, string to)
            {
                var v = _clip.At(frame, to) - _clip.At(frame, from);
                return Rotate(new Vector3(-v.x, v.y, v.z), yaw).normalized;
            }

            // Where the clip puts a joint, expressed in her world: rebase onto her hips, mirror X, turn by yaw.
            Vector3 ClipPoint(string joint)
            {
                var v = _clip.At(frame, joint) - clipHips;
                return hips.position + Rotate(new Vector3(-v.x, v.y, v.z), yaw);
            }

            _lastTargets.Clear();

            // The spine is solved as one curve rather than joint-for-joint. The clip's lumbar links are short
            // and lean back hard while its chest link is long and upright; giving those angles to four evenly
            // sized VRM bones walks the entire waist backwards and she reads as arching her back. So the clip's
            // spine becomes a polyline, and each bone takes the chord of the slice it covers, using its own
            // resting length as the fraction. The overall S-curve survives; the spikes that only exist because
            // SOMA splits the spine differently do not.
            if (_spineFix && _spineFractions != null && _clip.Has(SpineChain[0]) && _clip.Has(SpineChain[SpineChain.Length - 1]))
            {
                var points = new Vector3[SpineChain.Length];
                for (var i = 0; i < SpineChain.Length; i++) points[i] = ClipPoint(SpineChain[i]);

                var lengths = new float[SpineChain.Length - 1];
                var span = 0f;
                for (var i = 0; i < lengths.Length; i++)
                {
                    lengths[i] = Vector3.Distance(points[i], points[i + 1]);
                    span += lengths[i];
                }

                if (span > 1e-4f)
                {
                    // The clip's posture as a whole, hip to neck. Its short lumbar links swing 30-odd degrees
                    // behind it and its long chest link swings back in front, so the axis is the honest read of
                    // where the torso is actually pointing; each vertebra stays mostly on it, keeping only a
                    // fraction of the local curve as the lumbar arch a spine is supposed to have.
                    var axis = (points[points.Length - 1] - points[0]).normalized;
                    if (_spineUp > 0f) axis = Vector3.Slerp(axis, Vector3.up, Mathf.Clamp01(_spineUp));

                    for (var i = 0; i < SpineBones.Length; i++)
                    {
                        if (!ChildOf.TryGetValue(SpineBones[i], out var childBone)) continue;
                        var boneTransform = rig.Bone(SpineBones[i]);
                        var childTransform = rig.Bone(childBone);
                        if (boneTransform == null || childTransform == null) continue;

                        var from = AlongPolyline(points, lengths, span, _spineFractions[i]);
                        var to = AlongPolyline(points, lengths, span, _spineFractions[i + 1]);
                        var dir = to - from;
                        var current = childTransform.position - boneTransform.position;
                        if (dir.sqrMagnitude < 1e-10f || current.sqrMagnitude < 1e-8f) continue;

                        dir.Normalize();
                        if (_spineAxis > 0f && axis.sqrMagnitude > 0.5f)
                            dir = Vector3.Lerp(dir, axis, _spineAxis).normalized;

                        boneTransform.rotation = Quaternion.FromToRotation(current, dir) * boneTransform.rotation;
                        _lastTargets[childBone] = boneTransform.position + dir * current.magnitude;

                    }
                }
            }

            // Parent before child; each bone copies the direction of the clip segment its child joint stands
            // for. Directions, not positions: her bones are a different length from the clip's, and aiming at
            // an absolute point makes the lean of the chest fight the length of the neck.
            foreach (var (bone, from, to) in Segments)
            {
                if (_spineFix && IsSpineBone(bone)) continue;
                if (!_useLegs && IsLegBone(bone)) continue;
                if (!_clip.Has(from) || !_clip.Has(to)) continue;
                if (!ChildOf.TryGetValue(bone, out var childBone)) continue;

                var boneTransform = rig.Bone(bone);
                var childTransform = rig.Bone(childBone);
                if (boneTransform == null || childTransform == null) continue;

                var dir = Dir(from, to);
                var current = childTransform.position - boneTransform.position;
                if (current.sqrMagnitude < 1e-8f || dir.sqrMagnitude < 1e-8f) continue;
                boneTransform.rotation = Quaternion.FromToRotation(current, dir) * boneTransform.rotation;
                _lastTargets[childBone] = boneTransform.position + dir * current.magnitude;
            }

            // The head is a leaf, so instead of aiming a child it is turned to face the clip's own gaze:
            // rest face direction -> clip face direction, keeping the seated rest orientation as the base.
            // The neck has no clip segment of its own (the clip's neck joints run at the skull, not up the
            // spine), so it points at where the clip puts the head instead: that carries the bow into the neck
            // rather than folding the whole nod into the head bone.
            var neckBone = rig.Bone(HumanBodyBones.Neck);
            var headBone = rig.Bone(HumanBodyBones.Head);
            if (neckBone != null && headBone != null && _clip.Has("Head"))
            {
                var wantNeck = ClipPoint("Head") - neckBone.position;
                var curNeck = headBone.position - neckBone.position;
                if (wantNeck.sqrMagnitude > 1e-8f && curNeck.sqrMagnitude > 1e-8f)
                    neckBone.rotation = Quaternion.FromToRotation(curNeck, wantNeck) * neckBone.rotation;
            }

            if (_spineLog)
            {
                // Measured on the result, not on the intent: this is what the mesh actually bends around.
                var forward = Vector3.ProjectOnPlane(rig.Root.forward, Vector3.up).normalized;
                for (var i = 0; i < SpineBones.Length; i++)
                {
                    var b = rig.Bone(SpineBones[i]);
                    if (b == null || !ChildOf.TryGetValue(SpineBones[i], out var c)) continue;
                    var ct = rig.Bone(c);
                    if (ct == null) continue;
                    var d = (ct.position - b.position).normalized;
                    var lean = Vector3.Angle(Vector3.up, d) * (Vector3.Dot(d, forward) >= 0f ? 1f : -1f);
                    Debug.Log("[spine] " + SpineBones[i] + " leanFwdDeg=" + lean.ToString("0.0", CultureInfo.InvariantCulture));
                }
            }

            if (_ghost) DrawGhostCloud(rig, ClipPoint);

            var head = rig.Bone(HumanBodyBones.Head);
            if (head != null && _clip.Has("Head") && _clip.Has("HeadEnd") && _clip.Has("LeftEye") && _clip.Has("RightEye"))
            {
                // HeadEnd is the crown of the skull, not the gaze: the gaze comes from crossing the skull's up
                // axis with the line between the eyes, then going through the same mirror + yaw as everything else.
                var up = _clip.At(frame, "HeadEnd") - _clip.At(frame, "Head");
                var eyes = _clip.At(frame, "RightEye") - _clip.At(frame, "LeftEye");
                var face = Vector3.Cross(up, eyes);
                if (face.sqrMagnitude > 1e-10f)
                {
                    face = Rotate(new Vector3(-face.x, face.y, face.z), yaw).normalized;
                    head.rotation = Quaternion.FromToRotation(_restHeadFace, face) * _restHeadRot;
                }
            }
        }


        /// <summary>
        /// Puts the player's right hand on hers so the contact reads as a handhold: analytic two-bone IK,
        /// elbow plane chosen toward "down and forward" so the arm never bends backwards.
        /// </summary>
        static void HoldHands(RoomBootstrap boot, AvatarRig her)
        {
            var player = boot.Player == null ? null : boot.Player.GetComponent<PlayerBody>();
            var him = player == null ? null : player.Avatar;
            if (him == null) return;

            var herHand = her.Bone(HumanBodyBones.LeftHand);
            if (herHand == null) return;
            AimTwoBone(him, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, herHand.position, Vector3.down);
        }

        /// <summary>
        /// Analytic two-bone IK: keeps the shoulder in place, places the elbow on the circle that reaches the
        /// target, then rotates both bones with FromToRotation. The bend hint picks which way the elbow folds.
        /// </summary>
        static void AimTwoBone(AvatarRig rig, HumanBodyBones upperBone, HumanBodyBones lowerBone, HumanBodyBones handBone, Vector3 target, Vector3 bendHint)
        {
            var shoulder = rig.Bone(upperBone);
            var elbow = rig.Bone(lowerBone);
            var hand = rig.Bone(handBone);
            if (shoulder == null || elbow == null || hand == null)
            {
                Debug.LogWarning("[ik] missing bones " + upperBone + "/" + lowerBone + "/" + handBone);
                return;
            }

            var start = shoulder.position;
            var upper = Vector3.Distance(start, elbow.position);
            var lower = Vector3.Distance(elbow.position, hand.position);
            if (upper < 1e-4f || lower < 1e-4f) return;
            var toTarget = target - start;
            var distance = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upper - lower) + 0.01f, upper + lower - 0.01f);
            var dir = toTarget.normalized;

            var pole = bendHint + Vector3.ProjectOnPlane(rig.Root.forward, Vector3.up).normalized * 0.35f;
            var axis = Vector3.Cross(dir, pole);
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(dir, Vector3.right);
            axis.Normalize();

            var cos = Mathf.Clamp((upper * upper + distance * distance - lower * lower) / (2f * upper * distance), -1f, 1f);
            var elbowDir = Quaternion.AngleAxis(Mathf.Acos(cos) * Mathf.Rad2Deg, axis) * dir;
            var elbowPos = start + elbowDir * upper;

            var before = Vector3.Distance(hand.position, target);
            shoulder.rotation = Quaternion.FromToRotation(elbow.position - start, elbowPos - start) * shoulder.rotation;
            elbow.rotation = Quaternion.FromToRotation(hand.position - elbow.position, target - elbowPos) * elbow.rotation;
            Debug.Log("[ik] " + upperBone + " before=" + before.ToString("F3") + " after=" + Vector3.Distance(hand.position, target).ToString("F3") + " target=" + target.ToString("F2") + " handPos=" + hand.position.ToString("F2"));
        }

        /// <summary>
        /// The generated clip leaves her hands on her thighs; pin them in front of her chest so the contact
        /// reads, and keep letting the clip drive her torso, shoulders and head.
        /// </summary>
        static void PinHerHands(AvatarRig her)
        {
            var chest = her.Bone(HumanBodyBones.UpperChest);
            if (chest == null) chest = her.Bone(HumanBodyBones.Chest);
            if (chest == null) { Debug.LogWarning("[pin] no chest bone"); return; }

            var forward = Vector3.ProjectOnPlane(her.Root.forward, Vector3.up).normalized;
            var side = Vector3.Cross(Vector3.up, forward).normalized;
            var hold = chest.position + forward * 0.34f + Vector3.up * _pinLift;

            Debug.Log("[pin] chest=" + chest.name + " hold=" + hold.ToString("F2") + " forward=" + forward.ToString("F2"));
            AimTwoBone(her, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                hold + side * 0.07f, Vector3.down);
            AimTwoBone(her, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                hold - side * 0.07f, Vector3.down);
        }

        /// <summary>What the player actually sees: eye height of his body, looking at her face.</summary>
        static void LookFromHisEyes(Camera camera, AvatarRig her, AvatarRig him)
        {
            var eyes = him == null ? null : him.Bone(HumanBodyBones.Head);
            var head = her.Bone(HumanBodyBones.Head);
            if (eyes == null || head == null) return;

            var forward = Vector3.ProjectOnPlane(her.Root.position - eyes.position, Vector3.up).normalized;
            camera.transform.position = eyes.position + forward * 0.12f + Vector3.up * 0.06f;
            camera.transform.LookAt(head.position - Vector3.up * 0.10f);
        }

        /// <summary>Two-shot: both figures and the joined hands in one frame, from her left-front.</summary>
        /// <summary>Walks the player in until his arm can actually reach her hand, then re-solves the IK.</summary>
        static void CloseTheGap(RoomBootstrap boot, AvatarRig her)
        {
            var body = boot.Player == null ? null : boot.Player.GetComponent<PlayerBody>();
            var him = body == null ? null : body.Avatar;
            if (him == null) return;

            var herHand = her.Bone(HumanBodyBones.LeftHand);
            var shoulder = him.Bone(HumanBodyBones.RightUpperArm);
            var elbow = him.Bone(HumanBodyBones.RightLowerArm);
            var hand = him.Bone(HumanBodyBones.RightHand);
            if (herHand == null || shoulder == null || elbow == null || hand == null) return;

            var armLength = Vector3.Distance(shoulder.position, elbow.position) + Vector3.Distance(elbow.position, hand.position);
            for (var pass = 0; pass < 4; pass++)
            {
                var toTarget = herHand.position - shoulder.position;
                var gap = toTarget.magnitude - armLength * 0.94f;
                if (gap <= 0.01f) break;
                var step = Vector3.ProjectOnPlane(toTarget.normalized, Vector3.up).normalized * gap;
                him.transform.position += step;
            }

            AimTwoBone(him, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, herHand.position, Vector3.down);
        }
        static void ProbeRig(AvatarRig her, AvatarRig him)
        {
            Debug.Log("[probe] her root=" + her.Root.name + " comps=" + string.Join(",", her.Root.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()));
            if (him != null) Debug.Log("[probe] his root=" + him.Root.name + " comps=" + string.Join(",", him.Root.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()));

            foreach (var t in her.Root.GetComponentsInChildren<Transform>(true))
                if (t.name == "J_Bip_R_UpperArm" || t.name == "J_Bip_L_UpperArm" || t.name.Contains("ControlRig"))
                    Debug.Log("[probe] her " + t.name + " parent=" + (t.parent == null ? "-" : t.parent.name));
            if (him != null)
                foreach (var t in him.Root.GetComponentsInChildren<Transform>(true))
                    if (t.name == "J_Bip_R_UpperArm" || t.name.Contains("ControlRig"))
                        Debug.Log("[probe] his " + t.name + " parent=" + (t.parent == null ? "-" : t.parent.name));
        }
        /// <summary>
        /// Freezes the current skinned pose into static meshes. Headless renders reuse the bone palette from the
        /// last player-loop tick, so bone writes never reach the GPU; BakeMesh evaluates skinning on the CPU
        /// with the transforms we just wrote, which is exactly what an offline still needs.
        /// </summary>
        /// <summary>Pull a point onto the sphere a two-bone arm can reach (0.97 of its length, so the elbow keeps a bend).</summary>
        static Vector3 ClampToReach(Vector3 point, Transform shoulder, HumanBodyBones lowerBone, HumanBodyBones handBone, AvatarRig rig)
        {
            var elbow = rig.Bone(lowerBone);
            var hand = rig.Bone(handBone);
            if (elbow == null || hand == null) return point;
            var reach = (Vector3.Distance(shoulder.position, elbow.position) + Vector3.Distance(elbow.position, hand.position)) * 0.90f;
            var offset = point - shoulder.position;
            return offset.magnitude <= reach ? point : shoulder.position + offset.normalized * reach;
        }
        static void AddExposure(float ev)
        {
            var go = new GameObject("CliExposure");
            var volume = go.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.priority = 500f;
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            var tone = profile.Add<UnityEngine.Rendering.Universal.Tonemapping>();
            tone.active = true;
            tone.mode.Override(UnityEngine.Rendering.Universal.TonemappingMode.ACES);
            var colour = profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>();
            colour.active = true;
            colour.postExposure.Override(ev);
            colour.saturation.Override(6f);
            volume.profile = profile;
            Debug.Log("[motion] exposure volume ev=" + ev.ToString("F2"));
        }
        static void Rebake(AvatarRig rig)
        {
            if (rig == null) return;
            foreach (var t in rig.Root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.EndsWith("-baked")) continue;
                Object.DestroyImmediate(t.gameObject);
            }
            foreach (var smr in rig.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.enabled = true;
            BakeSkinned(rig);
        }
        static void BakeSkinned(AvatarRig rig)
        {
            if (rig == null) return;
            var baked = 0;
            foreach (var smr in rig.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.enabled) continue;
                var mesh = new Mesh { name = smr.name + "-baked" };
                smr.BakeMesh(mesh, true);
                var go = new GameObject(smr.name + "-baked");
                go.transform.SetParent(smr.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = smr.sharedMaterials;
                mr.shadowCastingMode = smr.shadowCastingMode;
                mr.receiveShadows = smr.receiveShadows;
                smr.enabled = false;
                baked++;
            }
            Debug.Log("[bake] " + rig.Root.name + " meshes=" + baked);
        }
        static void DumpBones(AvatarRig rig)
        {
            var human = rig.Bone(HumanBodyBones.LeftHand);
            foreach (var smr in rig.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = smr.bones;
                var hit = bones.FirstOrDefault(b => b != null && b.name == "J_Bip_L_Hand");
                var upper = bones.FirstOrDefault(b => b != null && b.name == "J_Bip_L_UpperArm");
                Debug.Log("[bones] " + smr.name + " bones=" + bones.Length
                    + " hand=" + (hit == null ? "-" : hit.name + (ReferenceEquals(hit, human) ? " (same as humanoid)" : " (DIFFERENT object)"))
                    + " upper=" + (upper == null ? "-" : upper.name)
                    + " rootBone=" + (smr.rootBone == null ? "-" : smr.rootBone.name));
                if (hit != null && upper != null)
                    Debug.Log("[bones]   hand parent=" + hit.parent.name + " upper parent=" + upper.parent.name
                        + " upperLocalRot=" + upper.localRotation.eulerAngles.ToString("F1"));
            }
        }
        /// <summary>
        /// Puts one hand of each figure on the same point between them. Both arms are solved with the same
        /// analytic IK, so the two palms meet instead of overlapping the wrists.
        /// </summary>
        static void JoinHands(RoomBootstrap boot, AvatarRig her, float u = 1f)
        {
            var pb = boot.Player == null ? null : boot.Player.GetComponent<PlayerBody>();
            var him = pb == null ? null : pb.Avatar;
            if (him == null) return;

            var herChest = her.Bone(HumanBodyBones.UpperChest) ?? her.Bone(HumanBodyBones.Chest);
            var hisChest = him.Bone(HumanBodyBones.UpperChest) ?? him.Bone(HumanBodyBones.Chest);
            if (herChest == null || hisChest == null) return;

            var forward = Vector3.ProjectOnPlane(her.Root.forward, Vector3.up).normalized;
            var side = Vector3.Cross(Vector3.up, forward).normalized;

            // The palms have to land on a point BOTH arms can actually reach. Aim the meeting point at the midpoint
            // of the two shoulders, then walk it back onto each arm's reach sphere until neither is stretched.
            var herShoulder = her.Bone(HumanBodyBones.LeftUpperArm);
            var hisShoulder = him.Bone(HumanBodyBones.RightUpperArm);
            if (herShoulder == null || hisShoulder == null) return;

            var meet = herShoulder.position + forward * 0.30f - Vector3.up * 0.12f;
            for (var pass = 0; pass < 3; pass++)
            {
                meet = ClampToReach(meet, herShoulder, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, her);
                meet = ClampToReach(meet, hisShoulder, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, him);
            }

            var herHand0 = her.Bone(HumanBodyBones.LeftHand);
            var hisHand0 = him.Bone(HumanBodyBones.RightHand);
            if (herHand0 == null || hisHand0 == null) return;

            // Ease-in-out between "hands where they hang" and the meeting point, so a handful of stills reads as
            // one continuous reach rather than two unrelated poses.
            var ease = u * u * (3f - 2f * u);
            _meetPoint = meet;
            var herTarget = Vector3.Lerp(herHand0.position, meet + forward * _handOverlap + side * 0.012f, ease);
            var hisTarget = Vector3.Lerp(hisHand0.position, meet - forward * _handOverlap - side * 0.012f, ease);

            AimTwoBone(her, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, herTarget, Vector3.down);
            AimTwoBone(him, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, hisTarget, Vector3.down);
            var hipsB = her.Bone(HumanBodyBones.Hips);
            Debug.Log("[join] actor=" + boot.Actor.transform.position.ToString("F3") + " root=" + her.Root.position.ToString("F3")
                + " hips=" + (hipsB == null ? "-" : hipsB.position.ToString("F3")) + " head=" + her.Bone(HumanBodyBones.Head).position.ToString("F3"));
            Debug.Log("[join] meet=" + meet.ToString("F3") + " herHand=" + her.Bone(HumanBodyBones.LeftHand).position.ToString("F3")
                + " hisHand=" + him.Bone(HumanBodyBones.RightHand).position.ToString("F3"));
        }
        static void AddMarker2(Vector3 p)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.position = p;
            go.transform.localScale = Vector3.one * 0.07f;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", Color.green);
            mat.SetFloat("_Cull", 0f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }
        static void AddMarker(Vector3 p)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.position = p;
            go.transform.localScale = Vector3.one * 0.06f;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", Color.red);
            mat.SetFloat("_Cull", 0f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }
        static void TwoShot(Camera camera, AvatarRig her, AvatarRig him)
        {
            var chest = her.Bone(HumanBodyBones.UpperChest) ?? her.Bone(HumanBodyBones.Chest);
            if (chest == null) return;

            var forward = Vector3.ProjectOnPlane(her.Root.forward, Vector3.up).normalized;
            var side = Vector3.Cross(Vector3.up, forward).normalized;
            var focus = _join && _meetPoint.sqrMagnitude > 0f ? _meetPoint : chest.position;
            if (!_join && him != null)
            {
                var hisChest = him.Bone(HumanBodyBones.UpperChest) ?? him.Bone(HumanBodyBones.Chest);
                if (hisChest != null) focus = (chest.position + hisChest.position) * 0.5f;
            }
            focus -= Vector3.up * _focusDrop;
            camera.transform.position = focus + side * _shotDist - forward * 0.15f + Vector3.up * 0.22f;
            camera.transform.LookAt(focus);
        }
        static void LookAtHands(Camera camera, AvatarRig her, AvatarRig him)
        {
            var herHand = her.Bone(HumanBodyBones.LeftHand);
            var hisHand = him == null ? null : him.Bone(HumanBodyBones.RightHand);
            if (herHand == null) return;

            var focus = hisHand == null ? herHand.position : (herHand.position + hisHand.position) * 0.5f;
            var facing = Vector3.ProjectOnPlane(her.Root.forward, Vector3.up).normalized;
            var side = Vector3.Cross(Vector3.up, facing).normalized;
            camera.transform.position = focus - facing * 0.55f + side * 1.15f + Vector3.up * 0.30f;
            camera.transform.LookAt(focus + Vector3.up * 0.02f);
        }

        static float actorYaw(Transform hips)
        {
            var root = hips;
            while (root.parent != null && root.parent.GetComponent<AvatarRig>() != null) root = root.parent;
            var forward = Vector3.ProjectOnPlane(root.forward, Vector3.up);
            return forward.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(forward.x, forward.z);
        }

        static Vector3 Rotate(Vector3 v, float yaw)
        {
            var c = Mathf.Cos(yaw);
            var s = Mathf.Sin(yaw);
            return new Vector3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
        }

        static void WriteShot(Camera camera, string name, float fov)
        {
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = rt;
            camera.fieldOfView = fov;
            // Skinning is one render behind the bone writes: the first Render flushes this pose into the
            // bone buffers, the second one actually draws it.
            camera.Render();
            camera.Render();
            RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();
            var bytes = texture.EncodeToPNG();
            File.WriteAllBytes("Shots/" + name + ".png", bytes);
            camera.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(texture);
            Object.DestroyImmediate(rt);
            Debug.Log("[motion] wrote Shots/" + name + ".png (" + bytes.Length + " bytes) cam=" + camera.transform.position.ToString("F2")
                + " rot=" + camera.transform.eulerAngles.ToString("F1") + " fov=" + camera.fieldOfView.ToString("F0"));
        }

        static string Arg(string[] args, string flag, string fallback)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }

        static float ParseFloat(string[] args, string flag, float fallback) =>
            float.TryParse(Arg(args, flag, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }
}
