using Mas3D.Avatar;
using Mas3D.Conversation;
using NUnit.Framework;
using UniVRM10;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 4 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: the mood layer turns
    /// <see cref="ConversationController.Phase"/> into three expression weights and nothing else.
    /// Every case drives <see cref="AvatarMood.Apply"/> / <see cref="AvatarMood.Tick"/> by hand with
    /// the component disabled, so nothing here depends on Update timing. The one case that has to
    /// reach Update's own frame calls <see cref="AvatarMood.Poll"/>, the exact body Update runs.
    ///
    /// AvatarIdle is deliberately absent from this fixture: it rewrites Blink to 0 on every tick, so
    /// MoodNeverTouchesBlinkOrMouth would go red for a reason that has nothing to do with the mood.
    /// </summary>
    public class AvatarMoodTests
    {
        AvatarRig _rig;
        AvatarMood _mood;

        [TearDown]
        public void TearDown()
        {
            if (_rig)
            {
                Object.DestroyImmediate(_rig.gameObject);
                _rig = null;
                _mood = null;
            }
        }

        /// <summary>Spawn Monkia (in the shipped seated pose) and a disabled mood driven only by hand.</summary>
        AvatarMood Spawn()
        {
#if UNITY_EDITOR
            AvatarPrefabFixture.Require();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabFixture.PrefabPath);
            Assert.IsNotNull(prefab, "Monkia.vrm 在本地，但 AssetDatabase 没导入它（在编辑器里重导入一次）："
                                     + AvatarPrefabFixture.PrefabPath);
#else
            Assert.Ignore("PlayMode mood tests need the Editor asset database (VRM importer).");
            GameObject prefab = null;
#endif
            _rig = AvatarRig.Spawn(prefab);
            AvatarPose.Sitting(_rig);

            _mood = _rig.gameObject.AddComponent<AvatarMood>();
            _mood.Rig = _rig;
            _mood.enabled = false;                  // Update 不许插一脚：时间只由 Tick(dt) 推进
            return _mood;
        }

        float Weight(ExpressionKey key)
        {
            return _rig.ExpressionWeight(key);
        }

        [Test]
        public void ThinkingLooksRelaxedAndNotHappy()
        {
            var mood = Spawn();
            mood.Apply(TurnPhase.Thinking);

            Assert.AreEqual(0.20f, Weight(ExpressionKey.Relaxed), 0.01f, "思考时该是浅浅的放松");
            Assert.Less(Weight(ExpressionKey.Happy), 0.01f, "思考时不许笑");
            Assert.Less(Weight(ExpressionKey.Sad), 0.01f, "思考时也不许难过");
        }

        [Test]
        public void AnAnswerSmilesThenSettlesBack()
        {
            var mood = Spawn();
            mood.Apply(TurnPhase.Answered);
            Assert.AreEqual(0.65f, Weight(ExpressionKey.Happy), 0.01f, "回答完该笑");
            // 先钉 Apply 当场那一笔：这时一次 Tick 都还没跑，0.35 只可能来自 Apply。
            Assert.AreEqual(0.35f, Weight(ExpressionKey.Relaxed), 0.01f, "回答当场就该落回待机的放松");

            // 再把 IdleRelaxed 挪到 0.55 才走完秒数：0.35 是默认值、Apply 也写过一遍，
            // 收尾那一笔必须读当下的字段，只有 Tick 真的重写了 Relaxed 才会看到 0.55。
            mood.IdleRelaxed = 0.55f;

            mood.Tick(2.6f);
            mood.Tick(0.5f);
            Assert.Less(Weight(ExpressionKey.Happy), 0.05f, "笑完要收回去");
            Assert.AreEqual(0.55f, Weight(ExpressionKey.Relaxed), 0.01f, "收回之后 Tick 要按当时的 IdleRelaxed 重写");
        }

        [Test]
        public void MoodNeverTouchesBlinkOrMouth()
        {
            var mood = Spawn();
            _rig.SetExpression(ExpressionKey.Blink, 0.4f);
            _rig.SetExpression(ExpressionKey.Aa, 0.3f);

            mood.Apply(TurnPhase.Answered);
            Assert.AreEqual(0.65f, Weight(ExpressionKey.Happy), 0.01f,
                "先确认 Apply 真的改了表情，否则下面两条是空转的");

            Assert.AreEqual(0.4f, Weight(ExpressionKey.Blink), 0.02f, "表情不许动眨眼");
            Assert.AreEqual(0.3f, Weight(ExpressionKey.Aa), 0.02f, "表情不许动口型");
        }

        [Test]
        public void AFailedTurnDropsTheSmile()
        {
            var mood = Spawn();
            mood.Apply(TurnPhase.Answered);
            Assert.AreEqual(0.65f, Weight(ExpressionKey.Happy), 0.01f, "先笑上，才能证明失败时收掉了");
            // 笑到一半就失败了：难过必须用自己的完整三秒，不继承笑剩下的秒数，
            // 否则失败的脸会提前消失。Apply 也得当场把笑写成 0，不能留个残影等 Tick 收。
            mood.Tick(1.0f);

            mood.Apply(TurnPhase.Failed);
            Assert.Less(Weight(ExpressionKey.Happy), 0.01f, "这一轮失败了，笑要当场收掉");
            Assert.AreEqual(0.50f, Weight(ExpressionKey.Sad), 0.01f, "失败该是难过");

            mood.Tick(2.9f);
            Assert.AreEqual(0.50f, Weight(ExpressionKey.Sad), 0.01f, "还没到三秒，难过不该提前结束");

            mood.Tick(0.2f);
            Assert.Less(Weight(ExpressionKey.Sad), 0.01f, "三秒后难过要收回去");
            Assert.AreEqual(0.35f, Weight(ExpressionKey.Relaxed), 0.01f, "回落到待机的放松");
        }

        /// <summary>
        /// Review Focus 3, reworded in fix round 1 (M3): AvatarRig.SetExpression carries the same
        /// GetWeights().ContainsKey gate as AvatarMood.Write, so this case cannot prove that Mood's
        /// guard exists — deleting it leaves every case green. What it does pin is the no-throw
        /// contract: both layers ignore an unknown key, and no phase may throw or leave a negative
        /// weight behind. The HasExpression precondition stays because it documents that "sulk" is
        /// really absent from this model, which is what makes the attempted write worth checking.
        /// </summary>
        [Test]
        public void MissingPresetsDoNotThrow()
        {
            var mood = Spawn();
            var sulk = ExpressionKey.CreateCustom("sulk");
            Assert.IsFalse(_rig.HasExpression(sulk), "sulk 必须真的不存在：缺失键必须被安静忽略，而不是靠它证明守卫存在");
            Assert.DoesNotThrow(() => _rig.SetExpression(sulk, 0.9f), "未知键的写入必须安静地忽略");
            Assert.AreEqual(0f, Weight(sulk), 1e-4f, "未知键不该真的写下权重");

            Assert.DoesNotThrow(() => mood.Apply(TurnPhase.Thinking));
            Assert.DoesNotThrow(() => mood.Tick(0.2f));
            Assert.DoesNotThrow(() => mood.Apply(TurnPhase.Answered));
            Assert.DoesNotThrow(() => mood.Tick(2.6f));
            Assert.DoesNotThrow(() => mood.Apply(TurnPhase.Failed));
            Assert.DoesNotThrow(() => mood.Tick(3.1f));
            Assert.DoesNotThrow(() => mood.Apply(TurnPhase.Idle));

            var relaxed = Weight(ExpressionKey.Relaxed);
            Assert.GreaterOrEqual(relaxed, 0f, "Relaxed 不许被写成负数");
            Assert.LessOrEqual(relaxed, 1f, "Relaxed 也不许越过 1");
            Assert.GreaterOrEqual(Weight(ExpressionKey.Sad), 0f, "Sad 不许被写成负数");
        }

        /// <summary>
        /// Fix round 1 (M4): nothing drove the null paths. A mood that never got a rig, and a mood
        /// whose AvatarRig found no Vrm10Instance, must both stay silent — Apply/Tick may not throw
        /// and may not reach into a null instance. Delete AvatarMood's "if (Rig == null) return;"
        /// and the first half of this case becomes a NullReferenceException.
        /// </summary>
        [Test]
        public void MoodWithNoRigOrNoVrmIsSilent()
        {
            var bare = new GameObject("MoodOnly");
            try
            {
                var mood = bare.AddComponent<AvatarMood>();   // Awake finds no AvatarRig to fall back to
                Assert.IsNull(mood.Rig, "空对象上不该凭空长出一个 rig");
                Assert.DoesNotThrow(() => mood.Apply(TurnPhase.Thinking), "没有 rig 时 Apply 不许抛");
                Assert.DoesNotThrow(() => mood.Tick(0.1f), "没有 rig 时 Tick 不许抛");
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }

            // A rig that found no Vrm10Instance warns once in Awake, then answers every expression
            // lookup with "not mine"; the mood must not turn that into an exception either.
            var noVrm = new GameObject("NoVrmRig");
            try
            {
                var rig = noVrm.AddComponent<AvatarRig>();
                var mood = noVrm.AddComponent<AvatarMood>();
                mood.Rig = rig;
                mood.enabled = false;

                Assert.IsFalse(rig.HasExpression(ExpressionKey.Relaxed), "没有 VRM 的 rig 不该声称自己有表情");
                Assert.DoesNotThrow(() => mood.Apply(TurnPhase.Thinking), "rig 找不到 VRM 时 Apply 不许抛");
                Assert.DoesNotThrow(() => mood.Tick(0.1f), "rig 找不到 VRM 时 Tick 不许抛");
                Assert.DoesNotThrow(() => mood.Apply(TurnPhase.Answered), "没有 VRM 时 Begin 也不许抛");
                Assert.DoesNotThrow(() => mood.Tick(2.6f), "没有 VRM 时收尾那一笔也不许抛");
            }
            finally
            {
                Object.DestroyImmediate(noVrm);
            }
        }

        /// <summary>
        /// Fix round 1 (M5): BindConversation(null) never calls Apply, so the face used to rest on the
        /// model's neutral pose while the wired path rests on IdleRelaxed. One Update-equivalent frame
        /// — AvatarMood.Poll, the exact body Update runs — has to land on 0.35 with no controller.
        /// </summary>
        [Test]
        public void NoControllerStillRestsOnIdleRelaxed()
        {
            var mood = Spawn();
            Assert.IsNull(mood.Controller, "这条用例的前提：没人绑对话状态");
            Assert.Less(Weight(ExpressionKey.Relaxed), 0.01f, "一帧都还没跑，表情停在模型的中性脸上");

            mood.Poll(0.1f);
            Assert.AreEqual(0.35f, Weight(ExpressionKey.Relaxed), 0.01f, "没有对话也要落到待机的放松，不能停在中性脸");
        }
    }
}
