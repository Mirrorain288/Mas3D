using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class TwoBoneIkTests
    {
        static (Transform upper, Transform lower) Chain(Vector3 root, float upperLength, float lowerLength)
        {
            var a = new GameObject("upper").transform;
            a.position = root;
            var b = new GameObject("lower").transform;
            b.SetParent(a, false);
            b.localPosition = new Vector3(0f, -upperLength, 0f);
            var tip = new GameObject("tip").transform;
            tip.SetParent(b, false);
            tip.localPosition = new Vector3(0f, -lowerLength, 0f);
            return (a, b);
        }

        static Transform Tip(Transform lower) => lower.GetChild(0);

        [UnityTest]
        public IEnumerator AReachableTargetIsHitWithinTwoCentimetres()
        {
            var (upper, lower) = Chain(Vector3.zero, 0.30f, 0.28f);
            try
            {
                yield return null;
                var target = new Vector3(0.20f, -0.30f, 0.15f);
                Assert.IsTrue(TwoBoneIk.Solve(upper, lower, target, Vector3.back), "0.42 m 的目标在 0.58 m 臂长内，必须够得着");
                Assert.Less(Vector3.Distance(Tip(lower).position, target), 0.02f, "末端到目标必须 ≤ 2 cm");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }

        [UnityTest]
        public IEnumerator AnUnreachableTargetIsRefusedNotStretched()
        {
            var (upper, lower) = Chain(Vector3.zero, 0.30f, 0.28f);
            try
            {
                yield return null;
                // 不共线：静止姿态沿 -Y 伸直，共线目标（如 (0,-2,0)）的 dot 天然是 1，
                // 那样「朝目标伸直」这条断言对空实现也绿，挡不住把两处 .rotation 删掉的回归。
                var target = new Vector3(0.30f, -1.60f, 0.30f);
                Assert.IsFalse(TwoBoneIk.Solve(upper, lower, target, Vector3.back), "1.65 m 外的目标必须报够不着");
                Assert.AreEqual(0.30f, Vector3.Distance(upper.position, lower.position), 0.002f, "上段长度不许变");
                Assert.AreEqual(0.28f, Vector3.Distance(lower.position, Tip(lower).position), 0.002f, "下段长度也不许变");
                Assert.Greater(Vector3.Dot((Tip(lower).position - upper.position).normalized, (target - upper.position).normalized), 0.99f, "够不着时至少要沿目标方向伸直");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }

        [UnityTest]
        public IEnumerator ATargetInsideTheDeadZoneIsRefused()
        {
            var (upper, lower) = Chain(Vector3.zero, 0.30f, 0.28f);
            try
            {
                yield return null;
                // 目标正好落在肩点上：两段骨长差 2 cm，这里坐进内死区（d < |l1-l2|），
                // 末端永远够不到：泛函上界就是 |l1-l2| = 2 cm，所以必须按够不着拒绝，不能撒谎说 true。
                var target = upper.position;
                Assert.IsFalse(TwoBoneIk.CanReach(upper, lower, target), "内死区里的目标，CanReach 必须答 false");
                Assert.IsFalse(TwoBoneIk.Solve(upper, lower, target, Vector3.back), "内死区里的目标，Solve 必须答 false");
                Assert.AreEqual(0.30f, Vector3.Distance(upper.position, lower.position), 0.002f, "上段长度不许变");
                Assert.AreEqual(0.28f, Vector3.Distance(lower.position, Tip(lower).position), 0.002f, "下段长度也不许变");
                var end = Tip(lower).position;
                Assert.IsFalse(float.IsNaN(end.x) || float.IsNaN(end.y) || float.IsNaN(end.z), "末端坐标不许出现 NaN");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }

        [UnityTest]
        public IEnumerator ThePoleChoosesWhichSideTheElbowBendsTo()
        {
            var (upper, lower) = Chain(Vector3.zero, 0.30f, 0.28f);
            try
            {
                yield return null;
                var target = new Vector3(0f, -0.40f, 0f);
                TwoBoneIk.Solve(upper, lower, target, Vector3.forward);
                var frontElbow = lower.position;
                TwoBoneIk.Solve(upper, lower, target, Vector3.back);
                var backElbow = lower.position;
                Assert.Greater(frontElbow.z, 0.05f, "pole 朝前时肘部必须往前弯");
                Assert.Less(backElbow.z, -0.05f, "pole 朝后时肘部必须往后弯");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }

        [UnityTest]
        public IEnumerator AMissingBoneReturnsFalseInsteadOfThrowing()
        {
            var upper = new GameObject("upper").transform;
            try
            {
                yield return null;
                Assert.IsFalse(TwoBoneIk.Solve(upper, null, Vector3.zero, Vector3.forward), "缺下段要返回 false，不抛");
                Assert.AreEqual(0f, TwoBoneIk.Reach(upper, null), 0.0001f, "缺骨头的臂长是 0");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }
    }
}
