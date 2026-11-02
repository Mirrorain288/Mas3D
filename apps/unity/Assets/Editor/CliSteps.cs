using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

#if MAS3D_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace Mas3D.Editor
{
    /// <summary>
    /// Project setup steps that need URP or scene APIs. Guarded by MAS3D_URP so the
    /// assembly still compiles before the render pipeline package is resolved.
    /// </summary>
    public static class CliSteps
    {
        const string SettingsDir = "Assets/Settings";
        const string PipelinePath = SettingsDir + "/Mas3D-URP.asset";
        const string RendererPath = SettingsDir + "/Mas3D-URP-Renderer.asset";
        const string ScenePath = "Assets/Scenes/Main.unity";

        /// <summary>Creates the URP pipeline asset and makes it the default for quality levels.</summary>
        public static void MakeUrp()
        {
#if MAS3D_URP
            EnsureFolder(SettingsDir);
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.name = "Mas3D-URP-Renderer";
                AssetDatabase.CreateAsset(renderer, RendererPath);
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.name = "Mas3D-URP";
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }

            pipeline.supportsHDR = true;
            pipeline.msaaSampleCount = 4;
            pipeline.shadowDistance = 30f;
            pipeline.renderScale = 1f;

            GraphicsSettings.defaultRenderPipeline = pipeline;
            for (var i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }

            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            Debug.Log("[cli] urp ready: " + AssetDatabase.GetAssetPath(pipeline));
            EditorApplication.Exit(0);
#else
            Debug.LogError("[cli] MAS3D_URP is not defined; run Cli.EnsurePackages first");
            EditorApplication.Exit(1);
#endif
        }

        /// <summary>Builds the bootstrap scene: camera, moon light, room root, player later.</summary>
        public static void CreateMainScene()
        {
            EnsureFolder("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 120f;
            camera.fieldOfView = 42f;
            cameraGo.transform.position = new Vector3(0f, 1.6f, 3.2f);
            cameraGo.transform.rotation = Quaternion.Euler(2f, 180f, 0f);
#if MAS3D_URP
            cameraGo.AddComponent<UniversalAdditionalCameraData>();
#endif

            var lightGo = new GameObject("Moon");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.74f, 0.84f, 1f);
            light.intensity = 2.1f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(32f, 168f, 0f);

            var session = new GameObject("Session");
            session.AddComponent<Transform>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[cli] scene saved: " + ScenePath);
            EditorApplication.Exit(0);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = path.Substring(0, path.LastIndexOf('/'));
            var leaf = path.Substring(path.LastIndexOf('/') + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
