using System.IO;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// The one channel that survives to the PowerShell side: Debug.Log does not reach the NUnit XML,
    /// and the CLI keeps the batchmode console elsewhere, so a smoke run proves itself by the lines it
    /// writes here. Paths are relative to the project root (Application.dataPath's parent).
    /// </summary>
    internal static class SmokeMarker
    {
        internal static void Write(string relative, string line)
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, line + System.Environment.NewLine);
            Debug.Log(line);
        }
    }
}
