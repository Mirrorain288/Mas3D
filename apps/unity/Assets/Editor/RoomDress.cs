using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Mas3D.Editor
{
    /// <summary>
    /// C# port of apps/web/src/scene/roomDressing.ts: the imported MiSide room ships with
    /// three untextured white meshes and a near-white roof, so it reads as flat white slabs
    /// from inside. Nothing here is a geometry fix, so instead of rebuilding the .blend we
    /// repaint those meshes by name, swap the pitched roof for a flat ceiling, and add the
    /// night light rig the web scene uses.
    ///
    /// Mesh names come from the Blender scene; changing the asset means changing this table.
    /// </summary>
    public static class RoomDress
    {
        public const string RoofMesh = "Mesh_0116out";
        public const string FloorMesh = "Mesh_0140out";
        public const string WallMesh = "Mesh_0115out";

        /// <summary>One ceiling tile is about 0.75 m, matching the density of the old roof grid.</summary>
        public const float CeilingTileM = 0.75f;

        /// <summary>The ceiling plate overshoots the floor by this much on every side.</summary>
        public const float CeilingMargin = 0.06f;

        /// <summary>
        /// Triangles of the roof node whose top vertex sits more than this below the eave are
        /// the 0.75 m skirting of the four walls and must survive the flattening.
        /// </summary>
        public const float RoofSkirtDepth = 0.1f;

        public enum Kind
        {
            Keep,
            Ceiling,
            PaintedWhite,
            PaintedTrim,
            Wall,
            Mirror,
        }

        static readonly Dictionary<string, Kind> KindByMesh = new Dictionary<string, Kind>
        {
            // Roof incl. the wall skirting that shares the same node.
            { RoofMesh, Kind.Ceiling },
            // Small drawer unit next to the bed.
            { "Mesh_0138out", Kind.PaintedWhite },
            // Four-cell shelf on the wall by the window.
            { "Mesh_0200out", Kind.PaintedWhite },
            // Window casing plus the dead-white board above it.
            { "Mesh_0122out", Kind.PaintedTrim },
            // The wall shell keeps its glTFast material: the wallpaper is the room's identity
            // and rebuilding it in URP Lit sampled the wrong part of the atlas, turning the
            // pink clouds into a white tile grid. Relief on a wall this size is not worth it.
            // The wall shell keeps its glTFast material: rebuilding it in URP Lit samples the
            // wrong region of the packed atlas, so the pink clouds turn into a white tile grid.
            // { WallMesh, Kind.Wall },
            // The tall standing mirror.
            { "Mesh_0014out", Kind.Mirror },
        };

        public static Kind KindFor(string meshName)
        {
            Kind kind;
            return KindByMesh.TryGetValue(meshName, out kind) ? kind : Kind.Keep;
        }

        /// <summary>Light levels, exposed so a capture can sweep them without a recompile.</summary>
        [Serializable]
        public class Tuning
        {
            // Fixed against the web A+ build with the custom surface shader: the window light is
            // weak on purpose, the warm lamp and the ceiling plate carry the room.
            public float moon = 1.2f;
            public float lamp = 1.2f;
            public float glow = 2.0f;
            public float bounce = 0.9f;
            public float floorBounce = 0.55f;
            public float temperature = 14f;
            public float tint = 0f;
            public float postExposure = -1.2f;
            public float contrast = 10f;
            public float saturation = 60f;
            // A 2048 shadow map stretched over a 30 m shadow distance needs a bias far larger
            // than the default, or the parallel light smears acne across every flat wall.
            public float shadowBias = 0.06f;
            public float shadowNormalBias = 0.6f;
            public bool moonShadows = true;
        }

        public struct Room
        {
            public float eaveY;
            public int painted;
            public int strippedTriangles;
            public int keptTriangles;
            public GameObject flatCeiling;
            public List<Material> materials;
        }

        static string PbrPath(string name)
        {
            return "Assets/_Local/pbr/" + name + "@1k.png";
        }

        /// <summary>
        /// The PBR PNGs are copies of the WebP files the web build uses. Unity cannot decode
        /// WebP, so they arrive as ordinary textures and need their import settings fixed up:
        /// only the colour map is sRGB, the normal map is a normal map, everything repeats.
        /// </summary>
        public static int ConfigurePbrTextures(bool force = false)
        {
            var touched = 0;
            foreach (var b in new[] { "ceiling", "clay" })
            {
                foreach (var map in new[] { "color", "normal", "roughness" })
                {
                    var path = PbrPath(b + "-" + map);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                    {
                        Debug.LogWarning("[dress] no TextureImporter at " + path + " (asset missing?)");
                        continue;
                    }
                    var wantType = map == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    var wantSrgb = map == "color";
                    if (!force
                        && importer.textureType == wantType
                        && importer.sRGBTexture == wantSrgb
                        && importer.wrapMode == TextureWrapMode.Repeat
                        && importer.maxTextureSize == 1024)
                    {
                        continue;
                    }
                    importer.textureType = wantType;
                    importer.sRGBTexture = wantSrgb;
                    importer.wrapMode = TextureWrapMode.Repeat;
                    importer.anisoLevel = 8;
                    importer.maxTextureSize = 1024;
                    importer.mipmapEnabled = true;
                    importer.SaveAndReimport();
                    touched++;
                }
            }
            Debug.Log("[dress] pbr textures configured=" + touched);
            return touched;
        }

        static Texture2D LoadTexture(string name)
        {
            var path = PbrPath(name);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) Debug.LogWarning("[dress] missing texture " + path);
            return texture;
        }

        /// <summary>
        /// Height of the long side walls: the plate the new ceiling sits on. Measured from the
        /// wall mesh rather than hardcoded, because the web numbers are in a rotated frame.
        /// </summary>
        public static float MeasureEaveY(GameObject room, out float ridgeY)
        {
            ridgeY = 0f;
            var wall = FindByName(room, WallMesh);
            if (wall == null) wall = room;
            var filters = wall.GetComponentsInChildren<MeshFilter>(true);

            var minX = float.MaxValue; var maxX = float.MinValue;
            var minZ = float.MaxValue; var maxZ = float.MinValue;
            var minY = float.MaxValue; var maxY = float.MinValue;
            var samples = new List<Vector3>();
            foreach (var f in filters)
            {
                if (f.sharedMesh == null) continue;
                foreach (var v in f.sharedMesh.vertices)
                {
                    var p = f.transform.TransformPoint(v);
                    samples.Add(p);
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                }
            }
            if (samples.Count == 0) return 3f;

            // Split the shell into its four wall planes and take the top of each.
            var halfX = (maxX - minX) * 0.5f;
            var halfZ = (maxZ - minZ) * 0.5f;
            var cx = (minX + maxX) * 0.5f;
            var cz = (minZ + maxZ) * 0.5f;
            var tolerance = Mathf.Min(halfX, halfZ) * 0.08f;
            float topPlusX = minY, topMinusX = minY, topPlusZ = minY, topMinusZ = minY;
            foreach (var p in samples)
            {
                if (Mathf.Abs(p.x - maxX) <= tolerance && p.y > topPlusX) topPlusX = p.y;
                if (Mathf.Abs(p.x - minX) <= tolerance && p.y > topMinusX) topMinusX = p.y;
                if (Mathf.Abs(p.z - maxZ) <= tolerance && p.y > topPlusZ) topPlusZ = p.y;
                if (Mathf.Abs(p.z - minZ) <= tolerance && p.y > topMinusZ) topMinusZ = p.y;
            }

            ridgeY = Mathf.Max(Mathf.Max(topPlusX, topMinusX), Mathf.Max(topPlusZ, topMinusZ));
            // The gables sit on the *short* ends and rise to the ridge; the eaves are the
            // low pair. Take the lower of the two pairs as the eave height.
            var xPair = Mathf.Max(topPlusX, topMinusX);
            var zPair = Mathf.Max(topPlusZ, topMinusZ);
            var eave = Mathf.Min(xPair, zPair);
            Debug.Log($"[dress] wall shell x={minX:F2}..{maxX:F2} z={minZ:F2}..{maxZ:F2} y={minY:F2}..{maxY:F2}" +
                      $" tops(+x {topPlusX:F2} -x {topMinusX:F2} +z {topPlusZ:F2} -z {topMinusZ:F2})" +
                      $" eave={eave:F2} ridge={ridgeY:F2} centre=({cx:F2},{cz:F2})");
            return eave;
        }

        public static Room Apply(GameObject room, bool dress, bool addLights, Tuning tuning = null, bool probeSolid = false)
        {
            tuning = tuning ?? new Tuning();
            var result = new Room { materials = new List<Material>() };
            var floor = FindByName(room, FloorMesh);
            var bounds = floor != null ? RendererBounds(floor) : RendererBounds(room);
            float ridge;
            result.eaveY = MeasureEaveY(room, out ridge);

            if (!dress)
            {
                Debug.Log("[dress] dress=off, geometry untouched");
                return result;
            }

            ConfigurePbrTextures();

            var ceiling = NewLit("ceiling");
            RoomMaterials.SetAlbedo(ceiling, Hex(0xefe4d4));
            ceiling.SetFloat("_AmbientStrength", 2.6f);
            ceiling.SetFloat("_Smoothness", 0.08f);
            ceiling.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            var ceilingMap = LoadTexture("ceiling-color");
            var ceilingNormal = LoadTexture("ceiling-normal");
            RoomMaterials.SetAlbedoMap(ceiling, ceilingMap);
            RoomMaterials.SetNormalMap(ceiling, ceilingNormal, 0.6f);

            var clayNormal = LoadTexture("clay-normal");

            var paintedWhite = NewLit("paintedWhite");
            RoomMaterials.SetAlbedo(paintedWhite, Hex(0xf1eae2));
            paintedWhite.SetFloat("_Smoothness", 0.28f);
            paintedWhite.SetFloat("_Metallic", 0.02f);
            paintedWhite.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            RoomMaterials.SetNormalMap(paintedWhite, clayNormal, 0.45f);

            var paintedTrim = NewLit("paintedTrim");
            RoomMaterials.SetAlbedo(paintedTrim, Hex(0xe8dbcb));
            paintedTrim.SetFloat("_Smoothness", 0.16f);
            paintedTrim.SetFloat("_Metallic", 0f);
            paintedTrim.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            RoomMaterials.SetNormalMap(paintedTrim, clayNormal, 0.6f);

            // No reflection probe exists in this scene, so the glass look comes from a fresnel
            // sheen instead of a real reflection: flat grey reads as cardboard otherwise.
            var mirrorShader = Shader.Find("Mas3D/Mirror");
            var mirror = new Material(mirrorShader != null ? mirrorShader : Shader.Find(RoomMaterials.ShaderName))
            {
                name = "mirror",
            };
            if (mirror.HasProperty("_Color")) mirror.SetColor("_Color", Hex(0x76838e));
            if (mirror.HasProperty("_RimColor")) mirror.SetColor("_RimColor", Hex(0xdce8f4));
            if (mirror.HasProperty("_RimPower")) mirror.SetFloat("_RimPower", 3.2f);
            if (mirror.HasProperty("_RimStrength")) mirror.SetFloat("_RimStrength", 0.6f);
            if (mirror.HasProperty("_Sheen")) mirror.SetFloat("_Sheen", 0.4f);
            if (mirror.HasProperty("_Cull")) mirror.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

            result.materials.Add(ceiling);
            result.materials.Add(paintedWhite);
            result.materials.Add(paintedTrim);
            result.materials.Add(mirror);

            var byKind = new Dictionary<Kind, Material>
            {
                { Kind.Ceiling, ceiling },
                { Kind.PaintedWhite, paintedWhite },
                { Kind.PaintedTrim, paintedTrim },
                { Kind.Mirror, mirror },
            };

            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                var kind = KindFor(r.gameObject.name);
                if (kind == Kind.Keep) continue;
                if (kind == Kind.Wall)
                {
                    // The wallpaper is the room's identity: keep the original map and tint,
                    // only add a faint relief so the white band above it is not dead flat.
                    var source = r.sharedMaterial;
                    var wall = NewLit("wall");
                    if (source != null)
                    {
                        // glTFast hands the room over as Shader Graph materials whose slots are
                        // named baseColorTexture / baseColorFactor, not the URP Lit names.
                        var map = source.GetTexture("_BaseMap")
                                  ?? source.GetTexture("baseColorTexture")
                                  ?? source.mainTexture;
                        if (map != null) wall.SetTexture("_BaseMap", map);
                        if (source.HasProperty("_BaseColor")) wall.color = source.GetColor("_BaseColor");
                        else if (source.HasProperty("baseColorFactor")) wall.color = source.GetColor("baseColorFactor");
                        else if (source.HasProperty("_Color")) wall.color = source.GetColor("_Color");
                        // glTFast bakes the wall's tiling into the shader graph, so the URP Lit copy
                        // needs the repeat count spelled out; the shell spans ~7 m and the wallpaper
                        // reads at roughly 3 m per tile.
                        wall.SetTextureScale("_BaseMap", new Vector2(3f, 3f));
                        wall.SetTextureOffset("_BaseMap", Vector2.zero);
                    }
                    wall.SetFloat("_Smoothness", 0.08f);
                    wall.SetFloat("_Metallic", 0f);
                    // The shell is modelled to be seen from outside too; without this the room
                    // interior shows the camera background where the wall should be.
                    wall.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                    if (source != null)
                    {
                        foreach (var prop in source.GetTexturePropertyNames())
                        {
                            var tex = source.GetTexture(prop);
                            if (tex != null) Debug.Log($"[dress] wall tex [{prop}] = {tex.name} {tex.width}x{tex.height}");
                        }
                        Debug.Log($"[dress] wall color={source.color} mainTex={(source.mainTexture == null ? "none" : source.mainTexture.name)}");
                    }
                    Debug.Log($"[dress] wall source={(source == null ? "null" : source.shader.name)}" +
                              $" map={(source != null && source.HasProperty("_BaseMap") && source.GetTexture("_BaseMap") != null ? source.GetTexture("_BaseMap").name : "none")}" +
                              $" color={(source != null && source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor").ToString() : "n/a")}");
                    if (clayNormal != null)
                    {
                        wall.SetTexture("_BumpMap", clayNormal);
                        wall.SetFloat("_BumpScale", 0.14f);
                        wall.EnableKeyword("_NORMALMAP");
                    }
                    if (probeSolid)
                    {
                        wall.SetTexture("_BaseMap", null);
                        wall.color = Color.magenta;
                    }
                    r.sharedMaterial = wall;
                    var wallMap = wall.GetTexture("_BaseMap");
                    if (wallMap != null)
                    {
                        Debug.Log($"[dress] wall map asset='{UnityEditor.AssetDatabase.GetAssetPath(wallMap)}'" +
                                  $" filter={wallMap.filterMode} wrap={wallMap.wrapMode} aniso={wallMap.anisoLevel} mips={wallMap.mipmapCount}");
                    }
                    Debug.Log($"[dress] wall applied shader={wall.shader.name}" +
                              $" map={(wallMap == null ? "none" : wallMap.name + " " + wallMap.width + "x" + wallMap.height)}" +
                              $" st={wall.GetTextureScale("_BaseMap")} offset={wall.GetTextureOffset("_BaseMap")}" +
                              $" color={wall.color} bump={(wall.GetTexture("_BumpMap") == null ? "none" : wall.GetTexture("_BumpMap").name)}");
                    result.materials.Add(wall);
                    result.painted++;
                    continue;
                }
                var count = Mathf.Max(1, r.sharedMaterials.Length);
                var list = new Material[count];
                for (var i = 0; i < count; i++) list[i] = byKind[kind];
                r.sharedMaterials = list;
                result.painted++;
                var applied = list[0].GetTexture("_BaseMap");
                Debug.Log($"[dress] {r.gameObject.name} -> {kind} mat={list[0].name} slots={count}" +
                          $" map={(applied == null ? "none" : applied.name + " st=" + list[0].GetTextureScale("_BaseMap"))}");
            }

            // Pitched roof -> flat plate at the eave line. The gable triangles above survive
            // but are hidden behind the plate, so they never show from inside.
            var roof = FindByName(room, RoofMesh);
            if (roof != null)
            {
                var filter = roof.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    int kept;
                    var removed = StripSlope(filter, result.eaveY - RoofSkirtDepth, out kept);
                    result.strippedTriangles = removed;
                    result.keptTriangles = kept;
                    var renderer = roof.GetComponent<Renderer>();
                    if (renderer != null && renderer.sharedMaterials.Length > 1 && kept > 0)
                    {
                        var trimmed = new Material[Mathf.Min(renderer.sharedMaterials.Length, 1)];
                        for (var i = 0; i < trimmed.Length; i++) trimmed[i] = renderer.sharedMaterials[i];
                        renderer.sharedMaterials = trimmed;
                    }
                }
            }

            result.flatCeiling = BuildFlatCeiling(room, ceiling, bounds, result.eaveY);

            if (addLights) CreateLights(room, bounds, result.eaveY, tuning);

            Debug.Log($"[dress] painted={result.painted} roof triangles removed={result.strippedTriangles}" +
                      $" kept={result.keptTriangles} eaveY={result.eaveY:F2}" +
                      $" ceiling={(result.flatCeiling == null ? "none" : result.flatCeiling.transform.position.ToString("F2"))}");
            return result;
        }

        /// <summary>
        /// Drops the pitched roof triangles of the roof node but keeps the wall skirting that
        /// shares it: everything whose highest vertex is above <paramref name="maxWorldY"/>
        /// goes, the rest is rebuilt into a fresh mesh.
        /// </summary>
        public static int StripSlope(MeshFilter filter, float maxWorldY, out int keptTriangles)
        {
            keptTriangles = 0;
            var mesh = filter.sharedMesh;
            if (mesh == null) return 0;
            var tf = filter.transform;
            var vertices = mesh.vertices;
            var tops = new float[vertices.Length];
            for (var i = 0; i < vertices.Length; i++) tops[i] = tf.TransformPoint(vertices[i]).y;

            var total = 0;
            var kept = new List<int>();
            var keptPerSubmesh = new List<int[]>();
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var triangles = mesh.GetTriangles(s);
                var list = new List<int>(triangles.Length);
                for (var t = 0; t + 2 < triangles.Length; t += 3)
                {
                    total++;
                    var top = Mathf.Max(tops[triangles[t]], Mathf.Max(tops[triangles[t + 1]], tops[triangles[t + 2]]));
                    if (top > maxWorldY) continue;
                    list.Add(triangles[t]);
                    list.Add(triangles[t + 1]);
                    list.Add(triangles[t + 2]);
                }
                keptPerSubmesh.Add(list.ToArray());
                kept.AddRange(list);
            }
            keptTriangles = kept.Count / 3;
            if (kept.Count >= total * 3) return 0;

            var next = new Mesh { name = mesh.name + "-dress" };
            next.indexFormat = vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            next.SetVertices(new List<Vector3>(vertices));
            if (mesh.normals != null && mesh.normals.Length > 0) next.SetNormals(new List<Vector3>(mesh.normals));
            if (mesh.tangents != null && mesh.tangents.Length > 0) next.SetTangents(new List<Vector4>(mesh.tangents));
            if (mesh.uv != null && mesh.uv.Length > 0) next.SetUVs(0, new List<Vector2>(mesh.uv));
            if (mesh.uv2 != null && mesh.uv2.Length > 0) next.SetUVs(1, new List<Vector2>(mesh.uv2));
            if (mesh.colors != null && mesh.colors.Length > 0) next.SetColors(new List<Color>(mesh.colors));

            var live = new List<int[]>();
            foreach (var arr in keptPerSubmesh)
                if (arr.Length > 0) live.Add(arr);
            next.subMeshCount = live.Count;
            for (var i = 0; i < live.Count; i++) next.SetTriangles(live[i], i, false);
            next.RecalculateBounds();
            filter.sharedMesh = next;

            Debug.Log($"[dress] roof strip: {total - keptTriangles} of {total} triangles above y={maxWorldY:F2} removed");
            return total - keptTriangles;
        }

        public static GameObject BuildFlatCeiling(GameObject room, Material material, Bounds floorBounds, float eaveWorldY)
        {
            var width = floorBounds.size.x + CeilingMargin * 2f;
            var depth = floorBounds.size.z + CeilingMargin * 2f;
            var centre = new Vector3(floorBounds.center.x, eaveWorldY, floorBounds.center.z);

            var mesh = new Mesh { name = "flat-ceiling" };
            var half = new Vector2(width * 0.5f, depth * 0.5f);
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-half.x, 0f, -half.y),
                new Vector3(-half.x, 0f, half.y),
                new Vector3(half.x, 0f, half.y),
                new Vector3(half.x, 0f, -half.y),
            });
            // Facing down: the room is only ever seen from below the plate, and a lit-from-below
            // surface needs the normal that points at the lights.
            mesh.SetNormals(new List<Vector3> { Vector3.down, Vector3.down, Vector3.down, Vector3.down });
            var repeatX = width / CeilingTileM;
            var repeatY = depth / CeilingTileM;
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0f),
                new Vector2(0f, repeatY),
                new Vector2(repeatX, repeatY),
                new Vector2(repeatX, 0f),
            });
            mesh.SetTriangles(new List<int> { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.RecalculateBounds();

            var go = new GameObject("flat-ceiling");
            go.transform.SetParent(room.transform, false);
            go.transform.position = centre;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            Debug.Log($"[dress] flat ceiling {width:F2} x {depth:F2} at {centre:F2} uv=({repeatX:F2},{repeatY:F2})");
            return go;
        }

        /// <summary>
        /// Night rig, ported from createRoomLights(): a cold moon through the window as key,
        /// a warm lamp as fill, a weak cold hemisphere, and a warm glow under the ceiling.
        /// </summary>
        public static void CreateLights(GameObject room, Bounds bounds, float eaveY, Tuning tuning)
        {
            var root = new GameObject("room-lights");

            // Window sits on the +x side of the imported room, so the key comes in from there.
            var moonGo = new GameObject("moon");
            moonGo.transform.SetParent(root.transform, false);
            moonGo.transform.position = new Vector3(bounds.max.x + 3.2f, eaveY + 0.9f, bounds.center.z + 1.6f);
            moonGo.transform.LookAt(new Vector3(bounds.center.x, 0.5f, bounds.center.z));
            var moon = moonGo.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = Hex(0xbcd6ff);
            moon.intensity = tuning.moon;
            moon.shadows = tuning.moonShadows ? LightShadows.Soft : LightShadows.None;
            moon.shadowStrength = 0.85f;
            moon.shadowBias = tuning.shadowBias;
            moon.shadowNormalBias = tuning.shadowNormalBias;

            var lampGo = new GameObject("bedside-lamp");
            lampGo.transform.SetParent(root.transform, false);
            lampGo.transform.position = new Vector3(bounds.center.x - bounds.size.x * 0.12f, 1.05f, bounds.center.z + bounds.size.z * 0.18f);
            var lamp = lampGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = Hex(0xffb877);
            lamp.intensity = tuning.lamp;
            lamp.range = 6f;
            lamp.shadows = LightShadows.None;

            // Soft warm plate light under the ceiling; no shadows, otherwise the plate
            // throws the whole room into shade.
            // One lamp right under the plate burns a white hole in the ceiling, so the glow is
            // split into a pair hung ~1.2 m below it: same total lift, much flatter falloff.
            Light glow = null;
            for (var i = 0; i < 2; i++)
            {
                var glowGo = new GameObject("ceiling-glow-" + (i + 1));
                glowGo.transform.SetParent(root.transform, false);
                var offset = (i == 0 ? -1f : 1f) * bounds.size.x * 0.22f;
                // Hung low and reaching far: the plate still lifts the room, but the falloff
                // is flat enough that the ceiling stops showing a hot spot.
                glowGo.transform.position = new Vector3(bounds.center.x + offset, eaveY - 1.75f, bounds.center.z);
                var light = glowGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = Hex(0xffdcb8);
                light.intensity = tuning.glow;
                light.range = 13f;
                light.shadows = LightShadows.None;
                if (glow == null) glow = light;
                else _glows.Add(light);
            }

            // The wood floor sits below every lamp, so it renders muddy without a low warm
            // source that only the floor plane can see.
            var floorGo = new GameObject("floor-bounce");
            floorGo.transform.SetParent(root.transform, false);
            floorGo.transform.position = new Vector3(bounds.center.x, 0.32f, bounds.center.z);
            var floorLight = floorGo.AddComponent<Light>();
            floorLight.type = LightType.Point;
            floorLight.color = Hex(0xffe6cc);
            floorLight.intensity = tuning.floorBounce;
            floorLight.range = 8.5f;
            floorLight.shadows = LightShadows.None;

            var fillGo = new GameObject("bounce");
            fillGo.transform.SetParent(root.transform, false);
            fillGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.color = Hex(0x8fa6d8);
            fill.intensity = tuning.bounce;
            fill.shadows = LightShadows.None;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Hex(0x565a78);
            RenderSettings.ambientEquatorColor = Hex(0x5a4148);
            RenderSettings.ambientGroundColor = Hex(0x2b2130);
            RenderSettings.fog = false;

            _moon = moon;
            _lamp = lamp;
            _glow = glow;
            _fill = fill;
            _floor = floorLight;
            Debug.Log($"[dress] lights moon={moon.intensity} lamp={lamp.intensity} glow={glow.intensity} eaveY={eaveY:F2}");
        }

        /// <summary>
        /// The web scene renders through ACES tone mapping; URP without it clips every lit
        /// surface to white, which is what made the first Unity captures look washed out.
        /// A volume profile never reaches this camera, so exposure and grading are pushed as
        /// globals that the room shaders read before their own ACES curve.
        /// </summary>
        public static void ApplyColour(Tuning tuning)
        {
            Shader.SetGlobalFloat("_RoomExposure", Mathf.Pow(2f, tuning.postExposure));
            Shader.SetGlobalFloat("_RoomSaturation", Mathf.Clamp01(1f + tuning.saturation / 100f));
            Debug.Log($"[dress] colour exposure={tuning.postExposure} curve={Mathf.Pow(2f, tuning.postExposure):F2} saturation={tuning.saturation}");
        }

        public const string SkyPath = "Assets/_Local/sky/night-sky-2k.jpg";

        /// <summary>
        /// The room is closed except for the window, so whatever the camera clears to is the
        /// night outside. Uses the same HDRI the web build has behind its window.
        /// </summary>
        public static bool ApplyNightSky()
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyPath);
            if (tex == null)
            {
                Debug.Log("[sky] missing " + SkyPath);
                return false;
            }

            var importer = AssetImporter.GetAtPath(SkyPath) as TextureImporter;
            if (importer != null && (importer.maxTextureSize < 2048 || importer.mipmapEnabled || importer.wrapMode != TextureWrapMode.Clamp))
            {
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.sRGBTexture = true;
                importer.maxTextureSize = 2048;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyPath);
            }

            Debug.Log($"[sky] ready {tex.width}x{tex.height}");
            return true;
        }

        /// <summary>
        /// A huge unlit card parked just outside the window wall. Assigning RenderSettings.skybox
        /// would also feed URP's default reflection probe, and every glossy glTFast surface in the
        /// room then mirrored the star field as blocky white patches; a card is invisible to
        /// lighting and only shows through the window hole.
        /// </summary>
        public static GameObject BuildSkyCard(Bounds bounds, float wallX)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyPath);
            if (tex == null)
            {
                Debug.Log("[sky] missing " + SkyPath);
                return null;
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            var mat = new Material(shader) { name = "night-sky-card" };
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(1.35f, 1.35f, 1.5f, 1f));
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(1.35f, 1.35f, 1.5f, 1f));
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

            var card = GameObject.CreatePrimitive(PrimitiveType.Quad);
            card.name = "night-sky-card";
            var collider = card.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            card.GetComponent<MeshRenderer>().sharedMaterial = mat;
            card.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            card.GetComponent<MeshRenderer>().receiveShadows = false;

            var height = bounds.max.y - 0.4f;
            card.transform.position = new Vector3(wallX + 0.55f, height * 0.55f, bounds.center.z);
            card.transform.rotation = Quaternion.Euler(0f, -90f, 0f);
            card.transform.localScale = new Vector3(bounds.size.z * 1.15f, height * 1.1f, 1f);
            Debug.Log($"[sky] card {card.transform.localScale.x:F2}x{card.transform.localScale.y:F2} at x={card.transform.position.x:F2}");
            return card;
        }

        static Light _moon;
        static Light _lamp;
        static Light _glow;
        static readonly List<Light> _glows = new List<Light>();
        static Light _floor;
        static Light _fill;

        /// <summary>Re-applies light levels and grade without rebuilding the room, for sweeps.</summary>
        public static void Retune(Tuning t)
        {
            if (_moon != null)
            {
                _moon.intensity = t.moon;
                _moon.shadows = t.moonShadows ? LightShadows.Soft : LightShadows.None;
                _moon.shadowBias = t.shadowBias;
                _moon.shadowNormalBias = t.shadowNormalBias;
            }
            if (_lamp != null) _lamp.intensity = t.lamp;
            if (_glow != null) _glow.intensity = t.glow;
            foreach (var extra in _glows) extra.intensity = t.glow;
            if (_fill != null) _fill.intensity = t.bounce;
            if (_floor != null) _floor.intensity = t.floorBounce;
            ApplyColour(t);
        }

        static Material NewLit(string name)
        {
            // Same shader as the rebuilt room: URP Lit loses the map at grazing angles in this
            // editor build (see RoomMaterials.CustomShader).
            var shader = Shader.Find(RoomMaterials.ShaderName);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[dress] no usable shader for " + name);
                shader = Shader.Find("Standard");
            }
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_AmbientStrength")) mat.SetFloat("_AmbientStrength", RoomMaterials.DefaultAmbient);
            return mat;
        }

        public static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
        }

        public static GameObject FindByName(GameObject root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.gameObject.name, name, StringComparison.Ordinal)) return t.gameObject;
            return null;
        }

        public static Bounds RendererBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.zero);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }
    }
}
