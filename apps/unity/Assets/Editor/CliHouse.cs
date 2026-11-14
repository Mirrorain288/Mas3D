using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Bakes the exploded flat export into one prefab the game can load: the source .glb keeps the
    /// rooms laid out hundreds of metres apart (the authoring tool's own layout), which is useless at
    /// runtime. This assembles them, gives the shell two-sided colliders and saves the result.
    /// </summary>
    public static class CliHouse
    {
        public const string Source = "Assets/_Local/room/mita-nodoor.glb";
        public const string Output = "Assets/_Local/room/House.prefab";

        /// <summary>The flat is authored 1.67x the .blend bedroom; 0.6 brings it back to metres.</summary>
        public const float Scale = 0.6f;


        const string ScenePath = "Assets/Scenes/Main.unity";

        /// <summary>
        /// Boots the game scene with the flat swapped in at runtime (no scene edit) and writes one
        /// frame from the player's camera. This is the check that the flat is walkable: colliders
        /// attach, the player lands in the bedroom, and the camera looks through the doorway.
        /// </summary>
        public static void Shot()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("[house] cannot open " + ScenePath);
                EditorApplication.Exit(1);
                return;
            }

            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Output);
            if (boot == null || prefab == null)
            {
                Debug.LogError("[house] missing bootstrap or prefab");
                EditorApplication.Exit(1);
                return;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = "HouseRoom(runtime)";
            go.transform.position = Vector3.zero;
            go.SetActive(false);
            boot.HouseRoom = go;
            boot.Boot();

            var camera = boot.OutputCamera;
            if (camera == null)
            {
                Debug.LogError("[house] no output camera");
                EditorApplication.Exit(1);
                return;
            }

            // Cinemachine owns the real camera at runtime; an editor-side render has no update loop,
            // so place the output camera by hand at eye height looking through the bedroom door.
            var yaw = ParseFloat("-yaw", 90f);
            var eye = boot.Player.transform.position + Vector3.up * 1.55f;
            camera.transform.SetPositionAndRotation(eye, Quaternion.Euler(3f, yaw, 0f));

            // First-person frame: the rig normally hides the head, and an editor render has no rig
            // update to do it, so the body would fill the screen from the inside.
            foreach (var r in boot.Player.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            camera.Render();
            camera.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            var outPath = Path.Combine(shotDir, ParseString("-shot", "house-player") + ".png");
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log($"[house] wrote {outPath} colliders={boot.ColliderCount} player={boot.Player.transform.position}");
            EditorApplication.Exit(0);
        }


        /// <summary>
        /// Kept only as a reference for the old three-lamp rig; the live lighting is
        /// HouseLook.Apply, which is baked into the prefab by Bake().
        /// </summary>
        static void LightTheFlatLegacy(GameObject room)
        {
            var shade = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var holder = new GameObject("HouseLights");

            void Lamp(string name, Vector3 at, float intensity, float range, Color colour)
            {
                var go = new GameObject(name);
                go.transform.SetParent(holder.transform, false);
                go.transform.position = at;
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = colour;
                light.intensity = intensity;
                light.range = range;
                light.shadows = LightShadows.Soft;
            }

            // Measured from the assembled flat: bedroom -7..-2.9, living room -2.9..2.7, kitchen 4..9.4.
            Lamp("bedroom-lamp", new Vector3(-4.95f, 2.55f, 0.2f), 1.7f, 7f, new Color(1f, 0.84f, 0.68f));
            Lamp("living-lamp", new Vector3(0.2f, 2.55f, 0.4f), 2.0f, 9f, new Color(1f, 0.86f, 0.72f));
            Lamp("kitchen-lamp", new Vector3(7.2f, 2.1f, 0.1f), 1.5f, 8f, new Color(1f, 0.88f, 0.78f));

            var moon = new GameObject("moon");
            moon.transform.SetParent(holder.transform, false);
            moon.transform.position = new Vector3(12f, 6f, -2f);
            moon.transform.rotation = Quaternion.Euler(24f, -62f, 0f);
            var sun = moon.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(0.74f, 0.84f, 1f);
            sun.intensity = 0.45f;
            sun.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.22f, 0.22f, 0.28f);
            RenderSettings.ambientEquatorColor = new Color(0.20f, 0.17f, 0.20f);
            RenderSettings.ambientGroundColor = new Color(0.10f, 0.09f, 0.12f);
            Object.DestroyImmediate(shade);
            Debug.Log("[house] lamps=4 (bedroom/living/kitchen/moon)");
        }

        /// <summary>
        /// Moves the lights and cards HouseLook spawns at the scene root under the house root, so they
        /// save into the prefab instead of being left behind in the scene.
        /// </summary>
        /// <summary>
        /// Reports the door leaf inside WallBedroom: the partition and the closed leaf are one mesh,
        /// so the only way to open the room up is to drop the leaf's triangles. This prints the
        /// leaf's own rectangle so the cut stays tight.
        /// </summary>
        public static void ProbeWall()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Output);
            var room = Object.Instantiate(prefab);
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.name.StartsWith("WallBedroom")) continue;
                var mesh = filter.sharedMesh;
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                var matrix = filter.transform.localToWorldMatrix;
                for (var t = 0; t < triangles.Length; t += 3)
                {
                    var a = matrix.MultiplyPoint3x4(vertices[triangles[t]]);
                    var b = matrix.MultiplyPoint3x4(vertices[triangles[t + 1]]);
                    var c = matrix.MultiplyPoint3x4(vertices[triangles[t + 2]]);
                    var centre = (a + b + c) / 3f;
                    if (centre.x < 0.30f) continue;
                    var normal = Vector3.Cross(b - a, c - a).normalized;
                    Debug.Log($"[wall] tri z={centre.z:F2} y={centre.y:F2} x={centre.x:F2} n=({normal.x:F2},{normal.y:F2},{normal.z:F2})");
                }
            }
            EditorApplication.Exit(0);
        }

        /// <summary>Names whatever the player is looking at, so a stray mesh can be identified by hit
        /// rather than guessed from bounds.</summary>
        public static void ProbeRay()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Output);
            var go = Object.Instantiate(prefab);
            var origin = new Vector3(-1.62f, 1.55f, -0.30f);
            foreach (var yaw in new[] { 90f, 60f, 120f })
            {
                var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                var hits = Physics.RaycastAll(origin, dir, 8f);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                    Debug.Log($"[ray] yaw={yaw} d={hit.distance:F2} hit={hit.collider.name} at={hit.point}");
            }
            EditorApplication.Exit(0);
        }

        static string ParseString(string key, string fallback)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return fallback;
        }

        static float ParseFloat(string key, float fallback)
        {
            var raw = ParseString(key, null);
            return raw != null && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }

        static void AdoptSceneRoots(GameObject room)
        {
            var scene = room.scene;
            foreach (var root in scene.GetRootGameObjects().ToArray())
            {
                if (root == room) continue;
                root.transform.SetParent(room.transform, true);
            }
        }

        public static void Bake()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Source);
            if (prefab == null)
            {
                Debug.LogError("[house] missing " + Source);
                EditorApplication.Exit(1);
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var room = Object.Instantiate(prefab);
            room.name = "House";
            room.transform.localScale *= Scale;

            var bounds = RoomLayout.Assemble(room, out var report);
            Debug.Log(report);

            var colliders = RoomColliders.Attach(room);
            var bedroom = RoomLayout.LastBedroom;
            var spawn = new Vector3(bedroom.center.x, bedroom.min.y + 0.1f, bedroom.center.z);

            // Bake the lighting into the prefab: lights and window cards are ordinary objects, and a
            // prefab that ships without them spawns a black flat. RenderSettings is scene state, so a
            // small runtime component restores the ambient when the prefab awakes.
            var look = new HouseLook.Rig();
            HouseLook.Apply(room, look);
            HouseLook.BuildSkies();
            HouseLook.BridgeFloorGaps(room);
            AdoptSceneRoots(room);
            room.AddComponent<HouseLighting>().Ambient = look.ambient;

            PrefabUtility.SaveAsPrefabAsset(room, Output);
            AssetDatabase.SaveAssets();

            Debug.Log($"[house] baked {Output} colliders={colliders} " +
                      $"bounds=({bounds.size.x:F2},{bounds.size.y:F2},{bounds.size.z:F2}) " +
                      $"bedroom=({bedroom.size.x:F2},{bedroom.size.z:F2}) spawn=({spawn.x:F2},{spawn.y:F2},{spawn.z:F2})");
            EditorApplication.Exit(0);
        }
    }
}