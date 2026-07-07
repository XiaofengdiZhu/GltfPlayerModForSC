using Engine.Animation;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    /// <summary>
    /// glTF 玩家跳跃相位状态机组件。
    /// 作为 ComponentAnimationParticipant 挂载，不替换 ComponentHumanModel。
    /// </summary>
    /// <remarks>
    /// 工作原理：
    /// - 作为参与者由 ComponentModel 收集（ShouldApplyTo 限定有 controller 的 model）。
    /// - ComponentModel.Animate() 每帧在 controller.Update 之前调用 SyncAnimationParameters，
    ///   此组件更新 JumpPhase 参数，状态规则据此选择 glb 内置动画。
    /// - OnControllerCreated 在控制器就绪时写入 JumpPhase 初值。
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
    public class ComponentGltfPlayerJumpController : ComponentAnimationParticipant {
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

        private ComponentCreature m_componentCreature;
        private ComponentRider m_componentRider;

        /// <summary>
        /// 加载：缓存依赖（参与者不继承模型组件，自行 FindComponent 取）。
        /// </summary>
        public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap) {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentCreature = Entity.FindComponent<ComponentCreature>(true);
            m_componentRider = Entity.FindComponent<ComponentRider>();
        }

        /// <summary>
        /// 仅参与有 AnimationController 的 model（服装 model 无 controller 自动排除），
        /// 避免本组件（Sync 有状态推进）一帧被多个 model 调用导致相位机错乱。
        /// </summary>
        public override bool ShouldApplyTo(ComponentModel componentModel)
            => componentModel is ComponentHumanModel && componentModel.AnimationController != null;

        /// <summary>
        /// 控制器就绪：写入 JumpPhase 初始值（规则首帧评估前需已存在）。
        /// </summary>
        public override void OnControllerCreated(AnimationController controller) {
            controller.Parameters.SetString("JumpPhase", m_jumpPhase);
        }

        /// <summary>
        /// 同步动画参数：更新跳跃相位状态机并写回控制器（每帧 controller.Update 之前调用）。
        /// </summary>
        public override void SyncAnimationParameters(AnimationController controller) {
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
            controller.Parameters.SetString("JumpPhase", m_jumpPhase);
        }

        /// <summary>
        /// 动画事件：处理跳跃动画完成 trigger，推进相位。
        /// </summary>
        public override void HandleAnimationEvent(AnimationController controller, AnimationEvent animationEvent) {
            switch (animationEvent.Name) {
                case "JumpStartComplete": m_jumpPhase = JumpPhaseLoop; break;
                case "JumpLandComplete": m_jumpPhase = JumpPhaseGround; break;
            }
        }
    }
}
