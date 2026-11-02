using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Mas3D.Editor
{
    /// <summary>
    /// Renders the imported room to PNG without a GUI (never pass -nographics).
    ///   -executeMethod Mas3D.Editor.CliCapture.Capture -cam 0,1.55,-2.6 -look 1.5,1.1,0 -shot seat
    ///   -orbit 4 -r 4.2 -shot orbit            -> four views around the room centre
    ///   -flat 1 / -only Name / -dumpTex 1      -> diagnostics
    /// </summary>
    public static class CliCapture
    {
        public const int Width = 1280;
        public const int Height = 720;

        static string _shotDir;

        public static void Raw()
        {
            Capture();
        }

        public static void Capture()
        {
            var args = Environment.GetCommandLineArgs();
            var shot = ParseString(args, "-shot", "unity-shot");
            var fov = ParseFloat(args, "-fov", 42f);
            var eye = ParseVector(args, "-cam", new Vector3(-2.6f, 1.55f, 0f));
            var target = ParseVector(args, "-look", new Vector3(1.5f, 1.1f, 0f));
            var orbit = (int)ParseFloat(args, "-orbit", 0f);
            var radius = ParseFloat(args, "-r", 4.2f);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CliRoom.RoomPath);
            if (prefab == null)
            {
                Debug.LogError("[cap] room prefab missing at " + CliRoom.RoomPath);
                EditorApplication.Exit(1);
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var room = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            var bounds = ComputeBounds(room);
            room.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            bounds = ComputeBounds(room);
            Debug.Log($"[cap] room bounds min={bounds.min:F2} max={bounds.max:F2} size={bounds.size:F2}");
            LogHierarchy(room, 6);

            // -dumpAssetTex 1 writes every imported room PNG to one contact sheet so the image Unity
            // actually holds can be compared with the file on disk.
            if (ParseFloat(args, "-dumpAssetTex", 0f) > 0.5f) DumpAssetTex();

            // -uv 1 prints the wall/floor UV envelope and the sampler settings the imported
            // materials actually carry; -fixWrap 1 forces Repeat on every room texture.
            if (ParseFloat(args, "-noRefl", 0f) > 0.5f)
            {
                RenderSettings.skybox = null;
                RenderSettings.customReflectionTexture = null;
                RenderSettings.reflectionIntensity = 0f;
                Debug.Log("[refl] skybox cleared, custom reflection empty, intensity 0");
            }
            if (ParseFloat(args, "-dumpMeshes", 0f) > 0.5f) DumpMeshes(room);
            if (ParseFloat(args, "-uv", 0f) > 0.5f) DumpUv(room);
            if (ParseFloat(args, "-fixWrap", 0f) > 0.5f) ForceWrap(room);

            var only = ParseString(args, "-only", null);
            var hide = ParseString(args, "-hide", null);
            if ((only != null && only.Length > 0) || (hide != null && hide.Length > 0))
            {
                var include = only == null ? new string[0] : only.Split(',');
                var exclude = hide == null ? new string[0] : hide.Split(',');
                var shown = 0;
                foreach (var r in room.GetComponentsInChildren<Renderer>(true))
                {
                    var keep = include.Length == 0;
                    foreach (var token in include)
                    {
                        if (token.Length > 0 && r.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) keep = true;
                    }
                    foreach (var token in exclude)
                    {
                        if (token.Length > 0 && r.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) keep = false;
                    }
                    r.enabled = keep;
                    if (keep) shown++;
                }
                Debug.Log($"[cap] isolation only='{only}' hide='{hide}' keeps {shown} renderers");
            }

            if (ParseFloat(args, "-dumpTex", 0f) > 0.5f) DumpTextures(room);

            // -tagMeshes 1 paints every renderer with its own flat unlit colour so a pixel on the
            // finished frame can be traced back to the exact mesh that drew it.
            if (ParseFloat(args, "-tagMeshes", 0f) > 0.5f)
            {
                var renderers = room.GetComponentsInChildren<Renderer>(true);
                Array.Sort(renderers, (a, b) => string.CompareOrdinal(a.name, b.name));
                var index = 0;
                foreach (var r in renderers)
                {
                    var colour = new Color(((index + 1) % 8) / 7f, (((index + 1) / 8) % 8) / 7f, (((index + 1) / 64) % 8) / 7f, 1f);
                    var tagMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    tagMat.SetColor("_BaseColor", colour);
                    tagMat.SetFloat("_Cull", 0f);
                    tagMat.renderQueue = 2000;
                    var count = Mathf.Max(1, r.sharedMaterials.Length);
                    var list = new Material[count];
                    for (var i = 0; i < count; i++) list[i] = tagMat;
                    r.sharedMaterials = list;
                    Debug.Log($"[tag] {index,2} {r.name,-16} #{ColorUtility.ToHtmlStringRGB(colour)}");
                    index++;
                }
                Debug.Log($"[tag] tagged {index} renderers");
            }

            if (ParseFloat(args, "-flat", 0f) > 0.5f)
            {
                var grey = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                grey.color = new Color(0.72f, 0.72f, 0.75f, 1f);
                var replaced = 0;
                foreach (var r in room.GetComponentsInChildren<Renderer>(true))
                {
                    var count = Mathf.Max(1, r.sharedMaterials.Length);
                    var list = new Material[count];
                    for (var i = 0; i < count; i++) list[i] = grey;
                    r.sharedMaterials = list;
                    replaced++;
                }
                Debug.Log($"[cap] flat pass replaced materials on {replaced} renderers");
            }

            // -dress 1 repaints the bare white meshes, flattens the roof and swaps in the
            // bedroom night rig; without it the capture keeps the raw imported materials.
            var dressed = false;
            // Start from the tuned defaults and only move what the command line overrides,
            // otherwise every capture silently reverts to whatever numbers were typed here.
            var baseline = new RoomDress.Tuning();
            var tuning = new RoomDress.Tuning
            {
                moon = ParseFloat(args, "-moon", baseline.moon),
                lamp = ParseFloat(args, "-lamp", baseline.lamp),
                glow = ParseFloat(args, "-glow", baseline.glow),
                bounce = ParseFloat(args, "-bounce", baseline.bounce),
                floorBounce = ParseFloat(args, "-floorBounce", baseline.floorBounce),
                temperature = ParseFloat(args, "-wbTemp", baseline.temperature),
                tint = ParseFloat(args, "-wbTint", baseline.tint),
                postExposure = ParseFloat(args, "-postExposure", baseline.postExposure),
                contrast = ParseFloat(args, "-contrast", baseline.contrast),
                saturation = ParseFloat(args, "-saturation", baseline.saturation),
                shadowBias = ParseFloat(args, "-shadowBias", baseline.shadowBias),
                shadowNormalBias = ParseFloat(args, "-normalBias", baseline.shadowNormalBias),
                moonShadows = ParseFloat(args, "-moonShadows", baseline.moonShadows ? 1f : 0f) > 0.5f,
            };
            // -rebuild 1 throws away every glTFast shader-graph material and binds our own
            // URP Lit assets, so the sampler never sees the glb embedded atlas again.
            if (ParseFloat(args, "-rebuild", 0f) > 0.5f)
            {
                if (ParseFloat(args, "-reportMat", 0f) > 0.5f) RoomMaterials.Report(room);
                RoomMaterials.DefaultAoStrength = ParseFloat(args, "-aoStrength", RoomMaterials.DefaultAoStrength);
                RoomMaterials.ShaderName = ParseFloat(args, "-urpLit", 0f) > 0.5f
                    ? "Universal Render Pipeline/Lit"
                    : ParseFloat(args, "-simpleLit", 0f) > 0.5f
                        ? "Universal Render Pipeline/Simple Lit"
                        : RoomMaterials.CustomShader;
                RoomMaterials.Rebuild(room, ParseFloat(args, "-noMips", 0f) < 0.5f, ParseFloat(args, "-reportMat", 0f) > 1.5f);
                if (ParseFloat(args, "-dumpWall", 0f) > 0.5f) RoomMaterials.DumpWall(room);
                var wallScale = ParseFloat(args, "-wallScale", 0f);
                if (wallScale > 0f) RoomMaterials.ScaleWall(room, wallScale, ParseFloat(args, "-wallScaleY", wallScale));
            }

            // Contact shading is part of the look now, so it is configured on every capture;
            // -ssao 0 keeps the option to shoot the same frame without it.
            if (ParseFloat(args, "-ssao", 1f) > 0.5f)
            {
                RoomAocclusion.Configure(
                    ParseFloat(args, "-ssaoIntensity", 0.6f),
                    ParseFloat(args, "-ssaoRadius", 0.15f),
                    ParseFloat(args, "-ssaoFalloff", 150f));
            }

            // -tintMat 1 keeps every rebuilt texture but multiplies it by a unique hue, so a
            // pixel in the finished frame still names the mesh that drew it. Flat tagging hides
            // texture aliasing, which is exactly what needs tracing here.
            if (ParseFloat(args, "-tintMat", 0f) > 0.5f)
            {
                var tintRenderers = room.GetComponentsInChildren<Renderer>(true);
                Array.Sort(tintRenderers, (a, b) => string.CompareOrdinal(a.name, b.name));
                var tintIndex = 0;
                foreach (var r in tintRenderers)
                {
                    var hue = (tintIndex * 0.618034f) % 1f;
                    // -tintMat 2 clones the materials with a neutral white tint: the A/B against
                    // mode 1 tells whether the aliasing follows the tint value or the clone itself.
                    var tint = ParseFloat(args, "-tintMat", 1f) > 1.5f ? Color.white : Color.HSVToRGB(hue, 0.9f, 1f);
                    var mats = r.sharedMaterials;
                    var clones = new Material[mats.Length];
                    for (var i = 0; i < mats.Length; i++)
                    {
                        var copy = new Material(mats[i]);
                        copy.SetColor("_BaseColor", tint);
                        clones[i] = copy;
                    }
                    r.sharedMaterials = clones;
                    Debug.Log($"[tint] {tintIndex,2} {r.name,-16} hue={hue:F3} #{ColorUtility.ToHtmlStringRGB(tint)}");
                    tintIndex++;
                }
                Debug.Log($"[tint] tinted {tintIndex} renderers");
            }

            if (ParseFloat(args, "-dress", 0f) > 0.5f)
            {
                RoomDress.Apply(room, true, true, tuning, ParseFloat(args, "-probeSolid", 0f) > 0.5f);
                dressed = true;
            }

            if (!dressed) CreateLights();

            // -noBatch 1 turns the SRP batcher off; -dumpBatch 1 reports whether the importer left
            // the room inside a static batch, both classic causes of one mesh drawing with another
            // mesh's material once the whole room is on screen.
            if (ParseFloat(args, "-noBatch", 0f) > 0.5f)
            {
                UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching = false;
                Debug.Log("[cap] srp batcher off");
            }
            // -clearMpb 1 wipes any per-renderer MaterialPropertyBlock the glTF importer may have
            // left behind; a stale block can keep feeding one mesh another mesh's textures.
            // -ambient N lifts the ambient term of every room material without touching the lights.
            var ambient = ParseFloat(args, "-ambient", 0f);
            if (ambient > 0f)
            {
                foreach (var rend in room.GetComponentsInChildren<MeshRenderer>(true))
                foreach (var mat in rend.sharedMaterials)
                    if (mat != null && mat.HasProperty("_AmbientStrength")) mat.SetFloat("_AmbientStrength", ambient);
                Debug.Log($"[cap] ambient strength {ambient}");
            }

            // -runtimeTex 1 rebuilds every map from the PNG bytes at capture time, bypassing the
            // importer (BC7) and the asset database entirely.
            if (ParseFloat(args, "-runtimeTex", 0f) > 0.5f)
            {
                var cache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
                var swapped = 0;
                foreach (var rend in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats = rend.sharedMaterials;
                    for (var i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null) continue;
                        var src = mats[i].GetTexture("_BaseMap");
                        if (src == null) continue;
                        var path = "Assets/_Local/tex/" + src.name + ".png";
                        if (!File.Exists(path)) continue;
                        if (!cache.TryGetValue(path, out var tex))
                        {
                            tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                            tex.LoadImage(File.ReadAllBytes(path));
                            tex.wrapMode = TextureWrapMode.Repeat;
                            tex.filterMode = FilterMode.Bilinear;
                            tex.name = src.name + "_raw";
                            cache[path] = tex;
                        }
                        var copy = new Material(mats[i]);
                        copy.SetTexture("_BaseMap", tex);
                        mats[i] = copy;
                        swapped++;
                    }
                    rend.sharedMaterials = mats;
                }
                Debug.Log($"[cap] runtime textures swapped into {swapped} slots ({cache.Count} unique)");
            }

            // -wallTex checker 1 puts a generated pattern on the wall: whatever appears proves which
            // texture the wall draw actually samples.
            if (ParseFloat(args, "-wallTexChecker", 0f) > 0.5f)
            {
                var checker = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                    checker.SetPixel(x, y, (x + y) % 2 == 0 ? Color.magenta : Color.cyan);
                checker.Apply();
                var applied = 0;
                foreach (var rend in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!rend.name.Contains("Mesh_0115out")) continue;
                    var mats = rend.sharedMaterials;
                    for (var i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null) continue;
                        var copy = new Material(mats[i]);
                        copy.SetTexture("_BaseMap", checker);
                        mats[i] = copy;
                        applied++;
                    }
                    rend.sharedMaterials = mats;
                }
                Debug.Log($"[cap] wall checker applied to {applied} slots");
            }

            // -cloneMats 1 swaps every material for an in-memory copy that changes nothing: if the
            // walls come out right, the fault lives in the saved asset, not in the texture data.
            if (ParseFloat(args, "-cloneMats", 0f) > 0.5f)
            {
                var cloned = 0;
                foreach (var rend in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats = rend.sharedMaterials;
                    var copies = new Material[mats.Length];
                    for (var i = 0; i < mats.Length; i++)
                    {
                        copies[i] = mats[i] == null ? null : new Material(mats[i]);
                        cloned++;
                    }
                    rend.sharedMaterials = copies;
                }
                Debug.Log($"[cap] cloned {cloned} material slots in memory");
            }

            // -solidWall 1 paints the wall shell with a flat magenta unlit material: whatever is
            // still visible on top of it is another mesh drawing across the wall.
            if (ParseFloat(args, "-solidWall", 0f) > 0.5f)
            {
                foreach (var rend in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!rend.name.Contains("Mesh_0115out")) continue;
                    var flat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    flat.SetColor("_BaseColor", Color.magenta);
                    flat.SetFloat("_Cull", 0f);
                    rend.sharedMaterials = new[] { flat };
                    Debug.Log("[cap] wall forced magenta");
                }
            }
            // -noRecvShadow 1 turns shadow receiving off on every room material: if the pattern on
            // the walls disappears, it was shadow acne, not a texture alias.
            if (ParseFloat(args, "-noRecvShadow", 0f) > 0.5f)
            {
                var hit = 0;
                foreach (var rend in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    foreach (var mat in rend.sharedMaterials)
                    {
                        if (mat == null) continue;
                        mat.SetFloat("_ReceiveShadows", 0f);
                        hit++;
                    }
                    rend.receiveShadows = false;
                }
                Debug.Log($"[cap] shadow receiving off on {hit} materials");
            }
            if (ParseFloat(args, "-clearMpb", 0f) > 0.5f)
            {
                var cleared = 0;
                foreach (var rend in room.GetComponentsInChildren<Renderer>(true))
                {
                    rend.SetPropertyBlock(null);
                    cleared++;
                }
                Debug.Log($"[cap] material property blocks cleared on {cleared} renderers");
            }
            if (ParseFloat(args, "-dumpBatch", 0f) > 0.5f)
            {
                foreach (var mr in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    var submeshes = mf != null && mf.sharedMesh != null ? mf.sharedMesh.subMeshCount : 0;
                    Debug.Log($"[batch] {mr.name,-16} static={mr.isPartOfStaticBatch} submeshes={submeshes} mats={mr.sharedMaterials.Length} enabled={mr.enabled}");
                }
            }

            var camGo = new GameObject("CaptureCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 120f;
            // The room is airtight apart from the window opening, so the background is what the
            // glass shows: a real equirect night sky instead of flat black.
            if (dressed && ParseFloat(args, "-sky", 1f) > 0.5f) RoomDress.BuildSkyCard(bounds, bounds.max.x);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
            cam.allowHDR = false;
            cam.allowMSAA = true;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            if (dressed) RoomDress.ApplyColour(tuning);
            if (ParseFloat(args, "-noLights", 0f) > 0.5f)
            {
                var off = 0;
                foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    light.enabled = false;
                    off++;
                }
                Debug.Log($"[cap] lights off: {off}");
            }
            if (ParseFloat(args, "-noPost", 0f) > 0.5f)
            {
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                Debug.Log("[cap] post processing off");
            }

            _shotDir = Path.Combine(Directory.GetCurrentDirectory(), "Shots");
            Directory.CreateDirectory(_shotDir);

            // -tunes "moon=2;lamp=1.2|moon=3;post=0" renders one frame per preset so a light
            // sweep costs a single editor launch.
            var tunes = ParseString(args, "-tunes", null);
            if (tunes != null && dressed)
            {
                var index = 1;
                foreach (var entry in tunes.Split('|'))
                {
                    var preset = new RoomDress.Tuning
                    {
                        moon = tuning.moon, lamp = tuning.lamp, glow = tuning.glow, bounce = tuning.bounce,
                        postExposure = tuning.postExposure, contrast = tuning.contrast, saturation = tuning.saturation,
                    };
                    foreach (var pair in entry.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = pair.Split('=');
                        if (kv.Length != 2) continue;
                        var value = 0f;
                        if (!float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) continue;
                        switch (kv[0].Trim().ToLowerInvariant())
                        {
                            case "moon": preset.moon = value; break;
                            case "lamp": preset.lamp = value; break;
                            case "glow": preset.glow = value; break;
                            case "bounce": preset.bounce = value; break;
                            case "post": preset.postExposure = value; break;
                            case "contrast": preset.contrast = value; break;
                            case "sat": preset.saturation = value; break;
                        }
                    }
                    RoomDress.Retune(preset);
                    Debug.Log($"[cap] tune {index}: moon={preset.moon} lamp={preset.lamp} glow={preset.glow} post={preset.postExposure}");
                    RenderOne(camGo, cam, eye, target, shot + "-" + index.ToString("00"));
                    index++;
                }
                EditorApplication.Exit(0);
                return;
            }

            // -sweep 1 renders one frame per renderer with everything else disabled, so a mesh
            // that silently swallows the frame is caught inside a single editor launch instead
            // of thirty-three of them.
            if (ParseFloat(args, "-sweep", 0f) > 0.5f)
            {
                var sweep = room.GetComponentsInChildren<Renderer>(true);
                Array.Sort(sweep, (a, b) => string.CompareOrdinal(a.name, b.name));
                for (var i = 0; i < sweep.Length; i++)
                {
                    for (var j = 0; j < sweep.Length; j++) sweep[j].enabled = j == i;
                    RenderOne(camGo, cam, eye, target, shot + "-" + i.ToString("00") + "-" + sweep[i].name);
                }
                for (var j = 0; j < sweep.Length; j++) sweep[j].enabled = true;
                EditorApplication.Exit(0);
                return;
            }

            var centre = bounds.center;
            if (orbit > 0)
            {
                for (var i = 0; i < orbit; i++)
                {
                    var angle = i * (360f / orbit) * Mathf.Deg2Rad;
                    var pos = new Vector3(centre.x + Mathf.Sin(angle) * radius, 1.55f, centre.z + Mathf.Cos(angle) * radius);
                    // Aim at eye level, not the bounding-box centre: the centre of a 3 m room sits
                    // above the horizon and tilts every orbit frame up at the ceiling.
                    var aim = new Vector3(centre.x, 1.05f, centre.z);
                    RenderOne(camGo, cam, pos, aim, shot + "-" + (i + 1).ToString("00"));
                }
            }
            else
            {
                RenderOne(camGo, cam, eye, target, shot);
            }

            EditorApplication.Exit(0);
        }

        static void RenderOne(GameObject camGo, Camera cam, Vector3 eye, Vector3 target, string shot)
        {
            camGo.transform.position = eye;
            camGo.transform.LookAt(target);

            var path = Path.Combine(_shotDir, shot + ".png");
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);

            Debug.Log($"[cap] cam={eye:F2}->{target:F2} wrote {path} ({new FileInfo(path).Length} bytes)");
        }

        static void CreateLights()
        {
            var moonGo = new GameObject("Moon");
            var moon = moonGo.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = new Color32(0xbc, 0xd6, 0xff, 0xff);
            moon.intensity = 1.1f;
            moon.shadows = LightShadows.Soft;
            moonGo.transform.rotation = Quaternion.Euler(32f, -38f, 0f);

            var lampGo = new GameObject("Lamp");
            var lamp = lampGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = new Color32(0xff, 0xb8, 0x77, 0xff);
            lamp.intensity = 1.6f;
            lamp.range = 6f;
            lampGo.transform.position = new Vector3(0.2f, 1.9f, -0.4f);

            var fillGo = new GameObject("Fill");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.color = new Color32(0x8f, 0xa6, 0xd8, 0xff);
            fill.intensity = 0.35f;
            fillGo.transform.rotation = Quaternion.Euler(10f, 140f, 0f);
        }

        static void DumpMeshes(GameObject room)
        {
            var lines = new List<string>();
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                var mat = renderer != null && renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials[0] : null;
                var tex = "-";
                if (mat != null)
                {
                    foreach (var prop in mat.GetTexturePropertyNames())
                    {
                        var t = mat.GetTexture(prop);
                        if (t != null) { tex = t.name; break; }
                    }
                }
                var size = Vector3.Scale(mesh.bounds.size, filter.transform.lossyScale);
                var centre = filter.transform.TransformPoint(mesh.bounds.center);
                lines.Add($"[mesh] {filter.name,-16} tris={mesh.triangles.Length / 3,6} tex={tex,-14} size=({size.x:F2},{size.y:F2},{size.z:F2}) at=({centre.x:F2},{centre.y:F2},{centre.z:F2})");
            }
            lines.Sort((a, b) => string.CompareOrdinal(a, b));
            foreach (var line in lines) Debug.Log(line);
        }

        static void DumpUv(GameObject room)
        {
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                if (filter.name != "Mesh_0115out" && filter.name != "Mesh_0140out" && filter.name != "Mesh_0116out") continue;
                var uv = filter.sharedMesh.uv;
                if (uv == null || uv.Length == 0)
                {
                    Debug.Log($"[uv] {filter.name}: no uv0");
                    continue;
                }
                var min = uv[0];
                var max = uv[0];
                foreach (var v in uv)
                {
                    min = new Vector2(Mathf.Min(min.x, v.x), Mathf.Min(min.y, v.y));
                    max = new Vector2(Mathf.Max(max.x, v.x), Mathf.Max(max.y, v.y));
                }
                var renderer = filter.GetComponent<MeshRenderer>();
                var mat = renderer != null ? renderer.sharedMaterial : null;
                var texName = "-";
                var wrap = "-";
                var scale = "-";
                if (mat != null)
                {
                    foreach (var prop in mat.GetTexturePropertyNames())
                    {
                        var tex = mat.GetTexture(prop);
                        if (tex == null) continue;
                        texName = tex.name;
                        wrap = tex.wrapMode.ToString();
                        var s = mat.GetTextureScale(prop);
                        scale = $"({s.x:F2},{s.y:F2})";
                        break;
                    }
                }
                var extra = "-";
                if (mat != null)
                {
                    var surface = mat.HasProperty("_Surface") ? mat.GetFloat("_Surface") : -1f;
                    var blend = mat.HasProperty("_Blend") ? mat.GetFloat("_Blend") : -1f;
                    var clip = mat.HasProperty("_AlphaClip") ? mat.GetFloat("_AlphaClip") : -1f;
                    var cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : -1f;
                    var alphaTex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
                    extra = $"queue={mat.renderQueue} surface={surface} blend={blend} clip={clip} cutoff={cutoff:F2} keywords={string.Join(",", mat.shaderKeywords)}";
                }
                Debug.Log($"[uv] {filter.name}: uv0 min=({min.x:F2},{min.y:F2}) max=({max.x:F2},{max.y:F2}) mat={mat?.name} tex={texName} wrap={wrap} scale={scale} shader={mat?.shader?.name} {extra}");
            }
        }

        static void ForceWrap(GameObject room)
        {
            var fixedCount = 0;
            var seen = new HashSet<Texture>();
            foreach (var renderer in room.GetComponentsInChildren<MeshRenderer>(true))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    foreach (var prop in mat.GetTexturePropertyNames())
                    {
                        var tex = mat.GetTexture(prop);
                        if (tex == null || !seen.Add(tex)) continue;
                        var before = tex.wrapMode;
                        tex.wrapMode = TextureWrapMode.Repeat;
                        fixedCount++;
                        if (before != TextureWrapMode.Repeat) Debug.Log($"[wrap] {tex.name}: {before} -> Repeat");
                    }
                }
            }
            Debug.Log($"[wrap] touched {fixedCount} textures");
        }

        static void DumpTextures(GameObject room)
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "Shots", "tex");
            Directory.CreateDirectory(dir);
            var seen = new HashSet<string>();
            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var map = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.mainTexture;
                    var st = m.HasProperty("_BaseMap") ? m.GetTextureScale("_BaseMap") + "/" + m.GetTextureOffset("_BaseMap") : "-";
                    Debug.Log($"[cap] mat {r.name} -> {m.name} map={(map != null ? map.name + " " + map.width + "x" + map.height : "null")} st={st} color={m.color}");
                    if (map == null || !seen.Add(map.name)) continue;
                    var rt = RenderTexture.GetTemporary(map.width, map.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(map, rt);
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(map.width, map.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, map.width, map.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                    var safe = string.Join("_", map.name.Split(Path.GetInvalidFileNameChars()));
                    var p = Path.Combine(dir, safe + ".png");
                    File.WriteAllBytes(p, tex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(tex);
                    Debug.Log($"[cap] tex dumped {safe} -> {p}");
                }
            }
        }

        static void DumpAssetTex()
        {
            const int cell = 192;
            var files = Directory.GetFiles("Assets/_Local/tex", "*.png");
            Array.Sort(files, StringComparer.Ordinal);
            var columns = Mathf.CeilToInt(Mathf.Sqrt(files.Length));
            var rows = Mathf.CeilToInt(files.Length / (float)columns);
            var sheet = new Texture2D(columns * cell, rows * cell, TextureFormat.RGBA32, false);
            var clear = new Color32[sheet.width * sheet.height];
            sheet.SetPixels32(clear);
            var rt = RenderTexture.GetTemporary(cell, cell, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var buf = new Texture2D(cell, cell, TextureFormat.RGBA32, false);
            for (var i = 0; i < files.Length; i++)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(files[i].Replace('\\', '/'));
                if (tex == null) { Debug.LogWarning("[atex] missing " + files[i]); continue; }
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                buf.ReadPixels(new Rect(0, 0, cell, cell), 0, 0);
                buf.Apply();
                RenderTexture.active = null;
                var px = buf.GetPixels32();
                var x0 = (i % columns) * cell;
                var y0 = (rows - 1 - i / columns) * cell;
                sheet.SetPixels32(x0, y0, cell, cell, px);
                var avg = new Color(0f, 0f, 0f);
                for (var k = 0; k < px.Length; k += 977) avg += px[k];
                avg /= Mathf.CeilToInt(px.Length / 977f);
                Debug.Log($"[atex] {i:00} {Path.GetFileNameWithoutExtension(files[i])} {tex.width}x{tex.height} fmt={tex.format} srgb={tex.isDataSRGB} avg=({avg.r:F2},{avg.g:F2},{avg.b:F2})");
            }
            sheet.Apply();
            RenderTexture.ReleaseTemporary(rt);
            var outPath = Path.Combine(_shotDir, "asset-tex-sheet.png");
            File.WriteAllBytes(outPath, sheet.EncodeToPNG());
            Debug.Log("[atex] wrote " + outPath);
            UnityEngine.Object.DestroyImmediate(sheet);
            UnityEngine.Object.DestroyImmediate(buf);
        }

        static void LogHierarchy(GameObject room, int max)
        {
            var sb = new StringBuilder();
            var count = 0;
            foreach (Transform t in room.GetComponentsInChildren<Transform>(true))
            {
                var r = t.GetComponent<Renderer>();
                sb.Append(t.name);
                sb.Append(" pos=").Append(t.position.ToString("F2"));
                sb.Append(" scale=").Append(t.lossyScale.ToString("F2"));
                if (r != null)
                {
                    sb.Append(" size=").Append(r.bounds.size.ToString("F2"));
                    sb.Append(" mat=").Append(r.sharedMaterial != null ? r.sharedMaterial.name : "null");
                    var mf = t.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh != null)
                    {
                        sb.Append(" submeshes=").Append(mesh.subMeshCount);
                        sb.Append(" uv=").Append(Range(mesh.uv));
                        if (mesh.colors != null && mesh.colors.Length > 0) sb.Append(" vcolor=").Append(Range(mesh.colors));
                    }
                }
                sb.Append('\n');
                if (++count >= max) break;
            }
            Debug.Log("[cap] hierarchy:\n" + sb);
        }

        static string Range(Vector2[] uv)
        {
            if (uv == null || uv.Length == 0) return "none";
            var min = uv[0];
            var max = uv[0];
            foreach (var v in uv)
            {
                min = Vector2.Min(min, v);
                max = Vector2.Max(max, v);
            }
            return min.ToString("F2") + ".." + max.ToString("F2") + " n=" + uv.Length;
        }

        static string Range(Color[] colors)
        {
            if (colors == null || colors.Length == 0) return "none";
            float rMin = 1f, gMin = 1f, bMin = 1f, aMin = 1f;
            float rMax = 0f, gMax = 0f, bMax = 0f, aMax = 0f;
            foreach (var c in colors)
            {
                rMin = Mathf.Min(rMin, c.r); rMax = Mathf.Max(rMax, c.r);
                gMin = Mathf.Min(gMin, c.g); gMax = Mathf.Max(gMax, c.g);
                bMin = Mathf.Min(bMin, c.b); bMax = Mathf.Max(bMax, c.b);
                aMin = Mathf.Min(aMin, c.a); aMax = Mathf.Max(aMax, c.a);
            }
            return "r" + rMin.ToString("F2") + "-" + rMax.ToString("F2") + " g" + gMin.ToString("F2") + "-" + gMax.ToString("F2")
                + " b" + bMin.ToString("F2") + "-" + bMax.ToString("F2") + " a" + aMin.ToString("F2") + "-" + aMax.ToString("F2");
        }

        static Bounds ComputeBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.zero);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        static string ParseString(string[] args, string flag, string fallback)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }

        static float ParseFloat(string[] args, string flag, float fallback)
        {
            var raw = ParseString(args, flag, null);
            return raw != null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        static Vector3 ParseVector(string[] args, string flag, Vector3 fallback)
        {
            var raw = ParseString(args, flag, null);
            if (raw == null) return fallback;
            var parts = raw.Split(',');
            if (parts.Length != 3) return fallback;
            var ok = true;
            var values = new float[3];
            for (var i = 0; i < 3; i++)
                ok &= float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
            return ok ? new Vector3(values[0], values[1], values[2]) : fallback;
        }
    }
}
