using System;
using System.IO;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// The interaction anchors solved from the room geometry by Mas3D.Editor.CliAnchors and shipped
    /// next to the build. Runtime code never recomputes them; it reads the table so the anchors the
    /// tests verified are the anchors the game uses.
    /// </summary>
    [Serializable]
    public class AnchorEntry
    {
        public string id;
        public string role;
        public float x;
        public float z;
        public float facingY;
        public float wallClear;
        public float nearestClear;

        public Vector3 Feet(float y = 0.02f) => new Vector3(x, y, z);
    }

    [Serializable]
    public class SeatEntry
    {
        public string id;
        public string mesh;
        public float x;
        public float y;
        public float z;
        public float topY;
        public float facingY;
    }

    [Serializable]
    public class InteriorRect
    {
        public float x0;
        public float x1;
        public float z0;
        public float z1;

        public bool Contains(Vector2 point) => point.x >= x0 && point.x <= x1 && point.y >= z0 && point.y <= z1;
        public Vector2 Centre => new Vector2((x0 + x1) * 0.5f, (z0 + z1) * 0.5f);
    }

    [Serializable]
    public class AnchorTable
    {
        public string space;
        public InteriorRect interior;
        public AnchorEntry[] anchors;
        public SeatEntry[] seats;

        public const string DefaultRelativePath = "mas3d/anchors.json";

        public static string DefaultPath => Path.Combine(Application.streamingAssetsPath, DefaultRelativePath);

        public static AnchorTable Load(string path = null)
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file))
            {
                Debug.LogWarning("[anchors] table not found: " + file);
                return null;
            }
            return JsonUtility.FromJson<AnchorTable>(File.ReadAllText(file));
        }

        public AnchorEntry Anchor(string id)
        {
            if (anchors == null) return null;
            foreach (var anchor in anchors)
                if (string.Equals(anchor.id, id, StringComparison.OrdinalIgnoreCase))
                    return anchor;
            return null;
        }

        public SeatEntry Seat(string id)
        {
            if (seats == null) return null;
            foreach (var seat in seats)
                if (string.Equals(seat.id, id, StringComparison.OrdinalIgnoreCase))
                    return seat;
            return null;
        }
    }
}
