using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Proves the imported MiSide room asset is usable inside Unity: geometry counts,
    /// material/texture coverage, bounds. Run headlessly through
    /// tools/unity.ps1 run -- -executeMethod Mas3D.Editor.CliRoom.Inspect.
    /// </summary>
    public static class CliRoom
    {
        /// <summary>Which imported room the editor tools should look at; -room overrides it.</summary>
        public static string RoomPath = "Assets/_Local/room/mita-room.glb";

        /// <summary>Point every capture/inspect/anchors helper at another imported asset.</summary>
        public static void Use(string path)
        {
            if (!string.IsNullOrEmpty(path)) RoomPath = path;
        }

        /// <summary>Batch methods do not receive args, so read -room straight from the command line.</summary>
        public static void UseFromCommandLine()
        {
            var argv = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < argv.Length - 1; i++)
                if (argv[i] == "-room") { Use(argv[i + 1]); return; }
        }

        public static void Inspect()
        {
            UseFromCommandLine();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(RoomPath);
            Debug.Log($"[room] importer={(importer == null ? "null" : importer.GetType().FullName)}");

            var main = AssetDatabase.LoadMainAssetAtPath(RoomPath);
            Debug.Log($"[room] main={(main == null ? "null" : main.GetType().Name)} name={(main == null ? "?" : main.name)}");

            var go = main as GameObject;
            if (go == null)
            {
                Debug.LogError("[room] main asset is not a GameObject");
                EditorApplication.Exit(1);
                return;
            }

            var filters = go.GetComponentsInChildren<MeshFilter>(true);
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            var triangles = 0;
            foreach (var f in filters)
            {
                if (f.sharedMesh != null) triangles += f.sharedMesh.triangles.Length / 3;
            }

            var materials = new HashSet<Material>();
            var withTexture = 0;
            var withoutTexture = 0;
            foreach (var r in renderers)
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    materials.Add(m);
                    if (m.mainTexture != null) withTexture++;
                    else withoutTexture++;
                }
            }

            var bounds = new Bounds(go.transform.position, Vector3.zero);
            var first = true;
            foreach (var r in renderers)
            {
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }

            Debug.Log($"[room] meshes={filters.Length} renderers={renderers.Length} tris={triangles}");
            Debug.Log($"[room] materials={materials.Count} slotsWithTexture={withTexture} slotsWithoutTexture={withoutTexture}");
            Debug.Log($"[room] bounds center={bounds.center.ToString("F2")} size={bounds.size.ToString("F2")}");

            var sb = new StringBuilder();
            foreach (var m in materials)
            {
                sb.Append(m.name);
                sb.Append(m.mainTexture == null ? "(no tex)" : "(" + m.mainTexture.name + ")");
                sb.Append(' ');
            }
            Debug.Log("[room] material list: " + sb);

            EditorApplication.Exit(0);
        }
    }
}
