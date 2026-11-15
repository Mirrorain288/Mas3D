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
        public string room;
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
        public string room;
        public string pose;
        public float standGap;
        public bool intimate;
        public float x;
        public float y;
        public float z;
        public float topY;
        public float facingY;
    }

    [Serializable]
    public class WaypointEntry
    {
        public string id;
        public string room;
        public float x;
        public float z;
        public float facingY;

        public Vector3 Feet(float y = 0.02f) => new Vector3(x, y, z);
    }

    [Serializable]
    public class LinkEntry
    {
        public string a;
        public string b;
        public string doorId;
    }

    [Serializable]
    public class SpawnEntry
    {
        public string id;
        public string room;
        public float x;
        public float y;
        public float z;
        public float facingY;

        public bool HasValue => !string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(room) || x != 0f || z != 0f;

        public Vector3 Feet(float defaultY = 0.02f) => new Vector3(x, y != 0f ? y : defaultY, z);
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
        public SpawnEntry defaultSpawn;
        public AnchorEntry[] anchors;
        public SeatEntry[] seats;
        public WaypointEntry[] waypoints;
        public LinkEntry[] links;

        public const string DefaultRelativePath = "mas3d/anchors.json";
        public const string FlatRelativePath = "mas3d/anchors-flat.json";

        static string _activeRelativePath;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticOnDomainLoad() => _activeRelativePath = null;

        public static string ActiveRelativePath =>
            string.IsNullOrEmpty(_activeRelativePath) ? DefaultRelativePath : _activeRelativePath;

        public static string DefaultPath => Path.Combine(Application.streamingAssetsPath, ActiveRelativePath);

        public static void SetActiveRelativePath(string relativePath)
        {
            _activeRelativePath = string.IsNullOrEmpty(relativePath) ? null : relativePath;
        }

        public static void ResetActive()
        {
            _activeRelativePath = null;
        }

        public AnchorEntry FindAnchor(string id)
        {
            if (anchors == null || string.IsNullOrEmpty(id)) return null;
            for (var i = 0; i < anchors.Length; i++)
            {
                if (anchors[i] != null && string.Equals(anchors[i].id, id, StringComparison.OrdinalIgnoreCase))
                    return anchors[i];
            }
            return null;
        }

        public SeatEntry FindSeat(string id)
        {
            if (seats == null || string.IsNullOrEmpty(id)) return null;
            for (var i = 0; i < seats.Length; i++)
            {
                if (seats[i] != null && string.Equals(seats[i].id, id, StringComparison.OrdinalIgnoreCase))
                    return seats[i];
            }
            return null;
        }

        public WaypointEntry FindWaypoint(string id)
        {
            if (waypoints == null || string.IsNullOrEmpty(id)) return null;
            for (var i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i] != null && string.Equals(waypoints[i].id, id, StringComparison.OrdinalIgnoreCase))
                    return waypoints[i];
            }
            return null;
        }

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
