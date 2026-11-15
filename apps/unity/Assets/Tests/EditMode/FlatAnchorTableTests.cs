using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Mas3D.Tests
{
    public class FlatAnchorTableTests
    {
        [TearDown]
        public void TearDown()
        {
            AnchorTable.ResetActive();
        }

        [Test]
        public void ActiveRelativePathSwitchesBetweenDefaultAndFlatAndResets()
        {
            Assert.AreEqual("mas3d/anchors.json", AnchorTable.DefaultRelativePath);
            Assert.AreEqual("mas3d/anchors-flat.json", AnchorTable.FlatRelativePath);
            Assert.AreEqual(AnchorTable.DefaultRelativePath, AnchorTable.ActiveRelativePath);
            Assert.AreEqual(
                Path.Combine(Application.streamingAssetsPath, AnchorTable.DefaultRelativePath),
                AnchorTable.DefaultPath);

            AnchorTable.SetActiveRelativePath(AnchorTable.FlatRelativePath);
            Assert.AreEqual(AnchorTable.FlatRelativePath, AnchorTable.ActiveRelativePath);
            Assert.AreEqual(
                Path.Combine(Application.streamingAssetsPath, AnchorTable.FlatRelativePath),
                AnchorTable.DefaultPath);

            AnchorTable.ResetActive();
            Assert.AreEqual(AnchorTable.DefaultRelativePath, AnchorTable.ActiveRelativePath);
        }

        [Test]
        public void ParsesLegacyJsonAndExtendedFlatSchemaWithOptionalFields()
        {
            const string legacyJson = @"{
                ""space"": ""room"",
                ""anchors"": [{ ""id"": ""A1"", ""role"": ""stand"", ""x"": 1.0, ""z"": -1.0, ""facingY"": 90.0 }],
                ""seats"": [{ ""id"": ""S1"", ""mesh"": ""chair"", ""x"": 0.5, ""y"": 0.4, ""z"": 0.2, ""topY"": 0.42, ""facingY"": 180.0 }]
            }";

            var legacy = JsonUtility.FromJson<AnchorTable>(legacyJson);
            Assert.IsNotNull(legacy);
            Assert.AreEqual("room", legacy.space);
            Assert.AreEqual(1, legacy.anchors.Length);
            Assert.IsTrue(string.IsNullOrEmpty(legacy.anchors[0].room));
            Assert.AreEqual(1, legacy.seats.Length);
            Assert.IsFalse(legacy.seats[0].intimate);
            Assert.IsTrue(legacy.waypoints == null || legacy.waypoints.Length == 0);
            Assert.IsTrue(legacy.links == null || legacy.links.Length == 0);

            const string flatJson = @"{
                ""space"": ""flat"",
                ""defaultSpawn"": { ""id"": ""player-living"", ""room"": ""living"", ""x"": 2.5, ""y"": 0.02, ""z"": -2.1, ""facingY"": 0.0 },
                ""anchors"": [{ ""id"": ""A1"", ""role"": ""sofa-front"", ""room"": ""living"", ""x"": 2.5, ""z"": -1.2, ""facingY"": 0.0 }],
                ""seats"": [{ ""id"": ""S1"", ""mesh"": ""mesh_218.002"", ""room"": ""living"", ""pose"": ""sofa"", ""standGap"": 0.85, ""intimate"": true, ""x"": 2.5, ""y"": 0.45, ""z"": 0.0, ""topY"": 0.46, ""facingY"": 180.0 }],
                ""waypoints"": [{ ""id"": ""D_BL_B"", ""room"": ""bedroom"", ""x"": -2.1, ""z"": -0.3, ""facingY"": 90.0 }],
                ""links"": [{ ""a"": ""A1"", ""b"": ""D_BL_B"", ""doorId"": ""Door_BedroomLiving"" }]
            }";

            var flat = JsonUtility.FromJson<AnchorTable>(flatJson);
            Assert.IsNotNull(flat);
            Assert.AreEqual("flat", flat.space);
            Assert.IsNotNull(flat.defaultSpawn);
            Assert.AreEqual("living", flat.defaultSpawn.room);
            Assert.AreEqual(2.5f, flat.defaultSpawn.x, 1e-4f);
            Assert.AreEqual(-2.1f, flat.defaultSpawn.z, 1e-4f);
            Assert.AreEqual("living", flat.anchors[0].room);
            Assert.AreEqual("living", flat.seats[0].room);
            Assert.AreEqual("sofa", flat.seats[0].pose);
            Assert.AreEqual(0.85f, flat.seats[0].standGap, 1e-4f);
            Assert.IsTrue(flat.seats[0].intimate);
            Assert.AreEqual(1, flat.waypoints.Length);
            Assert.AreEqual("D_BL_B", flat.waypoints[0].id);
            Assert.AreEqual("bedroom", flat.waypoints[0].room);
            Assert.AreEqual(1, flat.links.Length);
            Assert.AreEqual("Door_BedroomLiving", flat.links[0].doorId);
        }
    }
}
