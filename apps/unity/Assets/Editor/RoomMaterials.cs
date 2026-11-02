using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Replaces every glTFast shader-graph material in the imported room with a plain URP Lit
    /// material backed by a project-owned PNG, extracted from the glb by tools/glb-tex-extract.mjs.
    /// Goal: no shader graph, no glb-embedded sub-asset textures, explicit wrap and tiling.
    /// </summary>
    public static class RoomMaterials
    {
        public const string TexDir = "Assets/_Local/tex";
        public const string MatDir = "Assets/_Local/materials";
        public const string ManifestPath = "Assets/_Local/room/glb-materials.json";

        [System.Serializable]
        public class ManifestImage
        {
            public int index;
            public string name;
            public string file;
            public int width;
            public int height;
        }

        [System.Serializable]
        public class ManifestTexture
        {
            public int index;
            public int source;
            public string image;
            public string file;
        }

        [System.Serializable]
        public class ManifestMaterial
        {
            public int index;
            public string name;
            public int baseColorTexture;
            public string image;
            public string file;
            public float[] baseColorFactor;
            public float metallicFactor;
            public float roughnessFactor;
            public string alphaMode;
            public bool doubleSided;
        }

        [System.Serializable]
        public class ManifestMesh
        {
            public int index;
            public string name;
            public int[] materials;
        }

        [System.Serializable]
        public class Manifest
        {
            public List<ManifestImage> images = new List<ManifestImage>();
            public List<ManifestTexture> textures = new List<ManifestTexture>();
            public List<ManifestMaterial> materials = new List<ManifestMaterial>();
            public List<ManifestMesh> meshes = new List<ManifestMesh>();
        }

        public static Manifest Load()
        {
            if (!File.Exists(ManifestPath))
            {
                Debug.LogError("[rebuild] manifest missing: " + ManifestPath);
                return null;
            }

            var json = File.ReadAllText(ManifestPath);
            var manifest = JsonUtility.FromJson<Manifest>(json);
            if (manifest == null || manifest.materials == null || manifest.materials.Count == 0)
            {
                Debug.LogError("[rebuild] manifest unreadable: " + ManifestPath);
                return null;
            }

            return manifest;
        }

        /// <summary>Repeat wrap, sRGB, mipmaps, anisotropy - the four settings glTF needs.</summary>
        public static int ConfigureTextures(Manifest manifest, bool mipmaps = true)
        {
            var touched = 0;
            foreach (var image in manifest.images)
            {
                var path = TexDir + "/" + image.file;
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    Debug.LogWarning("[rebuild] texture not found: " + path);
                    continue;
                }

                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.alphaSource = image.name.EndsWith("_2") && Needed(manifest, image) ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.mipmapEnabled = mipmaps;
                importer.anisoLevel = mipmaps ? 8 : 1;
                importer.maxTextureSize = 1024;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
                touched++;
            }

            return touched;
        }

        /// <summary>Only the images that a material actually uses as base colour should keep alpha.</summary>
        static bool Needed(Manifest manifest, ManifestImage image)
        {
            foreach (var m in manifest.materials)
            {
                if (m.image == image.name) return true;
            }

            return false;
        }

        public static Texture2D LoadTexture(string file)
        {
            var path = TexDir + "/" + file;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) Debug.LogWarning("[rebuild] cannot load texture " + path);
            return tex;
        }

        /// <summary>Which shader the rebuilt materials use. URP Lit / Simple Lit both sample the
        /// wrong map once any realtime shadow path is active in this editor build, which paints
        /// the bed atlas across the walls at grazing angles, so our own surface shader is the
        /// default; -urpLit 1 switches back for comparison.</summary>
        public static string ShaderName = CustomShader;

        public const string CustomShader = "Mas3D/RoomSurface";

        /// <summary>Ambient lift baked into every rebuilt material; the room is lit from one
        /// window and a bounce term is what keeps the far side of the floor readable.</summary>
        public const float DefaultAmbient = 2f;

        /// <summary>How much of the screen space occlusion reaches the ambient term.</summary>
        public static float DefaultAoStrength = 0.55f;

        public static void SetAlbedo(Material mat, Color color)
        {
            if (mat == null) return;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        }

        public static void SetAlbedoMap(Material mat, Texture tex)
        {
            if (mat == null || tex == null) return;
            if (mat.HasProperty("_MainTex"))
            {
                mat.SetTexture("_MainTex", tex);
                return;
            }
            mat.SetTexture("_BaseMap", tex);
        }

        /// <summary>Normal maps only exist on the URP shaders; the custom surface shader is
        /// deliberately flat, so this quietly does nothing there.</summary>
        public static void SetNormalMap(Material mat, Texture tex, float scale)
        {
            if (mat == null || tex == null || !mat.HasProperty("_BumpMap")) return;
            mat.SetTexture("_BumpMap", tex);
            mat.SetFloat("_BumpScale", scale);
            mat.EnableKeyword("_NORMALMAP");
        }

        public static Material BuildMaterial(ManifestMaterial src, Texture2D tex)
        {
            var shader = Shader.Find(ShaderName) ?? Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[rebuild] URP Lit shader missing");
                return null;
            }

            var mat = new Material(shader) { name = src.name };
            var f = src.baseColorFactor;
            var color = f != null && f.Length >= 3
                ? new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f)
                : Color.white;

            // Our own shader names its slots differently on purpose: a distinct texture property
            // keeps the sampled map pinned to this material.
            var custom = shader.name == CustomShader;
            var mapName = custom ? "_MainTex" : "_BaseMap";
            var colorName = custom ? "_Color" : "_BaseColor";
            mat.SetColor(colorName, color);

            if (tex != null)
            {
                mat.SetTexture(mapName, tex);
                mat.SetTextureScale(mapName, Vector2.one);
                mat.SetTextureOffset(mapName, Vector2.zero);
            }

            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", src.metallicFactor);
            // glTF roughness 0.5 is a matte interior surface; taken literally as URP smoothness it
            // turns every wall into a mirror that blows out under the moon light.
            mat.SetFloat("_Smoothness", Mathf.Clamp(1f - src.roughnessFactor, 0f, 0.18f));
            // glTF is double sided; a room read from the inside needs both faces either way.
            mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_ReceiveShadows")) mat.SetFloat("_ReceiveShadows", 1f);
            if (mat.HasProperty("_AmbientStrength")) mat.SetFloat("_AmbientStrength", DefaultAmbient);
            if (mat.HasProperty("_AOStrength")) mat.SetFloat("_AOStrength", DefaultAoStrength);
            mat.enableInstancing = false;
            return mat;
        }

        /// <summary>Per-map exposure trim. The glTF maps are authored for a tone-mapped
        /// daylight viewer; under this night rig a few of them clip or sink, so each one gets a
        /// multiplier and its own ambient weight instead of a global exposure change.</summary>
        struct Calibration
        {
            public float tint;
            public float ambient;
            public float red;
            public float green;
            public float blue;
        }

        static readonly Dictionary<string, Calibration> Trims = new Dictionary<string, Calibration>
        {
            // Pale pink rug under a strong ambient term: without the trim it renders pure white.
            { "Tex_0201_2", new Calibration { tint = 0.95f, ambient = 1.50f } },
            // The wood floor only ever sees light from above; it needs more bounce than the rest.
            { "Tex_0271_2", new Calibration { tint = 1.25f, ambient = 3.40f } },
            // Bed linen and pillows are the brightest surface and clip before anything else.
            { "Tex_0206_2", new Calibration { tint = 0.70f, ambient = 1.30f } },
            // The wallpaper must stay close to the web build, which is a touch deeper than the map.
            { "Tex_0376_2", new Calibration { tint = 1.00f, ambient = 2.00f, red = 1.00f, green = 0.82f, blue = 0.88f } },
        };

        static void Calibrate(Material mat, string image)
        {
            if (mat == null || string.IsNullOrEmpty(image)) return;
            if (!Trims.TryGetValue(image, out var trim)) return;

            var red = trim.red > 0f ? trim.red : 1f;
            var green = trim.green > 0f ? trim.green : 1f;
            var blue = trim.blue > 0f ? trim.blue : 1f;

            var baseColor = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            var tinted = new Color(
                baseColor.r * trim.tint * red,
                baseColor.g * trim.tint * green,
                baseColor.b * trim.tint * blue,
                baseColor.a);
            SetAlbedo(mat, tinted);
            if (mat.HasProperty("_AmbientStrength")) mat.SetFloat("_AmbientStrength", trim.ambient);
        }

        public static Material SaveMaterial(Material mat)
        {
            Directory.CreateDirectory(MatDir);
            var path = MatDir + "/" + Sanitize(mat.name) + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.shader = mat.shader;
                existing.CopyPropertiesFromMaterial(mat);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static string Sanitize(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                foreach (var b in bad) if (chars[i] == b) chars[i] = '_';
            }

            return new string(chars);
        }

        /// <summary>Swap every renderer of the room onto URP Lit material assets. Returns slots replaced.</summary>
        public static int Rebuild(GameObject room, bool mipmaps = true, bool verbose = false)
        {
            var manifest = Load();
            if (manifest == null) return -1;

            var texTouched = ConfigureTextures(manifest, mipmaps);

            var byName = new Dictionary<string, ManifestMaterial>();
            var byIndex = new Dictionary<int, ManifestMaterial>();
            foreach (var m in manifest.materials)
            {
                if (!string.IsNullOrEmpty(m.name)) byName[m.name] = m;
                byIndex[m.index] = m;
            }

            var meshByName = new Dictionary<string, ManifestMesh>();
            foreach (var m in manifest.meshes) meshByName[m.name] = m;

            var textureByImage = new Dictionary<string, Texture2D>();
            var built = new Dictionary<int, Material>();

            var renderers = room.GetComponentsInChildren<MeshRenderer>(true);
            var replaced = 0;
            var missing = 0;

            foreach (var renderer in renderers)
            {
                var slots = renderer.sharedMaterials;
                var next = new Material[slots.Length];
                for (var i = 0; i < slots.Length; i++)
                {
                    var current = slots[i];
                    ManifestMaterial src = null;

                    // The renderer name is the glTF mesh name, which is the only trustworthy key:
                    // glTFast rewrites material names, so several renderers end up pointing at one
                    // name and the lookup silently lands on another mesh's texture.
                    if (meshByName.TryGetValue(renderer.name, out var mesh) && mesh.materials != null && i < mesh.materials.Length)
                    {
                        byIndex.TryGetValue(mesh.materials[i], out src);
                    }

                    if (src == null && current != null) byName.TryGetValue(Clean(current.name), out src);

                    if (src != null && replaced < 6)
                    {
                        Debug.Log($"[rebuild] {renderer.name}[{i}] <- {Clean(current == null ? "<null>" : current.name)} => {src.name} image={src.image}");
                    }

                    if (src == null)
                    {
                        missing++;
                        next[i] = current;
                        continue;
                    }

                    if (!built.TryGetValue(src.index, out var mat))
                    {
                        Texture2D tex = null;
                        if (!string.IsNullOrEmpty(src.image))
                        {
                            if (!textureByImage.TryGetValue(src.image, out tex))
                            {
                                tex = LoadTexture(src.file);
                                textureByImage[src.image] = tex;
                            }
                        }

                        mat = SaveMaterial(BuildMaterial(src, tex));
                        Calibrate(mat, src.image);
                        built[src.index] = mat;
                    }

                    next[i] = mat;
                    replaced++;
                }

                renderer.sharedMaterials = next;
            }

            if (verbose)
            {
                foreach (var renderer in renderers)
                {
                    var m0 = renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials[0] : null;
                    var t0 = m0 != null ? m0.GetTexture("_BaseMap") as Texture2D : null;
                    Debug.Log($"[bind] {renderer.name,-16} mat={(m0 != null ? m0.name : "-"),-24} tex={(t0 != null ? t0.name : "-"),-16} asset={(m0 != null ? AssetDatabase.GetAssetPath(m0) : "-")}");
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[rebuild] images={manifest.images.Count} textures={texTouched} materials={built.Count} slots={replaced} unresolved={missing} renderers={renderers.Length}");
            return replaced;
        }

        /// <summary>Diagnostic: force a _BaseMap tiling on the wall so we can see which one the render uses.</summary>
        public static void ScaleWall(GameObject room, float sx, float sy)
        {
            foreach (var renderer in room.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.name != "Mesh_0115out") continue;
                var mat = renderer.sharedMaterial;
                if (mat == null) continue;
                mat.SetTextureScale("_BaseMap", new Vector2(sx, sy));
                Debug.Log($"[wall] forced scale=({sx},{sy}) now={mat.GetTextureScale("_BaseMap")}");
            }
        }

        /// <summary>Diagnostic: what the wall renderer really holds after the swap.</summary>
        public static void DumpWall(GameObject room)
        {
            foreach (var renderer in room.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.name != "Mesh_0115out") continue;
                var mat = renderer.sharedMaterial;
                var tex = mat == null ? null : mat.GetTexture("_BaseMap") as Texture2D;
                var scale = mat == null ? Vector2.zero : mat.GetTextureScale("_BaseMap");
                var offset = mat == null ? Vector2.zero : mat.GetTextureOffset("_BaseMap");
                Debug.Log($"[wall] mat={(mat == null ? "<null>" : mat.name)} shader={(mat == null ? "?" : mat.shader.name)} " +
                          $"tex={(tex == null ? "<null>" : tex.name)} {tex?.width}x{tex?.height} wrap={(tex == null ? "?" : tex.wrapMode.ToString())} " +
                          $"st=({scale.x:0.00},{scale.y:0.00}) off=({offset.x:0.00},{offset.y:0.00}) color={(mat == null ? "?" : mat.GetColor("_BaseColor").ToString())}");

                var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;

                // Per wall face: the four planes share one material, but each plane has its own
                // UV span, so the tiling has to be chosen from these numbers, not from the bbox.
                var groups = new Dictionary<string, Vector4>();
                var normals = mesh.normals;
                var uvs = mesh.uv;
                for (var i = 0; i < uvs.Length && i < normals.Length; i++)
                {
                    var n = normals[i];
                    var key = Mathf.Abs(n.x) > Mathf.Abs(n.y) && Mathf.Abs(n.x) > Mathf.Abs(n.z)
                        ? (n.x > 0 ? "+x" : "-x")
                        : Mathf.Abs(n.z) > Mathf.Abs(n.y) ? (n.z > 0 ? "+z" : "-z") : (n.y > 0 ? "+y" : "-y");
                    var uv = uvs[i];
                    if (!groups.TryGetValue(key, out var box)) box = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                    box = new Vector4(Mathf.Min(box.x, uv.x), Mathf.Min(box.y, uv.y), Mathf.Max(box.z, uv.x), Mathf.Max(box.w, uv.y));
                    groups[key] = box;
                }

                foreach (var pair in groups)
                {
                    var b = pair.Value;
                    Debug.Log($"[wall] face {pair.Key} u={b.x:0.00}..{b.z:0.00} (span {b.z - b.x:0.00}) v={b.y:0.00}..{b.w:0.00} (span {b.w - b.y:0.00})");
                }

                for (var ch = 0; ch < mesh.uv.Length && ch < 3; ch++)
                {
                    var uv = ch == 0 ? mesh.uv : ch == 1 ? mesh.uv2 : mesh.uv3;
                    if (uv == null || uv.Length == 0) continue;
                    var min = new Vector2(float.MaxValue, float.MaxValue);
                    var max = new Vector2(float.MinValue, float.MinValue);
                    foreach (var t in uv)
                    {
                        min = new Vector2(Mathf.Min(min.x, t.x), Mathf.Min(min.y, t.y));
                        max = new Vector2(Mathf.Max(max.x, t.x), Mathf.Max(max.y, t.y));
                    }

                    Debug.Log($"[wall] uv{ch} count={uv.Length} min=({min.x:0.00},{min.y:0.00}) max=({max.x:0.00},{max.y:0.00})");
                }
            }
        }

        static string Clean(string name)
        {
            const string suffix = " (Instance)";
            return name.EndsWith(suffix) ? name.Substring(0, name.Length - suffix.Length) : name;
        }

        /// <summary>Diagnostic: print what every renderer would resolve to.</summary>
        public static void Report(GameObject room)
        {
            var manifest = Load();
            if (manifest == null) return;
            var byName = new Dictionary<string, ManifestMaterial>();
            foreach (var m in manifest.materials) byName[m.name] = m;

            foreach (var renderer in room.GetComponentsInChildren<MeshRenderer>(true))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    var name = mat == null ? "<null>" : Clean(mat.name);
                    byName.TryGetValue(name, out var src);
                    Debug.Log($"[rebuild] {renderer.name} <- {name} image={(src == null ? "?" : src.image)} shader={(mat == null ? "?" : mat.shader.name)}");
                }
            }
        }
    }
}
