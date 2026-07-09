using System;
using Engine;
using Engine.Animation;
using Engine.Graphics;

namespace Game.Animation.Drivers {
    /// <summary>
    /// glTF 玩家全身转向驱动器（Additive，叠加在 Base 层 walk/idle 动画之上）。
    /// </summary>
    /// <remarks>
    /// 行为：前方半圆 body 面向移动方向；后方半圆 body 背对移动方向 + walk 倒播 = 真倒退姿态。
    /// 转向角由瞬时 WalkOrder（LastWalkOrder，玩家输入）算：raw = Vector2.Angle(UnitY, WalkOrder)
    /// （同 ComponentHumanModel:150 公式，带符号归一化[-π,π]，raw=面向移动方向的 yaw）。
    /// 前后判定用 WalkOrder 与正前方夹角余弦 cos = WalkOrder.Y/|WalkOrder|：
    /// 前方半圆（cos≥BackwardCosThreshold，前进/侧移/斜前）：target=raw（body 面向移动方向）+ walk 正播。
    /// 后方半圆（cos&lt;BackwardCosThreshold=-0.3，即夹角&gt;107°，后退/斜后）：target=raw+π（body 背对移动方向）
    /// + walk 倒播（MoveSpeed 符号，UpdateMoveSpeed 同用 cos&lt;-0.3 反向播，阈值一致 → body 背对与 walk 倒播同步）
    /// → 身体朝移动反方向 + 腿倒迈 = 真倒退。
    /// 纯后退（raw≈±π）target=0（朝前）；斜后（raw≈±3π/4）target≈±π/4（朝斜前，背对斜后移动）。
    /// 用瞬时输入非平滑 HeadingOffset：后者平滑爬升（ComponentHumanModel:152）+ 阈值硬切致后退"先转后瞬间转正"。
    /// 自维护 m_currentHeading 单层平滑趋近 target（SmoothRate），各方向切换无跳变。
    /// 原版 dae 后退是身体转 180°+正走；此改为背对移动方向+倒播。
    /// head 视线追踪不在此 driver（head 局部坐标系经颈弯曲旋转，单轴抵消非纯 yaw），
    /// 由 ComponentGltfPlayerBaseController 注册的 SingleBoneIK 链在层混合后求解（自动补偿 pelvis 转）。
    /// Additive 混合（AnimationBlender: existing * incoming）→ 全身在 walk/idle 基础上叠加转向，腿/臂摆动动画保留。
    /// 只写 pelvis，其余骨骼该层无值 → blender 跳过；pelvis 子树（spine 链/腿/臂）经骨骼层级继承旋转。
    /// 旋转轴经 property 可配（JSON driverArgs）：glTF 骨骼局部坐标系因模型/烘焙旋转而异，YawAxis 默认 Y，
    /// 若表现为侧翻（绕水平轴）说明该骨骼局部 Y 非垂直，改 YawAxis 为 "X" 或 "Z" 试。
    /// 死亡/躺下由 JSON bodyturn 规则停用本层（condition 排除 IsDead/LieDownFactor），避免乱转。
    /// </remarks>
    public class GltfBodyTurnDriver : IAnimationDriver {
        // 后退判定阈值：WalkOrder 与正前方夹角余弦 < 此值视为后方半圆（body 背对移动方向 + walk 倒播）。
        // 与 UpdateMoveSpeed 的 cos<-0.3 阈值一致 → body 背对与 walk 倒播同步（夹角>107°）。
        // 注：两处 cos 来源不同（刻意）：driver 读瞬时输入 WalkOrder（避免速度滞后致"先转后瞬间转正"），
        // UpdateMoveSpeed 读物理速度 vel·fwd。地面正常移动两者同步；滑冰/walkSpeedWhenTurning/空中惯性时可能短暂偏差（可接受）。
        const float BackwardCosThreshold = -0.3f;
        // 静止死区：|WalkOrder|² < 此值视为无输入（target 归零回正）
        const float MoveDeadZone = 1e-4f;

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

        /// <summary>行走指令 X 分量参数名（侧向，LastWalkOrder.X，瞬时输入）</summary>
        public string WalkOrderXParam { get; set; } = "WalkOrderX";

        /// <summary>行走指令 Y 分量参数名（前后，LastWalkOrder.Y，瞬时输入）</summary>
        public string WalkOrderYParam { get; set; } = "WalkOrderY";

        /// <summary>转向（yaw）轴："X"/"Y"/"Z"，默认 Y。若侧翻改 X 或 Z</summary>
        public string YawAxis { get; set; } = "Y";

        /// <summary>转向幅度比例（1.0=原版全量，纯侧移约 90°；调小可减弱）</summary>
        public float HeadingScale { get; set; } = 1.0f;

        /// <summary>转向平滑速率（1/s，越大越快至 target）。各方向切换连续趋近无跳变。
        /// 默认 10 略快于引擎 HeadingOffset 的 8（ComponentHumanModel:152），使转向更跟手</summary>
        public float SmoothRate { get; set; } = 10f;

        /// <summary>转向方向反转（左右转向反了改 true）</summary>
        public bool InvertYaw { get; set; } = false;

        float m_currentHeading = 0f;

        public void Update(float deltaTime, AnimationParameters parameters) {
            float wx = parameters.GetFloat(WalkOrderXParam);
            float wy = parameters.GetFloat(WalkOrderYParam);
            // 目标转向角：无输入归零；前方半圆 body 面向移动方向（raw）；后方半圆 body 背对（raw+π）+ walk 倒播。
            // 前后判定用 cos=wy/|WalkOrder|（同 UpdateMoveSpeed），阈值 BackwardCosThreshold → 与 walk 倒播同步。
            float target = 0f;
            float len2 = wx * wx + wy * wy;
            if (len2 > MoveDeadZone) {
                float raw = Vector2.Angle(Vector2.UnitY, new Vector2(wx, wy));
                float cos = wy / MathF.Sqrt(len2);
                // cos<-0.3（107°）边界：target 在"面向移动方向"(前分支,≈±107°)与"背对"(后分支,≈∓73°)间跳变 ~180°，
                // 同边界 walk 播放方向翻转（UpdateMoveSpeed sign），两翻转关联。SmoothRate 平滑成快速旋转非瞬移。
                target = (cos < BackwardCosThreshold) ? MathUtils.NormalizeAngle(raw + MathF.PI) : raw;
            }
            // 单层平滑：瞬时 target + 连续趋近，各方向切换无跳变（NormalizeAngle 处理跨 ±π 最短角）
            m_currentHeading = MathUtils.NormalizeAngle(m_currentHeading);
            m_currentHeading += MathUtils.NormalizeAngle(target - m_currentHeading) * MathUtils.Saturate(SmoothRate * deltaTime);
        }

        public void SampleTransforms(Matrix?[] boneTransforms, Model model) {
            float heading = m_currentHeading * HeadingScale;
            if (MathUtils.Abs(heading) < 1e-6f) {
                return;
            }
            float yawAngle = InvertYaw ? -heading : heading;

            // 全身转向：BodyRoot（pelvis）绕 YawAxis 转，带动躯干+四肢
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
