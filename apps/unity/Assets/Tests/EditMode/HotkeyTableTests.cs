using Mas3D;
using Mas3D.Actions;
using NUnit.Framework;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// 设计 §7 的键表契约：数字键与 <see cref="ActionId.All"/> 一一对应（数量相同、键唯一、
    /// id 唯一、顺序就是 Alpha1..Alpha5），三个姿态键互不相同、也不跟数字键撞车。
    /// 纯数据检查，不需要场景，所以放在 EditMode。
    /// </summary>
    public class HotkeyTableTests
    {
        GameObject _go;
        ActionHotkeys _keys;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HotkeyTable");
            _keys = _go.AddComponent<ActionHotkeys>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void ActionKeysAreOneToOneWithTheActionTable()
        {
            Assert.IsNotNull(_keys.ActionKeys, "键表不许为空");
            Assert.AreEqual(ActionId.All.Length, _keys.ActionKeys.Length,
                "每个动作一个键：键表长度必须等于 ActionId.All 的长度");
            CollectionAssert.AllItemsAreUnique(ActionId.All, "动作 id 表本身不许有重复");
            CollectionAssert.AllItemsAreUnique(_keys.ActionKeys, "同一个键不许挂在两个槽位上");
            CollectionAssert.DoesNotContain(_keys.ActionKeys, KeyCode.None, "空键不算键");

            for (var i = 0; i < _keys.ActionKeys.Length; i++)
                Assert.AreEqual("Alpha" + (i + 1), _keys.ActionKeys[i].ToString(),
                    "第 " + (i + 1) + " 个槽位必须是数字键 " + (i + 1));
        }

        [Test]
        public void PostureKeysAreThreeDifferentKeysThatActionsDoNotUse()
        {
            Assert.AreEqual(KeyCode.C, _keys.CrouchKey, "蹲下是 C");
            Assert.AreEqual(KeyCode.Z, _keys.KneelKey, "跪下是 Z");
            Assert.AreEqual(KeyCode.X, _keys.SitKey, "坐下是 X");

            var posture = new[] { _keys.CrouchKey, _keys.KneelKey, _keys.SitKey };
            CollectionAssert.AllItemsAreUnique(posture, "三个姿态键必须互不相同");

            foreach (var key in posture)
                CollectionAssert.DoesNotContain(_keys.ActionKeys, key, "姿态键不许跟数字键撞车：" + key);
        }
    }
}
