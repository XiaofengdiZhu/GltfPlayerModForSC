using Engine;
using Engine.Animation;
using Engine.Graphics;

namespace Game.Animation.Drivers {
    /// <summary>
    /// glTF 玩家全身转向驱动器（Additive，叠加在 Base 层 walk/idle 动画之上）。
    /// </summary>
    /// <remarks>
    /// 复刻原版 dae 玩家行为：全身（躯干+四肢含双腿）转向移动方向。
    /// BodyRoot（pelvis，腿+躯干共同根）绕 YawAxis 转 HeadingOffset，带动躯干+双臂+双腿整体朝行走方向。
    /// 侧移时全身转约 90° 朝侧面（朝行走方向走），而非锁在摄像机方向。
    /// head 视线追踪不在此 driver（head 局部坐标系经颈弯曲旋转，单轴抵消 headingOffset 非纯 yaw），
    /// 改由 ComponentGltfPlayerBaseController 注册的 SingleBoneIK 链在层混合后求解（自动补偿 pelvis 转）。
    /// 参数 HeadingOffset 由 ComponentHumanModel.SyncAnimationParameters 写入 controller（玩家实体共用）。
    /// Additive 混合（AnimationBlender: existing * incoming）→ 全身在 walk/idle 基础上叠加转向，腿/臂摆动动画保留。
    /// 只写 pelvis，其余骨骼该层无值 → blender 跳过；pelvis 子树（spine 链/腿/臂）经骨骼层级继承旋转。
    /// 旋转轴经 property 可配（JSON driverArgs）：glTF 骨骼局部坐标系因模型/烘焙旋转而异，YawAxis 默认 Y，
    /// 若表现为侧翻（绕水平轴）说明该骨骼局部 Y 非垂直，改 YawAxis 为 "X" 或 "Z" 试。
    /// 后退不转（|HeadingOffset| > MaxHeadingAngle 视为后退，保持面向前方）。
    /// 死亡/躺下由 JSON bodyturn 规则停用本层（condition 排除 IsDead/LieDownFactor），避免乱转。
    /// </remarks>
    public class GltfBodyTurnDriver : IAnimationDriver {
        public string Name => "GltfBodyTurn";
        public AnimationBlendMode BlendMode => AnimationBlendMode.Additive;

        // 目标骨骼（骨骼名经 property 配置后失效缓存重建，参考 LookAtDriver）
        public string[] TargetBones => m_cachedTargetBones ??= [BodyRootBone];
        string[] m_cachedTargetBones;

        /// <summary>身体根骨骼名（转向目标，腿+躯干共同根，带动全身含四肢）</summary>
        public string BodyRootBone {
            get;
            set {
                field = value;
                m_cachedTargetBones = null;
            }
        } = "pelvis";

        /// <summary>转向（yaw）参数名</summary>
        public string HeadingOffsetParam { get; set; } = "HeadingOffset";

        /// <summary>转向（yaw）轴："X"/"Y"/"Z"，默认 Y。若侧翻改 X 或 Z</summary>
        public string YawAxis { get; set; } = "Y";

        /// <summary>转向幅度比例（1.0=原版全量，纯侧移约 90°；调小可减弱）</summary>
        public float HeadingScale { get; set; } = 1.0f;

        /// <summary>后退阈值（弧度）：|headingOffset| 超过此值视为后退，不转身体（保持面向前方）。π=180°纯后退，π/2≈90°侧移</summary>
        public float MaxHeadingAngle { get; set; } = 2.4f;

        /// <summary>转向方向反转（左右转向反了改 true）</summary>
        public bool InvertYaw { get; set; } = false;

        float m_headingOffset;

        public void Update(float deltaTime, AnimationParameters parameters) {
            m_headingOffset = parameters.GetFloat(HeadingOffsetParam);
        }

        public void SampleTransforms(Matrix?[] boneTransforms, Model model) {
            // 后退（|headingOffset|>MaxHeadingAngle）不转身体，保持面向前方
            bool backward = MathUtils.Abs(m_headingOffset) > MaxHeadingAngle;
            float heading = backward ? 0f : m_headingOffset * HeadingScale;
            if (heading == 0f) {
                return;
            }
            float yawAngle = InvertYaw ? -heading : heading;

            // 全身转向移动方向：BodyRoot（pelvis）绕 YawAxis 转 headingOffset，带动躯干+四肢
            ModelBone rootBone = model.FindBone(BodyRootBone, false);
            if (rootBone != null) {
                boneTransforms[rootBone.Index] = CreateRotationForAxis(YawAxis, yawAngle);
            }
        }

        Matrix CreateRotationForAxis(string axis, float angle) {
            return axis?.ToUpperInvariant() switch {
                "X" => Matrix.CreateRotationX(angle),
                "Y" => Matrix.CreateRotationY(angle),
                "Z" => Matrix.CreateRotationZ(angle),
                _ => Matrix.CreateRotationY(angle)
            };
        }
    }
}
