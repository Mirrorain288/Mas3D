using System.Collections.Generic;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// Gives the imported room real colliders: glTFast brings geometry and materials, not physics.
    ///
    /// The imported shell (floor, walls, roof) is authored as a closed box with outward normals,
    /// and a non-convex MeshCollider is one-sided for sweeps: a CharacterController standing inside
    /// such a room sweeps through the floor and the walls. The shell therefore gets a second,
    /// reversed-winding collider on a child object so the room is solid from the inside too, while
    /// furniture keeps its single outward-facing collider.
    /// </summary>
    public static class RoomColliders
    {
        // Boundary meshes whose inside surface is what the player walks on.
        static readonly HashSet<string> Shell = new HashSet<string>
        {
            "Mesh_0115out", // walls
            "Mesh_0116out", // roof skirt
            "Mesh_0140out", // floor
        };

        static readonly Dictionary<string, Mesh> Flipped = new Dictionary<string, Mesh>();

        public static int Attach(GameObject room, bool twoSidedShell = true)
        {
            if (room == null) return 0;
            var count = 0;
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                if (filter.GetComponent<Collider>() == null)
                {
                    var mesh = filter.gameObject.AddComponent<MeshCollider>();
                    mesh.sharedMesh = filter.sharedMesh;
                    mesh.convex = false;
                }
                count++;

                if (!twoSidedShell || !Shell.Contains(filter.gameObject.name)) continue;
                if (filter.transform.Find("inner-collider") != null) continue;

                var inner = new GameObject("inner-collider");
                inner.transform.SetParent(filter.transform, false);
                var collider = inner.AddComponent<MeshCollider>();
                collider.sharedMesh = FlippedMesh(filter.sharedMesh);
                collider.convex = false;
            }
            return count;
        }

        /// <summary>Same geometry, opposite winding: the same triangles seen from the other side.</summary>
        static Mesh FlippedMesh(Mesh source)
        {
            // Unity 6.6 turns GetInstanceID() into a compile error; the shell mesh names are unique.
            var key = source.name;
            if (Flipped.TryGetValue(key, out var cached) && cached != null) return cached;

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
            Flipped[key] = mesh;
            return mesh;
        }

        public static void Forget()
        {
            Flipped.Clear();
        }
    }
}