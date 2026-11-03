using System.IO;
using System.Text;
using Mas3D.Avatar;
using UnityEditor;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Renders the sitting pose from the front and from the side and dumps the numbers the pose
    /// table is tuned against: hip height versus the seat, thigh/shin direction against the seat's
    /// forward, and where the hands land. Needs a GPU: never pass -nographics.
    ///   powershell -ExecutionPolicy Bypass -File tools/unity.ps1 avatarpose
    /// </summary>
    public static class CliAvatarPose
    {
        const int Width = 720;
        const int Height = 960;
        const string SeatId = "S1";

        static readonly HumanBodyBones[] TableBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
        };

        public static void Probe()
        {
            var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(outDir);

            var table = AnchorTable.Load();
            if (table == null) { Debug.LogError("[pose] anchors.json missing"); EditorApplication.Exit(1); return; }
            var seat = table.Seat(SeatId);
            if (seat == null) { Debug.LogError("[pose] anchors.json has no seat " + SeatId); EditorApplication.Exit(1); return; }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarRig.DefaultPrefabPath);
            if (prefab == null) { Debug.LogError("[pose] not imported: " + AvatarRig.DefaultPrefabPath); EditorApplication.Exit(1); return; }

            var rig = AvatarRig.Spawn(prefab);
            var avatarSeat = rig.gameObject.AddComponent<AvatarSeat>();
            avatarSeat.Rig = rig;   // Awake does not run outside play mode

            Debug.Log($"[pose] seat {seat.id} x={seat.x:F3} y={seat.y:F3} z={seat.z:F3} topY={seat.topY:F3} facingY={seat.facingY} mesh={seat.mesh}");
            Debug.Log("[pose] table " + AvatarPose.Describe());

            Eulers("rest", rig);
            Dirs("rest", rig, seat);

            var room = LoadRoom();
            avatarSeat.Sit(seat);
            Eulers("sitting", rig);
            Dirs("sitting", rig, seat);
            if (room != null) BenchOverlap(rig, seat);
            Debug.Log($"[pose] hips={Fmt(avatarSeat.HipsWorld)} want={(seat.y + AvatarSeat.HipsGap):F3} seated={avatarSeat.Seated}");

            // She faces -x on S1 (facingY -90), so "in front of her" is look + forward * radius.
            var fwd = Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward;

            var hipsBone = rig.Bone(HumanBodyBones.Hips);
            var kneeBone = rig.Bone(HumanBodyBones.LeftLowerLeg);
            if (hipsBone != null && kneeBone != null)
            {
                var ahead = Vector3.ProjectOnPlane(kneeBone.position - hipsBone.position, Vector3.up);
                Debug.Log($"[pose] knee-ahead dot={Vector3.Dot(ahead.normalized, fwd):F2} (test wants > 0.15)");
            }

            // She faces -x on S1, so the key light has to come from that side or the face is black.
            var key = new GameObject("key").AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.97f, 0.93f);
            key.intensity = 1.15f;
            key.transform.rotation = Quaternion.LookRotation((-fwd + Vector3.down * 0.45f).normalized);
            key.shadows = LightShadows.Soft;

            var fill = new GameObject("fill").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.color = new Color(0.72f, 0.78f, 0.95f);
            fill.intensity = 0.45f;
            fill.transform.rotation = Quaternion.LookRotation((-fwd * 0.2f + Vector3.down * 0.3f - Quaternion.Euler(0f, seat.facingY + 90f, 0f) * Vector3.forward * 0.9f).normalized);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.44f, 0.47f, 0.58f);
            RenderSettings.ambientEquatorColor = new Color(0.34f, 0.32f, 0.38f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.15f, 0.18f);

            var camGo = new GameObject("cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.08f, 0.1f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
#if MAS3D_URP
            camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
#endif

            // Frame the posed body, not the seat: a sitting monkey is shorter than a standing one.
            var bounds = PoseBounds(rig);
            var look = bounds.center;
            var tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var tanH = tanV * ((float)Width / Height);
            var radius = Mathf.Max(bounds.extents.y / tanV, Mathf.Max(bounds.extents.x, bounds.extents.z) / tanH) * 1.12f;

            var side = Quaternion.Euler(0f, seat.facingY + 90f, 0f) * Vector3.forward;

            cam.transform.position = look + fwd * radius + Vector3.up * 0.18f;
            cam.transform.LookAt(look);
            Debug.Log($"[pose] front cam={Fmt(cam.transform.position)} look={Fmt(look)} radius={radius:F2}");
            Shot(cam, Path.Combine(outDir, "avatar-pose-front.png"));

            cam.transform.position = look + side * radius + Vector3.up * 0.18f;
            cam.transform.LookAt(look);
            Debug.Log($"[pose] side cam={Fmt(cam.transform.position)} right={Fmt(side)}");
            Shot(cam, Path.Combine(outDir, "avatar-pose-side.png"));

            EditorApplication.Exit(0);
        }

        static GameObject LoadRoom()
        {
            const string path = "Assets/_Local/room/mita-room.glb";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) { Debug.Log("[pose] room not imported: " + path); return null; }

            var room = Object.Instantiate(asset);
            room.name = "probe-room";
            var bounds = new Bounds(room.transform.position, Vector3.zero);
            var first = true;
            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                if (first) { bounds = r.bounds; first = false; }
                else { bounds.Encapsulate(r.bounds); }
            }
            room.transform.position -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            Physics.SyncTransforms();
            Debug.Log($"[pose] room zeroed shift={Fmt(-new Vector3(bounds.center.x, bounds.min.y, bounds.center.z))}");
            return room;
        }

        /// <summary>
        /// Which mesh owns the lowest vertex inside the bench footprint — the same test the
        /// PlayMode case runs, but it names the offender instead of only reporting a height.
        /// </summary>
        static void BenchOverlap(AvatarRig rig, SeatEntry seat)
        {
            var benchGo = GameObject.Find(seat.mesh);
            var benchRenderer = benchGo != null ? benchGo.GetComponent<Renderer>() : null;
            if (benchRenderer == null) { Debug.Log("[pose] bench renderer not found: " + seat.mesh); return; }

            var b = benchRenderer.bounds;
            var limit = b.max.y - 0.02f;
            Debug.Log($"[pose] bench {seat.mesh} min={Fmt(b.min)} max={Fmt(b.max)} top={b.max.y:F3} limit={limit:F3}");

            var collider = benchGo.GetComponent<MeshCollider>();
            if (collider == null) collider = benchGo.AddComponent<MeshCollider>();
            var filter = benchGo.GetComponent<MeshFilter>();
            if (filter != null) collider.sharedMesh = filter.sharedMesh;
            Physics.SyncTransforms();

            if (filter != null && filter.sharedMesh != null)
            {
                var meshVerts = filter.sharedMesh.vertices;
                var meshT = filter.transform;
                var topMin = new Vector3(float.PositiveInfinity, 0f, float.PositiveInfinity);
                var topMax = new Vector3(float.NegativeInfinity, 0f, float.NegativeInfinity);
                var top = 0;
                for (var i = 0; i < meshVerts.Length; i++)
                {
                    var w = meshT.TransformPoint(meshVerts[i]);
                    if (w.y < b.max.y - 0.01f) continue;
                    top++;
                    topMin = Vector3.Min(topMin, w);
                    topMax = Vector3.Max(topMax, w);
                }
                Debug.Log($"[pose] bench top slab verts={top} x {topMin.x:F3}..{topMax.x:F3} z {topMin.z:F3}..{topMax.z:F3}");
            }

            var baked = new Mesh();
            var lowest = float.PositiveInfinity;
            var lowestAt = Vector3.zero;
            foreach (var smr in rig.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.BakeMesh(baked, true);
                var verts = baked.vertices;
                var t = smr.transform;
                var meshLow = float.PositiveInfinity;
                var meshAt = Vector3.zero;
                var inside = 0;
                var meshBone = "?";
                for (var i = 0; i < verts.Length; i++)
                {
                    var w = t.TransformPoint(verts[i]);
                    if (Mathf.Abs(w.x - b.center.x) > b.extents.x + 0.02f) continue;
                    if (Mathf.Abs(w.z - b.center.z) > b.extents.z + 0.02f) continue;
                    inside++;
                    if (w.y < meshLow) { meshLow = w.y; meshAt = w; meshBone = Dominant(smr, i); }
                }
                if (inside == 0) continue;
                if (meshLow < limit)
                {
                    Debug.Log($"[pose] sink {smr.name}/{baked.name} inside={inside} low={meshLow:F3} at={Fmt(meshAt)} bone={meshBone} above={Above(meshAt)}");
                }
                if (meshLow < lowest) { lowest = meshLow; lowestAt = meshAt; }
            }
            Debug.Log($"[pose] sink lowest={lowest:F3} at={Fmt(lowestAt)} limit={limit:F3} pass={lowest >= limit}");

            var hips = rig.Bone(HumanBodyBones.Hips);
            if (hips != null)
            {
                Patch(rig, hips.position, 0.15f);
                Patch(rig, hips.position, 0.20f);
            }
            Groups(rig, b, 0.02f, "slack");
            Groups(rig, b, 0f, "strict");
            Groups(rig, b, 0f, "strict-skeleton", true);
        }

        /// <summary>
        /// Lowest body vertex within a horizontal radius of the hips — the contact patch that has
        /// to rest on the seat, ignoring hair, calves and anything else that hangs off to the side.
        /// </summary>
        static void Patch(AvatarRig rig, Vector3 hips, float radius)
        {
            var baked = new Mesh();
            var lowest = float.PositiveInfinity;
            var at = Vector3.zero;
            var owner = "";
            var bone = "";
            foreach (var smr in rig.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.BakeMesh(baked, true);
                var verts = baked.vertices;
                var t = smr.transform;
                for (var i = 0; i < verts.Length; i++)
                {
                    var w = t.TransformPoint(verts[i]);
                    var d = new Vector2(w.x - hips.x, w.z - hips.z).magnitude;
                    if (d > radius) continue;
                    if (w.y < lowest) { lowest = w.y; at = w; owner = smr.name; bone = Dominant(smr, i); }
                }
            }
            Debug.Log($"[pose] patch r={radius:F2} lowest={lowest:F3} at={Fmt(at)} mesh={owner} bone={bone}");
        }

        /// <summary>
        /// Lowest vertex per owning bone class inside the bench footprint. Separates skeleton-driven
        /// skin (hips, thighs, shins) from secondary/spring bones (skirt, hair) and shows which of
        /// them is actually being pushed through the seat.
        /// </summary>
        static void Groups(AvatarRig rig, Bounds b, float slack, string tag, bool skeletonOnly = false)
        {
            var classes = new System.Collections.Generic.Dictionary<string, string>();
            Add(classes, rig, HumanBodyBones.Hips, "hips");
            Add(classes, rig, HumanBodyBones.LeftUpperLeg, "thighL");
            Add(classes, rig, HumanBodyBones.RightUpperLeg, "thighR");
            Add(classes, rig, HumanBodyBones.LeftLowerLeg, "shinL");
            Add(classes, rig, HumanBodyBones.RightLowerLeg, "shinR");
            Add(classes, rig, HumanBodyBones.LeftFoot, "footL");
            Add(classes, rig, HumanBodyBones.RightFoot, "footR");

            var lows = new System.Collections.Generic.Dictionary<string, (float y, Vector3 at)>();
            var baked = new Mesh();
            foreach (var smr in rig.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.BakeMesh(baked, true);
                var verts = baked.vertices;
                var t = smr.transform;
                for (var i = 0; i < verts.Length; i++)
                {
                    var w = t.TransformPoint(verts[i]);
                    if (Mathf.Abs(w.x - b.center.x) > b.extents.x + slack) continue;
                    if (Mathf.Abs(w.z - b.center.z) > b.extents.z + slack) continue;
                    var name = Dominant(smr, i);
                    var secondary = name.Contains("_Sec_");
                    if (skeletonOnly && secondary) continue;
                    var cls = classes.TryGetValue(name, out var c) ? c : secondary ? "secondary" : "other";
                    if (!lows.TryGetValue(cls, out var cur) || w.y < cur.y) lows[cls] = (w.y, w);
                }
            }
            foreach (var pair in lows)
            {
                Debug.Log($"[pose] {tag} {pair.Key} low={pair.Value.y:F3} at={Fmt(pair.Value.at)}");
            }
        }

        static void Add(System.Collections.Generic.Dictionary<string, string> map, AvatarRig rig, HumanBodyBones bone, string label)
        {
            var t = rig.Bone(bone);
            if (t != null) map[t.name] = label;
        }

        /// <summary>Bone that owns the vertex, so a low vertex can be named as skin, skirt or hair.</summary>
        static string Dominant(SkinnedMeshRenderer smr, int index)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null || smr.bones == null) return "?";
            var weights = mesh.boneWeights;
            if (weights == null || index >= weights.Length) return "?";
            var bw = weights[index];
            var best = bw.weight0;
            var bone = bw.boneIndex0;
            if (bw.weight1 > best) { best = bw.weight1; bone = bw.boneIndex1; }
            if (bw.weight2 > best) { best = bw.weight2; bone = bw.boneIndex2; }
            if (bw.weight3 > best) { best = bw.weight3; bone = bw.boneIndex3; }
            return bone >= 0 && bone < smr.bones.Length && smr.bones[bone] != null ? smr.bones[bone].name : "?";
        }

        /// <summary>
        /// Lift height of the bench surface directly above a point on the avatar, or "open" when
        /// there is nothing overhead. A point under a surface has been pushed through the bench;
        /// a point in open air beside or below the bench is only inside the AABB, not the bench.
        /// </summary>
        static string Above(Vector3 p)
        {
            return Physics.Raycast(p + Vector3.up * 0.003f, Vector3.up, out var hit, 1f)
                ? hit.point.y.ToString("F3")
                : "open";
        }

        static Bounds PoseBounds(AvatarRig rig)
        {
            var hips = rig.Bone(HumanBodyBones.Hips);
            var bounds = new Bounds(hips != null ? hips.position : rig.transform.position, Vector3.zero);
            for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
            {
                var t = rig.Bone(bone);
                if (t != null) bounds.Encapsulate(t.position);
            }
            bounds.Expand(0.26f);   // joints sit inside the mesh; 13 cm of flesh all round
            return bounds;
        }

        static void Shot(Camera cam, string path)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            var bytes = tex.EncodeToPNG();
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);

            File.WriteAllBytes(path, bytes);
            Debug.Log($"[pose] wrote {path} bytes={bytes.Length}");
        }

        static void Eulers(string tag, AvatarRig rig)
        {
            var sb = new StringBuilder();
            foreach (var bone in TableBones)
            {
                var t = rig.Bone(bone);
                if (t == null) continue;
                var e = t.localRotation.eulerAngles;
                sb.Append(' ').Append(bone).Append("=(")
                  .Append(e.x.ToString("0.#")).Append(',')
                  .Append(e.y.ToString("0.#")).Append(',')
                  .Append(e.z.ToString("0.#")).Append(')');
            }
            Debug.Log($"[pose] {tag} local euler{sb}");
        }

        static void Dirs(string tag, AvatarRig rig, SeatEntry s1)
        {
            var fwd = Quaternion.Euler(0f, s1.facingY, 0f) * Vector3.forward;
            var right = Quaternion.Euler(0f, s1.facingY + 90f, 0f) * Vector3.forward;
            Dir(tag + " thighL", rig, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, fwd, right);
            Dir(tag + " shinL", rig, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, fwd, right);
            Dir(tag + " footL", rig, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, fwd, right);
            Dir(tag + " thighR", rig, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, fwd, right);
            Dir(tag + " shinR", rig, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, fwd, right);
            Dir(tag + " upperArmL", rig, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, fwd, right);
            Dir(tag + " foreArmL", rig, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, fwd, right);
            Dir(tag + " upperArmR", rig, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, fwd, right);
            Dir(tag + " foreArmR", rig, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, fwd, right);

            var hand = rig.Bone(HumanBodyBones.LeftHand);
            var knee = rig.Bone(HumanBodyBones.LeftLowerLeg);
            if (hand != null && knee != null)
            {
                Debug.Log($"[pose] {tag} handL={Fmt(hand.position)} kneeL={Fmt(knee.position)}");
            }
        }

        static void Dir(string label, AvatarRig rig, HumanBodyBones from, HumanBodyBones to, Vector3 fwd, Vector3 right)
        {
            var a = rig.Bone(from);
            var b = rig.Bone(to);
            if (a == null || b == null) { Debug.Log($"[pose] {label}: missing bone"); return; }
            var d = b.position - a.position;
            var len = d.magnitude;
            if (len < 1e-4f) { Debug.Log($"[pose] {label}: zero length"); return; }
            var n = d / len;
            Debug.Log($"[pose] {label} n={Fmt(n)} len={len:F3} up={Vector3.Dot(n, Vector3.up):F2} fwd={Vector3.Dot(n, fwd):F2} right={Vector3.Dot(n, right):F2}");
        }

        static string Fmt(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";
    }
}
