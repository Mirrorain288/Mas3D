using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Mas3D
{
    /// <summary>
    /// 房间与角色的统一调色入口。
    ///
    /// 历史：管线切到 Linear 之前，房间 shader 自带一段手写 ACES + 曝光 + 饱和度，
    /// 而 MToon 角色完全不经过它，于是同一个场景里两种色调曲线打架（脸颊发粉白、房间发灰）。
    /// 现在整条链路改由 URP 的全局 Volume 承担：Tonemapping(ACES) + ColorAdjustments + Bloom + Vignette，
    /// 房间与角色走同一条曲线，shader 里那段自带调色已删除。
    ///
    /// Volume 用代码建而不是 .asset：手写 VolumeProfile 的 YAML 要引用 URP 程序集里的 GUID，
    /// 一旦包版本变了就静默失效；代码建则每次启动都是同一套值，编辑器出图与打包运行天然一致。
    /// </summary>
    public static class RoomLook
    {
        /// <summary>ACES 之前的曝光补偿（档）。房间夜景偏暗一点点更接近参考图。</summary>
        // −0.2 时脸颊最亮处仍到 0.945（判据 ≤0.92，ACES 之后就是"糊白"）；压到 −0.32 之后
        // 关掉 SSAO 又把脸抬亮到 0.955，再压一档到 −0.45 才落回 0.92 以下。
        public const float PostExposure = -0.15f;

        /// <summary>对比度（-100..100）。</summary>
        public const float Contrast = 18f;

        /// <summary>饱和度（-100..100，0 = 原样）。</summary>
        public const float Saturation = 15f;

        /// <summary>
        /// 整体色偏。04 那一轮的选色（m17644「光色我喜欢第四档」）把它从中性偏暖的
        /// (1.00, 0.96, 0.93) 推到微冷 (0.98, 1.00, 1.02)：参考立绘的脸颊 B/R 是 0.829，
        /// 而旧的暖滤镜 + 钨丝台灯把她压到 0.688。
        /// </summary>
        public static readonly Color Filter = new Color(0.98f, 1.00f, 1.02f);

        /// <summary>白平衡：往冷里拉 16 点、品红方向 4 点，跟 <see cref="Filter"/> 一起定下肤色。</summary>
        public const float Temperature = -16f;
        public const float Tint = 4f;

        /// <summary>Bloom 的强度与阈值：只在窗外的星空和高光上起一层薄雾。</summary>
        public const float BloomIntensity = 0.12f;
        public const float BloomThreshold = 1.1f;

        /// <summary>暗角，0..1。夜里看屏幕，四角压一点更像“房间”。</summary>
        public const float Vignette = 0.15f;

        /// <summary>
        /// 灯光补偿系数。管线统一之前它是 0.5：房间 shader 自带 ACES、角色没有，
        /// 所以把所有灯压一半再把房间曝光乘回来，才能在同一个画面里对齐两者。
        /// 现在两边都过 Volume 的 ACES，补偿失去意义，保留成 1.0 只是不去动调用点（RoomDress 里十几处 RoomLook.Scale）。
        /// </summary>
        public const float LightScale = 1f;

        /// <summary>灯光强度按 <see cref="LightScale"/> 缩放。</summary>
        public static float Scale(float intensity) => intensity * LightScale;

        /// <summary>环境光三元组按 <see cref="LightScale"/> 缩放。</summary>
        public static Color Scale(Color ambient) => ambient * LightScale;

        static Volume _volume;
        static Tonemapping _tonemapping;
        static ColorAdjustments _colours;
        static WhiteBalance _balance;
        static Bloom _bloom;
        static Vignette _vignette;

        /// <summary>
        /// 写入（或更新）全局 Volume。幂等：第一次调用建对象，之后只改 override 值。
        /// 打包运行与编辑器出图（CliCapture / CliPlayer / RoomDress）都走这一个入口。
        /// </summary>
        public static void Apply(float postExposure = PostExposure, float saturation = Saturation,
                                 float contrast = Contrast, float vignette = Vignette,
                                 float bloom = BloomIntensity, Color? filter = null,
                                 float whiteBalanceTemperature = Temperature, float whiteBalanceTint = Tint)
        {
            EnsureVolume();
            if (_volume == null) return;

            if (_tonemapping != null)
            {
                _tonemapping.active = true;
                _tonemapping.mode.Override(TonemappingMode.ACES);
            }

            if (_colours != null)
            {
                _colours.active = true;
                _colours.postExposure.Override(postExposure);
                _colours.contrast.Override(Mathf.Clamp(contrast, -100f, 100f));
                _colours.saturation.Override(Mathf.Clamp(saturation, -100f, 100f));
                if (_colours.colorFilter != null) _colours.colorFilter.Override(filter ?? Filter);
            }

            // 白平衡默认跟着定档的肤色走（−16 / +4）；传 0/0 就回到中性，出图扫参时用得上。
            if (_balance != null)
            {
                _balance.active = Mathf.Abs(whiteBalanceTemperature) > 0.0001f || Mathf.Abs(whiteBalanceTint) > 0.0001f;
                _balance.temperature.Override(Mathf.Clamp(whiteBalanceTemperature, -100f, 100f));
                _balance.tint.Override(Mathf.Clamp(whiteBalanceTint, -100f, 100f));
            }

            if (_bloom != null)
            {
                _bloom.active = bloom > 0f;
                _bloom.intensity.Override(Mathf.Max(0f, bloom));
                _bloom.threshold.Override(BloomThreshold);
            }

            if (_vignette != null)
            {
                _vignette.active = vignette > 0f;
                _vignette.intensity.Override(Mathf.Clamp01(vignette));
                _vignette.smoothness.Override(0.4f);
            }
        }

        /// <summary>当前这套值是否已经挂到场景里（测试与诊断用）。</summary>
        public static bool Ready => _volume != null && _volume.sharedProfile != null;

        static void EnsureVolume()
        {
            // 场景重载（编辑器出图每次 NewScene）会把静态引用变成“已销毁”的假非空，
            // Unity 的 == 对已销毁对象返回 true，所以这一句就够了。
            if (_volume != null) return;

            // 不设 hideFlags：URP 的 VolumeManager 靠 FindObjectsByType 扫全局 Volume，
            // 对象一旦被 HideAndDontSave 藏起来，它就既扫不到也不会参与后处理（表现为“调色完全不生效”）。
            var host = new GameObject("Mas3D-Look");
            if (Application.isPlaying) Object.DontDestroyOnLoad(host);

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Mas3D-Look";

            _tonemapping = profile.Add<Tonemapping>(true);
            _colours = profile.Add<ColorAdjustments>(true);
            _balance = profile.Add<WhiteBalance>(true);
            _bloom = profile.Add<Bloom>(true);
            _vignette = profile.Add<Vignette>(true);

            _volume = host.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 1000f;
            _volume.weight = 1f;
            _volume.sharedProfile = profile;
        }
    }
}
