using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Renders the flat under several lighting rigs in one editor launch, two fixed viewpoints each,
    /// so a night look can be chosen by eye instead of one rebuild per guess.
    /// </summary>
    public static class CliLamp
    {
        public const string Source = "Assets/_Local/room/mita-nodoor.glb";
        public const float Scale = 0.6f;

        static readonly (string name, Vector3 eye, Vector3 look)[] Views =
        {
            ("v1", new Vector3(-1.62f, 1.55f, -0.20f), new Vector3(2.00f, 1.25f, -0.10f)), // bedroom, facing the door
            ("v2", new Vector3(1.20f, 1.55f, 0.20f), new Vector3(9.00f, 1.30f, 0.20f)),   // living room, facing the kitchen
            ("v3", new Vector3(7.20f, 1.55f, 0.10f), new Vector3(11.60f, 1.20f, 0.20f)),  // kitchen, facing the window
            ("v4", new Vector3(-3.10f, 1.55f, 0.20f), new Vector3(-6.00f, 1.10f, 0.60f)), // bathroom, facing the tub
        };

        public static void Sweep()
        {
            var args = Environment.GetCommandLineArgs();
            var spec = Argument(args, "-variants") ?? "a|b|c";

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Source);
            if (prefab == null)
            {
                Debug.LogError("[lamp] missing " + Source);
                EditorApplication.Exit(1);
                return;
            }

            var room = UnityEngine.Object.Instantiate(prefab);
            room.name = "House";
            room.transform.localScale *= Scale;
            RoomLayout.Assemble(room, out var report);
            Debug.Log(report);

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            DumpWorld(room, Path.Combine(shotDir, "house-world.csv"));

            foreach (var entry in spec.Split('|'))
            {
                var rig = Parse(entry);
                HouseLook.Apply(room, rig);
                HouseLook.BuildSkies();
                HouseLook.BridgeFloorGaps(room);
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[lamp] {0} bed={1} liv={2} kit={3} bath={4} moon={5} amb={6} post={7} wb={8}",
                    entry, rig.bedroom, rig.living, rig.kitchen, rig.bath, rig.moon, rig.ambient, rig.post, rig.whiteBalance));

                foreach (var view in Views)
                {
                    var path = Path.Combine(shotDir, "lamp-" + entry + "-" + view.name + ".png");
                    Render(view.eye, view.look, path);
                }
            }

            EditorApplication.Exit(0);
        }

        /// <summary>Runtime world bounds of every visible mesh, so a camera or a lamp can be placed
        /// from measured coordinates instead of guessed ones (the layout moves every run).</summary>
        static void DumpWorld(GameObject room, string path)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("name,minx,miny,minz,maxx,maxy,maxz,cx,cy,cz");
            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                var b = r.bounds;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:F3},{2:F3},{3:F3},{4:F3},{5:F3},{6:F3},{7:F3},{8:F3},{9:F3}",
                    r.gameObject.name, b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z, b.center.x, b.center.y, b.center.z));
            }
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[lamp] world " + path);
        }

        static void Render(Vector3 eye, Vector3 look, string path)
        {
            var go = new GameObject("shot-cam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 68f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 80f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.03f, 0.03f, 0.05f);
            cam.allowHDR = false;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation((look - eye).normalized, Vector3.up));

            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = target;
            cam.Render();
            cam.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(go);
            Debug.Log("[lamp] wrote " + path);
        }

        static HouseLook.Rig Parse(string entry)
        {
            var rig = new HouseLook.Rig();
            foreach (var pair in entry.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2) continue;
                var value = 0f;
                if (!float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) continue;
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "bed": rig.bedroom = value; break;
                    case "liv": rig.living = value; break;
                    case "kit": rig.kitchen = value; break;
                    case "bath": rig.bath = value; break;
                    case "moon": rig.moon = value; break;
                    case "amb": rig.ambient = value; break;
                    case "post": rig.post = value; break;
                    case "con": rig.contrast = value; break;
                    case "sat": rig.saturation = value; break;
                    case "wb": rig.whiteBalance = value; break;
                    case "tint": rig.tint = value; break;
                }
            }
            return rig;
        }

        static string Argument(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
