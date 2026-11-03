using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Imports the VRM avatars that live under Assets/_Local/avatars and reports what the
    /// importer produced: main asset type, skinned meshes, blend shapes, materials and shaders.
    ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliAvatar.Inspect
    /// </summary>
    public static class CliAvatar
    {
        public const string AvatarDir = "Assets/_Local/avatars";

        public static void Inspect()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            if (!Directory.Exists(AvatarDir))
            {
                Debug.LogError("[avatar] missing dir " + AvatarDir);
                EditorApplication.Exit(1);
                return;
            }

            var files = Directory.GetFiles(AvatarDir, "*.vrm", SearchOption.TopDirectoryOnly);
            Debug.Log("[avatar] found " + files.Length + " vrm file(s)");
            foreach (var file in files)
            {
                var path = file.Replace('\\', '/');
                var importer = AssetImporter.GetAtPath(path);
                var main = AssetDatabase.LoadAssetAtPath<Object>(path);
                Debug.Log($"[avatar] {Path.GetFileName(path)} importer={importer?.GetType().Name ?? "null"} main={main?.GetType().Name ?? "null"}");
                var sub = AssetDatabase.LoadAllAssetsAtPath(path);
                Debug.Log("[avatar]   sub-assets: " + string.Join(", ", sub.Where(a => a != null).Select(a => a.GetType().Name).GroupBy(t => t).Select(g => g.Key + "x" + g.Count())));
                if (main is GameObject go)
                {
                    Report(go);
                }
            }

            EditorApplication.Exit(0);
        }

        static void Report(GameObject prefab)
        {
            var smrs = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var tris = 0;
            var positionCount = 0;
            foreach (var smr in smrs)
            {
                if (smr.sharedMesh == null) continue;
                tris += smr.sharedMesh.triangles.Length / 3;
                positionCount += smr.sharedMesh.vertexCount;
            }
            var bones = new HashSet<Transform>();
            foreach (var smr in smrs)
            {
                if (smr.bones == null) continue;
                foreach (var b in smr.bones) if (b != null) bones.Add(b);
            }
            var blendShapes = smrs.Where(s => s.sharedMesh != null).SelectMany(s => Enumerable.Range(0, s.sharedMesh.blendShapeCount).Select(i => s.name + ":" + s.sharedMesh.GetBlendShapeName(i))).ToList();

            Debug.Log($"[avatar]   skinnedMeshes={smrs.Length} tris={tris} verts={positionCount} bones={bones.Count} blendShapes={blendShapes.Count}");
            Debug.Log("[avatar]   boneSample: " + string.Join(", ", bones.Select(b => b.name).OrderBy(n => n).Take(12)));

            var materials = smrs.Where(s => s.sharedMaterials != null).SelectMany(s => s.sharedMaterials).Where(m => m != null).Distinct().ToList();
            var shaders = materials.GroupBy(m => m.shader != null ? m.shader.name : "null").Select(g => g.Key + "x" + g.Count()).ToList();
            Debug.Log($"[avatar]   materials={materials.Count} shaders: {string.Join(", ", shaders)}");
            Debug.Log("[avatar]   materialNames: " + string.Join(", ", materials.Take(10).Select(m => m.name)));

            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            var bounds = new Bounds();
            var first = true;
            foreach (var r in renderers)
            {
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }
            Debug.Log($"[avatar]   renderers={renderers.Length} bounds size={bounds.size:F2} center={bounds.center:F2}");
            Debug.Log("[avatar]   components: " + string.Join(", ", prefab.GetComponentsInChildren<Component>(true)
                .Select(c => c == null ? "null" : c.GetType().Name)
                .GroupBy(n => n).Select(g => g.Key + "x" + g.Count()).OrderByDescending(s => s)));
        }

        public static void ShaderProbe()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            var names = new[]
            {
                "VRM10/MToon10",
                "VRM10/Universal Render Pipeline/MToon10",
                "Universal Render Pipeline/Lit",
                "UniGLTF/UniUnlit",
            };
            foreach (var name in names)
            {
                var s = Shader.Find(name);
                Debug.Log($"[probe] {name} -> {(s == null ? "NULL" : s.name + " supported=" + s.isSupported)}");
            }
            var gs = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
            Debug.Log("[probe] defaultRenderPipeline=" + (gs == null ? "null" : gs.name + " (" + gs.GetType().Name + ")"));
            var shaderAsset = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.vrmc.vrm/MToon10/Shaders/vrmc_materials_mtoon_urp.shader");
            Debug.Log("[probe] urp shader asset=" + (shaderAsset == null ? "null" : shaderAsset.name));
            var builtinAsset = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.vrmc.vrm/MToon10/Shaders/vrmc_materials_mtoon.shader");
            Debug.Log("[probe] builtin shader asset=" + (builtinAsset == null ? "null" : builtinAsset.name));
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// ScriptedImporter runs in a parallel import worker, and Shader.Find there only sees
        /// shaders on the always-included list, so the VRM importer would build null materials.
        /// </summary>
        static int AddAlwaysIncluded(params string[] shaderNames)
        {
            var settings = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(settings);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            var added = 0;
            foreach (var name in shaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null) { Debug.LogError("[avatar] shader not found: " + name); continue; }
                var present = false;
                for (var i = 0; i < list.arraySize; i++)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) { present = true; break; }
                }
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                added++;
            }
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return added;
        }

        public static void Reimport()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            var added = AddAlwaysIncluded("VRM10/MToon10", "VRM10/Universal Render Pipeline/MToon10");
            Debug.Log("[avatar] always-included shaders added=" + added);

            foreach (var file in Directory.GetFiles(AvatarDir, "*.vrm"))
            {
                var path = file.Replace('\\', '/');
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var main = AssetDatabase.LoadAssetAtPath<Object>(path);
                Debug.Log($"[avatar] reimported {Path.GetFileName(path)} main={main?.GetType().Name ?? "null"}");
                if (main is GameObject go) Report(go);
            }
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Renders each avatar on a neutral backdrop so the imported materials can be eyeballed.
        /// Needs a GPU: never pass -nographics.
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliAvatar.Shot
        /// </summary>
        public static void Shot()
        {
            var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(outDir);

            foreach (var file in Directory.GetFiles(AvatarDir, "*.vrm"))
            {
                var path = file.Replace('\\', '/');
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.LogError("[avatar] not imported: " + path); continue; }

                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                var bounds = new Bounds();
                var first = true;
                foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
                {
                    if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds);
                }

                var key = new GameObject("key").AddComponent<Light>();
                key.type = LightType.Directional;
                key.color = new Color(1f, 0.97f, 0.93f);
                key.intensity = 1.15f;
                key.transform.rotation = Quaternion.Euler(24f, -28f, 0f);
                key.shadows = LightShadows.Soft;

                var fill = new GameObject("fill").AddComponent<Light>();
                fill.type = LightType.Directional;
                fill.color = new Color(0.62f, 0.72f, 0.95f);
                fill.intensity = 0.5f;
                fill.transform.rotation = Quaternion.Euler(12f, 148f, 0f);

                var rim = new GameObject("rim").AddComponent<Light>();
                rim.type = LightType.Directional;
                rim.color = new Color(1f, 0.85f, 0.82f);
                rim.intensity = 0.55f;
                rim.transform.rotation = Quaternion.Euler(-18f, 196f, 0f);

                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.44f, 0.47f, 0.58f);
                RenderSettings.ambientEquatorColor = new Color(0.34f, 0.32f, 0.38f);
                RenderSettings.ambientGroundColor = new Color(0.16f, 0.15f, 0.18f);

                var camGo = new GameObject("cam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.09f, 0.08f, 0.1f);
                cam.fieldOfView = 32f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;
#if MAS3D_URP
                camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
#endif

                var name = Path.GetFileNameWithoutExtension(path);
                foreach (var angle in new[] { 0f, 34f })
                {
                    // VRM 1.0 avatars face +z in Unity, so the camera sits on the +z side.
                    // Fit both the vertical and the horizontal extent (T-pose arms are wide).
                    var tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    var tanH = tanV * ((float)720 / 960);
                    var radius = Mathf.Max(bounds.extents.y / tanV, bounds.extents.x / tanH) * 1.12f;
                    var look = new Vector3(bounds.center.x, bounds.center.y, bounds.center.z);
                    var offset = Quaternion.Euler(0f, angle, 0f) * new Vector3(0f, 0f, radius);
                    cam.transform.position = look + offset + new Vector3(0f, 0.12f, 0f);
                    cam.transform.LookAt(look);

                    var rt = new RenderTexture(720, 960, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(720, 960, TextureFormat.RGB24, false, false);
                    tex.ReadPixels(new Rect(0, 0, 720, 960), 0, 0);
                    tex.Apply();
                    var bytes = tex.EncodeToPNG();
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    rt.Release();
                    Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(tex);

                    var suffix = angle < 0.5f ? "front" : "three-quarter";
                    var outPath = Path.Combine(outDir, $"avatar-{name}-{suffix}.png");
                    File.WriteAllBytes(outPath, bytes);
                    Debug.Log($"[avatar] shot {outPath} bytes={bytes.Length} size={bounds.size:F2}");
                }
            }

            EditorApplication.Exit(0);
        }
    }
}
