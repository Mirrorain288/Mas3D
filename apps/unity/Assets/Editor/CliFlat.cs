using System.IO;
using Mas3D.Room;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Bakes the whole-apartment export (帽子米塔的房间.blend, rooms 卧室/客厅/餐厅/浴室/柜子) into one
    /// prefab: scales uniformly by <see cref="FlatScale"/>, reorganizes the three interior doors around
    /// their +Z hinges with <see cref="InteractiveDoor"/>, attaches persistent double-sided colliders and lighting.
    /// </summary>
    public static class CliFlat
    {
        public const string Source = "Assets/_Local/room/mita-flat.glb";
        public const string Output = "Assets/_Local/room/Flat.prefab";
        public const float FlatScale = 1.5f;
        const string ScenePath = "Assets/Scenes/Main.unity";

        public static void Bake()
        {
            Flipped.Clear();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(Source);
            if (prefabAsset == null)
            {
                Debug.LogError("[flat] cannot load " + Source);
                EditorApplication.Exit(1);
                return;
            }

            var room = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            if (PrefabUtility.IsPartOfPrefabInstance(room))
            {
                PrefabUtility.UnpackPrefabInstance(room, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }
            room.name = "Flat";

            // Sit the floor on y = 0 and centre the footprint on the origin, then scale uniformly by
            // FlatScale so doorways (~0.915 m x 1.95 m) and ceilings (~2.655 m) fit the standing player.
            var bounds = Measure(room);

            // The export carries window/backdrop planes well below the rooms, so bounds.min.y is not
            // the floor. Take the biggest horizontal mesh instead: that is the living-room floor.
            var floorY = DominantFloorY(room);
            var shift = new Vector3(-bounds.center.x, -floorY, -bounds.center.z);

            // Offset and scale the children, not the root: the root stays at the origin with unit scale
            // so pivot local rotations and spawn coordinates stay in plain world metres.
            foreach (Transform child in room.transform)
            {
                child.position = (child.position + shift) * FlatScale;
                child.localScale *= FlatScale;
            }
            bounds = Measure(room);
            Debug.Log($"[flat] bounds center={bounds.center} size={bounds.size} scale={FlatScale:F2}");

            var doorCount = SetupDoors(room);
            if (doorCount != InteractiveDoor.InteriorSpecs.Length)
            {
                Debug.LogError($"[flat] expected {InteractiveDoor.InteriorSpecs.Length} interior doors, built {doorCount}");
                EditorApplication.Exit(1);
                return;
            }

            var colliders = AttachColliders(room);
            var lamps = Light(room, bounds);
            var shell = room.AddComponent<HouseLighting>();
            shell.Ambient = 1f;

            var dir = Path.GetDirectoryName(Output);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!SavePrefabWithFlippedSubAssets(room))
            {
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[flat] baked {Output} doors={doorCount} colliders={colliders} lamps={lamps} bounds={bounds.size}");
            EditorApplication.Exit(0);
        }

        static int SetupDoors(GameObject room)
        {
            var created = 0;
            for (var i = 0; i < InteractiveDoor.InteriorSpecs.Length; i++)
            {
                var spec = InteractiveDoor.InteriorSpecs[i];
                var leaf = FindChildRecursive(room.transform, spec.LeafMesh);
                var handle = FindChildRecursive(room.transform, spec.HandleMesh);
                if (leaf == null || handle == null)
                {
                    Debug.LogError($"[flat] missing mesh for {spec.PivotName}: leaf={spec.LeafMesh} ({leaf != null}) handle={spec.HandleMesh} ({handle != null})");
                    return created;
                }

                var leafBounds = Measure(leaf.gameObject);
                var hingePos = new Vector3(leafBounds.center.x, leafBounds.min.y, leafBounds.max.z);

                var pivot = new GameObject(spec.PivotName);
                pivot.transform.SetParent(room.transform, false);
                pivot.transform.position = hingePos;
                pivot.transform.localRotation = Quaternion.identity;
                pivot.transform.localScale = Vector3.one;

                leaf.SetParent(pivot.transform, true);
                handle.SetParent(pivot.transform, true);

                var door = pivot.AddComponent<InteractiveDoor>();
                door.DoorId = spec.PivotName;
                door.SwingAngle = spec.SwingAngle;
                door.SwingDuration = InteractiveDoor.DefaultSwingDuration;

                Debug.Log($"[flat] door {spec.PivotName} hinge={hingePos} leafSize={leafBounds.size}");
                created++;
            }
            return created;
        }

        static Transform FindChildRecursive(Transform root, string targetName)
        {
            if (root == null) return null;
            if (string.Equals(root.name, targetName, System.StringComparison.Ordinal)) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var hit = FindChildRecursive(root.GetChild(i), targetName);
                if (hit != null) return hit;
            }
            return null;
        }

        static bool SavePrefabWithFlippedSubAssets(GameObject room)
        {
            // Persist flipped (inner) meshes as sub-assets inside Flat.prefab so MeshColliders survive reload.
            PrefabUtility.SaveAsPrefabAsset(room, Output);
            foreach (var mesh in Flipped.Values)
            {
                if (mesh != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh)))
                {
                    AssetDatabase.AddObjectToAsset(mesh, Output);
                }
            }
            AssetDatabase.SaveAssets();
            PrefabUtility.SaveAsPrefabAsset(room, Output);
            AssetDatabase.SaveAssets();

            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(Output);
            if (reloaded == null)
            {
                Debug.LogError("[flat] failed to reload baked prefab: " + Output);
                return false;
            }
            var nullColliders = 0;
            foreach (var mc in reloaded.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc.sharedMesh == null) nullColliders++;
            }
            if (nullColliders > 0)
            {
                Debug.LogError($"[flat] {nullColliders} MeshCollider(s) in {Output} have null sharedMesh after save");
                return false;
            }
            var doors = reloaded.GetComponentsInChildren<InteractiveDoor>(true);
            if (doors.Length != InteractiveDoor.InteriorSpecs.Length)
            {
                Debug.LogError($"[flat] expected {InteractiveDoor.InteriorSpecs.Length} InteractiveDoor(s) in {Output}, found {doors.Length}");
                return false;
            }
            foreach (var door in doors)
            {
                var doorRenderers = door.GetComponentsInChildren<Renderer>(true).Length;
                var doorColliders = door.GetComponentsInChildren<MeshCollider>(true).Length;
                if (doorRenderers < 2 || doorColliders < 4)
                {
                    Debug.LogError($"[flat] {door.DoorId} missing children after save: renderers={doorRenderers} colliders={doorColliders}");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Every mesh gets a collider plus a reversed copy. The flat's meshes are named mesh_###, so
        /// the Floor*/Wall* prefix rule RoomColliders uses for the other imports cannot tell a shell
        /// from a chair; flipping all of them costs nothing at bake time and keeps the player inside.
        /// </summary>
        static int AttachColliders(GameObject room)
        {
            var count = 0;
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                var mesh = filter.gameObject.AddComponent<MeshCollider>();
                mesh.sharedMesh = filter.sharedMesh;
                mesh.convex = false;
                count++;

                if (filter.transform.Find("inner-collider") != null) continue;
                var innerMesh = Flip(filter.sharedMesh);
                var inner = new GameObject("inner-collider");
                inner.transform.SetParent(filter.transform, false);
                var collider = inner.AddComponent<MeshCollider>();
                collider.sharedMesh = innerMesh;
                collider.convex = false;
            }
            return count;
        }

        static readonly System.Collections.Generic.Dictionary<string, Mesh> Flipped =
            new System.Collections.Generic.Dictionary<string, Mesh>();

        static Mesh Flip(Mesh source)
        {
            if (Flipped.TryGetValue(source.name, out var cached) && cached != null) return cached;
            var mesh = Object.Instantiate(source);
            mesh.name = source.name + " (inner)";
            var triangles = mesh.triangles;
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                var swap = triangles[i];
                triangles[i] = triangles[i + 2];
                triangles[i + 2] = swap;
            }
            mesh.triangles = triangles;
            var normals = mesh.normals;
            for (var i = 0; i < normals.Length; i++) normals[i] = -normals[i];
            if (normals.Length > 0) mesh.normals = normals;
            mesh.RecalculateBounds();
            Flipped[source.name] = mesh;
            return mesh;
        }

        /// <summary>
        /// Five ceiling lamps spread along the flat plus one moon through the window side. The rooms
        /// run along x, so even spacing is the honest first pass; the per-room positions come from
        /// the render, not from the source file (its collections overlap in bounding box).
        /// </summary>
        static int Light(GameObject room, Bounds bounds)
        {
            var holder = new GameObject("FlatLights");
            holder.transform.SetParent(room.transform, false);

            var warm = new Color(1f, 0.85f, 0.7f);
            const int lamps = 5;
            var margin = 1.2f * FlatScale;
            var inner = bounds.size.x - margin * 2f;
            for (var i = 0; i < lamps; i++)
            {
                var t = lamps == 1 ? 0.5f : i / (float)(lamps - 1);
                var x = bounds.min.x + margin + inner * t;
                var go = new GameObject("flat-lamp-" + (i + 1));
                go.transform.SetParent(holder.transform, false);
                // 1.5x scaled ceilings sit near 2.655 m; lamps placed below them illuminate all rooms.
                go.transform.position = new Vector3(x, 1.40f * FlatScale, bounds.center.z);
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = warm;
                light.intensity = 2.1f;
                light.range = 7.5f * FlatScale;
                light.shadows = LightShadows.None;
            }

            var moonGo = new GameObject("flat-moon");
            moonGo.transform.SetParent(holder.transform, false);
            moonGo.transform.position = new Vector3(bounds.center.x, 6f * FlatScale, bounds.min.z - 4f * FlatScale);
            moonGo.transform.rotation = Quaternion.Euler(26f, 0f, 0f);
            var moon = moonGo.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = new Color(0.72f, 0.83f, 1f);
            moon.intensity = 0.45f;
            moon.shadows = LightShadows.Soft;
            moon.shadowStrength = 0.9f;

            return lamps + 1;
        }

        /// <summary>
        /// Puts the baked prefab into Main.unity as RoomBootstrap.FlatRoom (left inactive), so the
        /// built game can walk the flat with -flat 1 and every existing capture keeps the bedroom.
        /// </summary>
        public static void Wire()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[flat] cannot open " + ScenePath); EditorApplication.Exit(1); return; }

            if (!WireIntoScene(scene))
            {
                Debug.LogError("[flat] missing bootstrap or prefab");
                EditorApplication.Exit(1);
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            Debug.Log($"[flat] wired {Output} into Main.unity spawn={boot.FlatSpawn}");
            EditorApplication.Exit(0);
        }

        public static bool WireIntoScene(UnityEngine.SceneManagement.Scene scene)
        {
            if (!scene.IsValid()) return false;
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Output);
            if (boot == null || prefab == null) return false;

            if (boot.FlatRoom != null) Object.DestroyImmediate(boot.FlatRoom);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = "Flat";
            go.transform.position = Vector3.zero;
            go.SetActive(false);
            boot.FlatRoom = go;
            boot.FlatSpawn = new Vector3(-3.15f * FlatScale, 0.10f, 0.20f * FlatScale);
            boot.FlatYaw = 90f;
            EditorUtility.SetDirty(boot);
            return true;
        }

        /// <summary>Player-eye frame inside the flat, four rooms in a row.</summary>
        public static void Shot()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("[flat] cannot open " + ScenePath);
                EditorApplication.Exit(1);
                return;
            }

            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Output);
            if (boot == null || prefab == null)
            {
                Debug.LogError("[flat] missing bootstrap or prefab");
                EditorApplication.Exit(1);
                return;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = "Flat(runtime)";
            go.transform.position = Vector3.zero;
            go.SetActive(false);
            boot.FlatRoom = go;
            boot.ShowFlat();
            boot.Boot();

            var camera = boot.OutputCamera;
            if (camera == null)
            {
                Debug.LogError("[flat] no output camera");
                EditorApplication.Exit(1);
                return;
            }
            foreach (var r in boot.Player.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            var views = new (string name, Vector3 eye, Vector3 look)[]
            {
                ("flat-bed", new Vector3(-3.20f, 1.42f, 0.95f) * FlatScale, new Vector3(0.60f, 1.15f, -0.40f) * FlatScale),
                ("flat-living", new Vector3(-0.60f, 1.50f, 1.30f) * FlatScale, new Vector3(4.20f, 1.20f, -0.60f) * FlatScale),
                ("flat-dining", new Vector3(5.00f, 1.45f, 2.60f) * FlatScale, new Vector3(7.40f, 1.15f, -0.80f) * FlatScale),
                ("flat-kitchen", new Vector3(4.95f, 1.45f, 2.60f) * FlatScale, new Vector3(7.40f, 1.15f, -1.40f) * FlatScale),
                ("flat-bath", new Vector3(-5.55f, 1.45f, 0.70f) * FlatScale, new Vector3(-7.60f, 1.10f, -0.60f) * FlatScale),
                ("flat-wide", new Vector3(-1.00f, 8.00f, 9.50f) * FlatScale, new Vector3(-0.20f, 1.00f, 0.00f) * FlatScale),
                ("flat-player", boot.Player.transform.position + Vector3.up * boot.Player.EyeHeight,
                    boot.Player.transform.position + Vector3.up * 1.35f + Vector3.right * (3f * FlatScale)),
            };

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            camera.fieldOfView = 68f;

            foreach (var view in views)
            {
                camera.transform.SetPositionAndRotation(view.eye, Quaternion.LookRotation((view.look - view.eye).normalized));
                camera.Render();
                camera.Render();
                RenderTexture.active = target;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                var outPath = Path.Combine(shotDir, view.name + ".png");
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
                Debug.Log($"[flat] wrote {outPath}");
            }

            Debug.Log($"[flat] colliders={boot.ColliderCount} player={boot.Player.transform.position}");
            EditorApplication.Exit(0);
        }

        /// <summary>Writes every visible renderer's world box, for placing the camera and lamps.</summary>
        public static void Map()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Output);
            if (prefab == null) { EditorApplication.Exit(1); return; }
            var room = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            room.transform.position = Vector3.zero;

            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "flat-world.csv");
            var lines = new System.Collections.Generic.List<string> { "name,minx,miny,minz,maxx,maxy,maxz,cx,cy,cz" };
            var floors = new System.Collections.Generic.List<string>();
            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                var b = r.bounds;
                lines.Add($"{r.name},{b.min.x:F2},{b.min.y:F2},{b.min.z:F2},{b.max.x:F2},{b.max.y:F2},{b.max.z:F2},{b.center.x:F2},{b.center.y:F2},{b.center.z:F2}");
                var area = b.size.x * b.size.z;
                if (b.size.y < 0.35f && area > 1.5f) floors.Add($"[floor] {r.name} x {b.min.x:F2}..{b.max.x:F2} z {b.min.z:F2}..{b.max.z:F2} y {b.min.y:F2}..{b.max.y:F2} area {area:F1}");
            }
            File.WriteAllLines(path, lines);
            foreach (var f2 in floors) Debug.Log(f2);
            Debug.Log($"[flat] map rows={lines.Count - 1} -> {path}");
            EditorApplication.Exit(0);
        }

        /// <summary>Top of the largest horizontal surface: the floor everything else stands on.</summary>
        static float DominantFloorY(GameObject room)
        {
            var best = float.MinValue;
            var bestArea = 0f;
            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                var b = r.bounds;
                if (b.size.y > 0.35f) continue;
                var area = b.size.x * b.size.z;
                if (area <= bestArea) continue;
                bestArea = area;
                best = b.max.y;
            }
            return bestArea > 0f ? best : Measure(room).min.y;
        }

        static Bounds Measure(GameObject room)
        {
            var renderers = room.GetComponentsInChildren<Renderer>(true);
            var bounds = new Bounds(room.transform.position, Vector3.zero);
            var first = true;
            foreach (var r in renderers)
            {
                if (!r.enabled) continue;
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }
            return bounds;
        }
    }
}
