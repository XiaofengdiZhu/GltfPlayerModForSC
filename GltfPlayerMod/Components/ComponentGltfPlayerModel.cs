using System;
using Engine.Animation;
using Engine.Graphics;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    /// <summary>
    /// glTF 玩家模型组件。
    /// 继承 ComponentHumanModel，复用基类的模型加载、参数同步、Animate 短路逻辑。
    /// </summary>
    /// <remarks>
    /// 工作原理：
    /// - 基类 ComponentHumanModel.Animate() 检测到 AnimationController != null 时直接 return，
    ///   跳过旧的 6 骨骼过程式动画（AnimateCreatureFallback）。
    /// - 基类 ComponentHumanModel.SyncAnimationParameters() 每帧同步 IsDead/IsInWater/IsOnGround/
    ///   VelocityY/WalkSpeed 等参数，状态规则据此选择 glb 内置动画。
    /// - 基类 ComponentHumanModel.SetModel() 用 FindBone("Body"/"Head"/"Hand1"/...)，
    ///   因配置了 boneAliases，会解析到 glb 真实骨骼，蹲下等基类逻辑不会 NRE。
    ///
    /// 跳跃相位状态机（Jump_Start / Jump_Loop / Jump_Land 三段式）：
    /// 滞空动画分起跳、滞空循环、落地三段，是带历史依赖的状态序列
    /// （Start 播完才进 Loop、仅 Loop 后播 Land），纯条件规则无法表达。
    /// 此组件维护 JumpPhase 参数写入控制器，配合 JSON 规则与动画完成事件驱动转换：
    /// - 离地边沿：上升（VelocityY > 阈值）→ Start；下降（走下悬崖等掉落）→ Loop
    /// - 落地边沿：Loop→Land（仅 Loop 后播 Land）；Start→Ground（短跳不播 Land）
    /// - Jump_Start 播完（onComplete trigger "JumpStartComplete"）→ Loop
    /// - Jump_Land 播完（onComplete trigger "JumpLandComplete"）→ Ground
    /// - 进入水/飞行/梯子/骑乘/死亡 → 重置 Ground，避免落地误播 Land
    /// </remarks>
    public class ComponentGltfPlayerModel : ComponentHumanModel {
        // 跳跃相位取值（写入控制器 JumpPhase 参数，供 JSON 规则 [JumpPhase]=='xxx' 查询）
        private const string JumpPhaseGround = "Ground";
        private const string JumpPhaseStart = "Start";
        private const string JumpPhaseLoop = "Loop";
        private const string JumpPhaseLand = "Land";

        // 当前跳跃相位
        private string m_jumpPhase = JumpPhaseGround;

        // 上一帧是否在地面（用于离地/落地边沿检测）
        private bool m_prevOnGround = true;

        // 起跳判定阈值：离地瞬间垂直速度超过此值视为主动起跳（上升），否则视为掉落
        private const float JumpStartVelocityThreshold = 0.5f;

        /// <summary>
        /// 加载：基类加载后初始化跳跃相位参数。
        /// 规则首帧评估前 JumpPhase 需已存在（Load 早于首帧 Update）。
        /// </summary>
        public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap) {
            base.Load(valuesDictionary, idToEntityMap);
            if (AnimationController != null) {
                AnimationController.Parameters.SetString("JumpPhase", m_jumpPhase);
            }
        }

        /// <summary>
        /// 同步动画参数：基类同步全部参数后，更新跳跃相位状态机并写回控制器。
        /// </summary>
        public override void SyncAnimationParameters() {
            base.SyncAnimationParameters();

            var ctrl = AnimationController;
            if (ctrl == null) return;

            var componentBody = m_componentCreature.ComponentBody;
            var componentLocomotion = m_componentCreature.ComponentLocomotion;

            bool onGround = componentBody.StandingOnValue.HasValue;
            float velocityY = componentBody.Velocity.Y;

            // 非陆地跳跃状态接管时重置相位，避免出水/出飞行落地误播 Land
            bool overridden = componentBody.ImmersionFactor > 0
                || componentLocomotion.m_flying
                || componentLocomotion.LadderValue.HasValue
                || m_componentRider?.Mount != null
                || m_componentCreature.ComponentHealth.Health <= 0;

            if (overridden) {
                m_jumpPhase = JumpPhaseGround;
            }
            else if (onGround) {
                // 刚落地：仅 Loop 相位后播 Land；Start 直接落地（短跳）不播 Land
                if (!m_prevOnGround) {
                    m_jumpPhase = (m_jumpPhase == JumpPhaseLoop) ? JumpPhaseLand : JumpPhaseGround;
                }
            }
            else {
                // 刚离地：上升=主动起跳→Start；下降=掉落→Loop。
                // 持续空中时 Start→Loop 由 Jump_Start 完成事件推进，此处不动。
                if (!m_prevOnGround) {
                    m_jumpPhase = (velocityY > JumpStartVelocityThreshold) ? JumpPhaseStart : JumpPhaseLoop;
                }
            }

            m_prevOnGround = onGround;
            ctrl.Parameters.SetString("JumpPhase", m_jumpPhase);
        }

        /// <summary>
        /// 动画事件：处理跳跃动画完成 trigger，推进相位。调用基类保留内置事件处理。
        /// </summary>
        public override void HandleAnimationEvent(AnimationEvent animationEvent) {
            if (animationEvent != null) {
                switch (animationEvent.Name) {
                    case "JumpStartComplete": m_jumpPhase = JumpPhaseLoop; break;
                    case "JumpLandComplete": m_jumpPhase = JumpPhaseGround; break;
                }
            }
            base.HandleAnimationEvent(animationEvent);
        }
    }
}
