using System;
using System.IO;
using Mas3D.Room;
using NUnit.Framework;
using UnityEngine;

namespace Mas3D.Tests
{
    public class FlatAnchorTests
    {
        static string AnchorsFlatFile =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Anchors", "anchors-flat.json"));

        static string StreamingFlatFile =>
            Path.Combine(Application.streamingAssetsPath, AnchorTable.FlatRelativePath);

        [TearDown]
        public void TearDown()
        {
            AnchorTable.ResetActive();
            EdgeTable.Rebuild();
        }

        [Test]
        public void GeneratesFlatAnchorsWithLivingBedroomKitchenAndZeroBathroomNodes()
        {
            Assert.IsTrue(File.Exists(AnchorsFlatFile), "缺 Anchors/anchors-flat.json，需先运行 unity.ps1 flatanchors");
            Assert.IsTrue(File.Exists(StreamingFlatFile), "缺 StreamingAssets/mas3d/anchors-flat.json");

            var table = AnchorTable.Load(AnchorsFlatFile);
            Assert.IsNotNull(table);
            Assert.AreEqual("flat", table.space);
            Assert.IsNotNull(table.defaultSpawn);
            Assert.IsTrue(table.defaultSpawn.HasValue);
            Assert.AreEqual("living", table.defaultSpawn.room);

            var s1 = table.FindSeat("S1");
            Assert.IsNotNull(s1, "平层必须定义客厅大沙发主座 S1");
            Assert.AreEqual("living", s1.room);
            Assert.AreEqual("sofa", s1.pose);
            Assert.IsTrue(s1.intimate, "客厅主沙发 S1 必须允许 1-5 亲密动作");

            var s2 = table.FindSeat("S2");
            Assert.IsNotNull(s2, "平层必须定义客厅大沙发副座 S2");
            Assert.AreEqual("living", s2.room);

            bool hasBedroom = false, hasLiving = false, hasKitchen = false;
            if (table.anchors != null)
            {
                foreach (var a in table.anchors)
                {
                    Assert.AreNotEqual("bathroom", a.room, "Step 1 不得包含卫浴节点: " + a.id);
                    Assert.Greater(a.x, -7.80f, "锚点落在卫浴区间: " + a.id);
                    if (a.room == "bedroom") hasBedroom = true;
                    if (a.room == "living") hasLiving = true;
                    if (a.room == "kitchen") hasKitchen = true;
                }
            }
            if (table.seats != null)
            {
                foreach (var s in table.seats)
                {
                    Assert.AreNotEqual("bathroom", s.room, "Step 1 不得包含卫浴座位: " + s.id);
                    Assert.Greater(s.x, -7.80f, "座位落在卫浴区间: " + s.id);
                    if (s.room == "bedroom") hasBedroom = true;
                    if (s.room == "living") hasLiving = true;
                    if (s.room == "kitchen") hasKitchen = true;
                }
            }

            Assert.IsTrue(hasBedroom, "平层需涵盖卧室生活区");
            Assert.IsTrue(hasLiving, "平层需涵盖客厅生活区");
            Assert.IsTrue(hasKitchen, "平层需涵盖餐厨生活区");

            bool hasDoorBL = false, hasDoorLK = false;
            Assert.IsNotNull(table.links);
            foreach (var link in table.links)
            {
                Assert.AreNotEqual("Door_BathBedroom", link.doorId, "Step 1 卫浴门不得纳入走位图");
                if (link.doorId == "Door_BedroomLiving") hasDoorBL = true;
                if (link.doorId == "Door_LivingKitchen") hasDoorLK = true;
            }
            Assert.IsTrue(hasDoorBL, "缺少 Door_BedroomLiving 跨门洞边");
            Assert.IsTrue(hasDoorLK, "缺少 Door_LivingKitchen 跨门洞边");
        }

        [Test]
        public void DefaultSpawnAndMonikaS1FaceEachOtherInLivingRoom()
        {
            var table = AnchorTable.Load(AnchorsFlatFile);
            Assert.IsNotNull(table);
            var s1 = table.FindSeat("S1");
            Assert.IsNotNull(s1);
            Assert.IsNotNull(table.defaultSpawn);

            var spawnPos = new Vector2(table.defaultSpawn.x, table.defaultSpawn.z);
            var s1Pos = new Vector2(s1.x, s1.z);
            var toS1 = s1Pos - spawnPos;
            var dist = toS1.magnitude;
            Assert.GreaterOrEqual(dist, 1.1f, "开场玩家与沙发莫妮卡过近");
            Assert.LessOrEqual(dist, 3.2f, "开场玩家与沙发莫妮卡过远");

            var playerFwd3 = Quaternion.Euler(0f, table.defaultSpawn.facingY, 0f) * Vector3.forward;
            var monikaFwd3 = Quaternion.Euler(0f, s1.facingY, 0f) * Vector3.forward;
            var playerDot = Vector2.Dot(new Vector2(playerFwd3.x, playerFwd3.z).normalized, toS1.normalized);
            var monikaDot = Vector2.Dot(new Vector2(monikaFwd3.x, monikaFwd3.z).normalized, (-toS1).normalized);

            Assert.Greater(playerDot, 0.70f, "玩家出生朝向未迎面望向沙发上的莫妮卡");
            Assert.Greater(monikaDot, 0.70f, "莫妮卡 S1 朝向未迎面望向客厅玩家出生点");
        }

        [Test]
        public void EdgeTableBuildsDynamicFlatGraphWithDoorIdsAndRebuildsBackToBedroom()
        {
            if (!File.Exists(StreamingFlatFile))
                Assert.Ignore("StreamingAssets/mas3d/anchors-flat.json 尚未生成");

            AnchorTable.SetActiveRelativePath(AnchorTable.FlatRelativePath);
            EdgeTable.Rebuild();

            var table = AnchorTable.Load();
            Assert.IsNotNull(table);
            Assert.AreEqual("flat", table.space);

            string bedroomTarget = null;
            string kitchenTarget = null;
            foreach (var node in EdgeTable.Nodes)
            {
                Assert.AreNotEqual("bathroom", node.Room);
                if (bedroomTarget == null && node.Room == "bedroom") bedroomTarget = node.Id;
                if (kitchenTarget == null && node.Room == "kitchen") kitchenTarget = node.Id;
            }

            Assert.IsNotNull(bedroomTarget, "动态边表缺少卧室目标节点");
            Assert.IsNotNull(kitchenTarget, "动态边表缺少餐厨目标节点");

            var pathToBedroom = EdgeTable.Path("S1", bedroomTarget);
            Assert.IsNotNull(pathToBedroom, "S1 必须能走到卧室节点 " + bedroomTarget);

            var pathToKitchen = EdgeTable.Path("S1", kitchenTarget);
            Assert.IsNotNull(pathToKitchen, "S1 必须能走到餐厨节点 " + kitchenTarget);

            bool sawBL = false, sawLK = false;
            foreach (var edge in EdgeTable.Edges)
            {
                if (edge.DoorId == "Door_BedroomLiving") sawBL = true;
                if (edge.DoorId == "Door_LivingKitchen") sawLK = true;
            }
            Assert.IsTrue(sawBL, "动态边表未给卧-客跨门边标上 DoorId");
            Assert.IsTrue(sawLK, "动态边表未给客-厨跨门边标上 DoorId");

            var sample = pathToBedroom.Count >= 2 ? EdgeTable.FindEdge(pathToBedroom[0].Id, pathToBedroom[1].Id) : null;
            Assert.IsNotNull(sample);

            AnchorTable.ResetActive();
            EdgeTable.Rebuild();
            Assert.IsNotNull(EdgeTable.Node("A5"), "切回单卧默认图后 A5 必须恢复");
        }
    }
}
