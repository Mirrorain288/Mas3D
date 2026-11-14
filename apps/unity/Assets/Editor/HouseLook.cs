using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mas3D.Editor
{
    /// <summary>
    /// Night lighting for the multi-room flat. The bedroom rig (one moon plus one lamp) cannot light
    /// an 18 m plan: each room gets its own warm pendant, the moon only comes through the windows,
    /// and the exposure is graded by the same RoomLook volume the game uses.
    /// </summary>
    public static class HouseLook
    {
        public class Rig
        {
            public float bedroom = 1.6f;
            public float living = 2.2f;
            public float kitchen = 1.4f;
            public float bath = 1.2f;
            public float moon = 0.35f;
            public float ambient = 1.0f;
            public float post = RoomLook.PostExposure;
            public float contrast = RoomLook.Contrast;
            public float saturation = RoomLook.Saturation;
            public float whiteBalance = 0f;
            public float tint = 0f;
        }

        public static readonly Color Warm = new Color(1f, 0.84f, 0.68f);
        public static readonly Color Moon = new Color(0.72f, 0.83f, 1f);

        public const string SkyPath = "Assets/_Local/sky/night-sky-2k.jpg";

        /// <summary>
        /// A night sky behind each window facade. Unlit cards, not a skybox: a skybox would be picked
        /// up by the default reflection probe and every glossy surface in the flat would mirror it.
        /// Windows sit at z ≈ -2.7 (bedroom/hall) and z ≈ +1.6 (kitchen).
        /// </summary>
        public static void BuildSkies()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyPath);
            if (texture == null)
            {
                Debug.LogWarning("[look] no sky texture at " + SkyPath);
                return;
            }

            void Card(string name, Vector3 centre, Vector2 size, float yaw)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = name;
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                quad.transform.SetPositionAndRotation(centre, Quaternion.Euler(0f, yaw, 0f));
                quad.transform.localScale = new Vector3(size.x, size.y, 1f);
                var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                mat.SetTexture("_BaseMap", texture);
                mat.SetColor("_BaseColor", new Color(1.15f, 1.15f, 1.28f));
                mat.SetFloat("_Cull", 0f);
                quad.GetComponent<Renderer>().sharedMaterial = mat;
                quad.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // One card per window, not one big plane: a wall-height plane shows through every doorway
            // that looks past a wall and reads as a hole into space.
            Card("sky-bedroom", new Vector3(-1.62f, 1.05f, -3.05f), new Vector2(1.7f, 1.5f), 0f);
            Card("sky-hall-2", new Vector3(2.02f, 1.05f, -3.05f), new Vector2(1.1f, 1.6f), 0f);
            Card("sky-hall-1", new Vector3(4.51f, 1.05f, -3.05f), new Vector2(1.1f, 1.6f), 0f);
            Card("sky-kitchen", new Vector3(10.10f, 1.10f, 1.85f), new Vector2(4.3f, 1.2f), 180f);
        }

        /// <summary>
        /// Floors inside one assembled cluster do not always meet: the bedroom floor stops at
        /// x = 0.39 and the living room floor starts at 0.57, which reads as a black slot under the
        /// doorway. Each gap gets a thin quad using the neighbouring floor's own material, so the
        /// threshold keeps the floor's texture instead of turning into a hole.
        /// </summary>
        public static int BridgeFloorGaps(GameObject room)
        {
            var floors = new System.Collections.Generic.List<Renderer>();
            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                if (!r.name.StartsWith("Floor", System.StringComparison.OrdinalIgnoreCase) &&
                    !r.name.Equals("RoomMain", System.StringComparison.OrdinalIgnoreCase)) continue;
                floors.Add(r);
            }
            floors.Sort((a, b) => a.bounds.min.x.CompareTo(b.bounds.min.x));

            var patches = 0;
            for (var i = 0; i + 1 < floors.Count; i++)
            {
                var left = floors[i].bounds;
                var right = floors[i + 1].bounds;
                var gap = right.min.x - left.max.x;
                if (gap <= 0.005f || gap > 1.0f) continue;

                var zMin = Mathf.Max(left.min.z, right.min.z);
                var zMax = Mathf.Min(left.max.z, right.max.z);
                if (zMax - zMin < 0.2f) continue;

                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "floor-patch-" + patches;
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                var centre = new Vector3((left.max.x + right.min.x) * 0.5f, left.max.y - 0.002f, (zMin + zMax) * 0.5f);
                quad.transform.SetPositionAndRotation(centre, Quaternion.Euler(-90f, 0f, 0f));
                quad.transform.localScale = new Vector3(gap, zMax - zMin, 1f);
                var source = floors[i].sharedMaterial != null ? floors[i].sharedMaterial : floors[i + 1].sharedMaterial;
                quad.GetComponent<Renderer>().sharedMaterial = source;
                patches++;
            }

            if (patches > 0) Debug.Log("[look] floor patches=" + patches);
            return patches;
        }

        /// <summary>
        /// The bedroom's closed door leaf is baked into WallBedroom, so the doorway cannot be opened by
        /// moving a node. Dropping the leaf's own triangles (x just inside the partition, z across the
        /// opening) turns it into an open doorway and the living room becomes visible from the bed.
        /// </summary>
        public static int OpenDoorways(GameObject room)
        {
            var opened = 0;
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.name.StartsWith("WallBedroom", System.StringComparison.OrdinalIgnoreCase)) continue;
                var mesh = filter.sharedMesh;
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                var matrix = filter.transform.localToWorldMatrix;

                // Everything on the bedroom side of the partition past x = 0.40 is the closed leaf and
                // its trim; the partition skin itself sits at x = 0.39 and the living-room skin belongs
                // to WallMain, so this box removes the door without touching either wall face.
                bool IsLeaf(Vector3 p) => p.x > 0.40f && p.z > -1.20f && p.z < 0.90f && p.y < 2.10f;

                var kept = new System.Collections.Generic.List<int>(triangles.Length);
                var dropped = 0;
                for (var t = 0; t < triangles.Length; t += 3)
                {
                    var a = matrix.MultiplyPoint3x4(vertices[triangles[t]]);
                    var b = matrix.MultiplyPoint3x4(vertices[triangles[t + 1]]);
                    var c = matrix.MultiplyPoint3x4(vertices[triangles[t + 2]]);
                    if (IsLeaf((a + b + c) / 3f))
                    {
                        dropped++;
                        continue;
                    }
                    kept.Add(triangles[t]);
                    kept.Add(triangles[t + 1]);
                    kept.Add(triangles[t + 2]);
                }

                if (dropped == 0) continue;
                var rebuilt = Object.Instantiate(mesh);
                rebuilt.name = mesh.name + "-doorway";
                rebuilt.SetTriangles(kept.ToArray(), 0);
                rebuilt.RecalculateBounds();
                filter.sharedMesh = rebuilt;
                opened++;
                Debug.Log($"[look] doorway cut {filter.name}: dropped {dropped} of {triangles.Length / 3} triangles");
            }
            return opened;
        }

        public static void Apply(GameObject room, Rig rig)
        {
            var old = GameObject.Find("HouseLights");
            if (old != null) Object.DestroyImmediate(old);

            var root = new GameObject("HouseLights");

            void Lamp(string name, Vector3 at, float intensity, float range, Color colour)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                go.transform.position = at;
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = colour;
                light.intensity = intensity;
                light.range = range;
                // No shadows on the pendants: a 2 m room with a point light 30 cm under the ceiling
                // throws the whole wall into hard blobs, and the moonlight already draws the shadows
                // that give the room depth.
                light.shadows = LightShadows.None;
                light.shadowStrength = 0f;
                light.bounceIntensity = 1f;
            }

            // Measured after the floor-abutting layout (Shots/house-world.csv):
            // bath x -5.97..-3.81, bedroom -3.63..0.39, living 0.57..5.97, kitchen 6.61..12.01.
            Lamp("bath-lamp", new Vector3(-4.89f, 2.05f, 0.20f), rig.bath, 5f, Warm);
            Lamp("bedroom-lamp", new Vector3(-1.62f, 2.60f, -0.20f), rig.bedroom, 7f, Warm);
            Lamp("living-lamp-a", new Vector3(1.80f, 2.60f, 0.10f), rig.living * 0.6f, 7f, Warm);
            Lamp("living-lamp-b", new Vector3(4.60f, 2.60f, 0.10f), rig.living * 0.6f, 7f, Warm);
            Lamp("kitchen-lamp", new Vector3(9.30f, 2.05f, 0.00f), rig.kitchen, 8f, Warm);

            var moonGo = new GameObject("moon");
            moonGo.transform.SetParent(root.transform, false);
            // The flat's windows all face -z, so the moon comes in from that side.
            moonGo.transform.position = new Vector3(1.2f, 5.5f, -8f);
            moonGo.transform.rotation = Quaternion.Euler(26f, 0f, 0f);
            var moon = moonGo.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = Moon;
            moon.intensity = rig.moon;
            moon.shadows = LightShadows.Soft;
            moon.shadowStrength = 0.9f;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.26f, 0.27f, 0.36f) * rig.ambient;
            RenderSettings.ambientEquatorColor = new Color(0.24f, 0.20f, 0.24f) * rig.ambient;
            RenderSettings.ambientGroundColor = new Color(0.12f, 0.10f, 0.14f) * rig.ambient;
            RenderSettings.fog = false;

            RoomLook.Apply(rig.post, rig.saturation, rig.contrast, RoomLook.Vignette,
                RoomLook.BloomIntensity, null, rig.whiteBalance, rig.tint);
        }
    }
}
