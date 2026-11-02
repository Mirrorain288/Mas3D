using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Headless entry points. Every method here is idempotent and callable as
    /// `Unity.exe -batchmode -quit -executeMethod Mas3D.Editor.Cli.&lt;Name&gt;`.
    /// Exit code 0 means the step did what it promised, 1 means it did not.
    /// </summary>
    public static class Cli
    {
        /// <summary>Packages the project needs before any of the other steps can run.</summary>
        static readonly string[] RequiredPackages =
        {
            "com.unity.render-pipelines.universal",
            "com.unity.cinemachine",
            "com.unity.cloud.gltfast",
        };

        public static void SelfCheck()
        {
            var ok = true;
            Debug.Log($"[cli] editor {Application.unityVersion}");
            foreach (var name in RequiredPackages)
            {
                var found = FindPackage(name);
                Debug.Log($"[cli] package {name}: {(found == null ? "MISSING" : found.version)}");
                if (found == null) ok = false;
            }
            Finish(ok);
        }

        /// <summary>Adds the required packages and waits for resolution, then exits.</summary>
        public static void EnsurePackages()
        {
            var added = new List<string>();
            foreach (var name in RequiredPackages)
            {
                if (FindPackage(name) != null) { Debug.Log($"[cli] {name} already present"); continue; }
                var request = Client.Add(name);
                while (!request.IsCompleted) Thread.Sleep(200);
                if (request.Status == StatusCode.Success)
                {
                    Debug.Log($"[cli] added {request.Result.packageId}");
                    added.Add(request.Result.packageId);
                }
                else
                {
                    Debug.LogError($"[cli] failed to add {name}: {request.Error?.message}");
                }
            }
            Finish(added.Count > 0 || RequiredPackages.Length > 0);
        }

        static UnityEditor.PackageManager.PackageInfo FindPackage(string name)
        {
            foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                if (p.name == name) return p;
            return null;
        }

        /// <summary>Exits with 0 when the whole CLI surface is healthy.</summary>
        static void Finish(bool ok)
        {
            Debug.Log($"[cli] done, ok={ok}");
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
