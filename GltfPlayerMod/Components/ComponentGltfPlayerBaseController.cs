using Engine;
using Engine.Animation;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    /// <summary>
    /// glTF 玩家 Base 层动画参数同步组件（ComponentAnimationParticipant，不替换 ComponentHumanModel）。
    /// </summary>
    /// <remarks>
    /// 每帧 SyncAnimationParameters 更新跳跃/起床/疲劳/近战武器等相位参数，
    /// Base 层 JSON 状态规则据此选择 glb 内置动画。
    /// 仅参与有 controller 的 ComponentHumanModel（ShouldApplyTo 过滤）。
    /// 各相位机详细逻辑见对应 Update* 方法。
    /// </remarks>
    public class ComponentGltfPlayerBaseController : ComponentAnimationParticipant {
        // 跳跃相位取值（写入控制器 JumpPhase 参数，供 JSON 规则 [JumpPhase]=='xxx' 查询）
        private const string JumpPhaseGround = "Ground";
        private const string JumpPhaseStart = "Start";
        private const string JumpPhaseLoop = "Loop";
        private const string JumpPhaseLand = "Land";
        private const string JumpPhaseClimbUp = "ClimbUp";

        // 当前跳跃相位
        private string m_jumpPhase = JumpPhaseGround;

        // 上一帧是否在地面（用于离地/落地边沿检测）
        private bool m_prevOnGround = true;

        // 起跳判定阈值：离地瞬间垂直速度超过此值视为主动起跳（上升），否则视为掉落
        private const float JumpStartVelocityThreshold = 0.5f;

        // 落地播 Land 所需的最小净下落高度（米）：落地高度比起跳/下落时低超过此值才播 Land
        private const float JumpLandMinDropHeight = 1.8f;

        // 本次滞空起跳/下落瞬间的 Y 高度，落地时与当前高度比较判定是否播 Land
        private float m_takeoffY;

        // 起床中标志（写入控制器 IsWakingUp 参数）；上一帧是否在睡（用于 IsSleeping 下降沿检测）
        private bool m_isWakingUp;
        private bool m_prevIsSleeping;

        private ComponentCreature m_componentCreature;
        private ComponentRider m_componentRider;
        private ComponentSleep m_componentSleep;
        private ComponentFlu m_componentFlu;
        private ComponentVitalStats m_componentVitalStats;

        // glTF 玩家自动跳跃（可空：仅 Player 实体挂载）；触发越障时消费其攀爬标志进入 ClimbUp 相位
        private ComponentGltfPlayerAutoJump m_componentAutoJump;

        // 玩家挖掘组件（可空：服装 model 无）；读 ActiveBlockValue 判定手持近战武器
        private ComponentMiner m_componentMiner;

        /// <summary>
        /// 加载：缓存依赖（参与者不继承模型组件，自行 FindComponent 取）。
        /// </summary>
        public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap) {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentCreature = Entity.FindComponent<ComponentCreature>(true);
            m_componentRider = Entity.FindComponent<ComponentRider>();
            m_componentSleep = Entity.FindComponent<ComponentSleep>();
            m_componentFlu = Entity.FindComponent<ComponentFlu>();
            m_componentVitalStats = Entity.FindComponent<ComponentVitalStats>();
            m_componentAutoJump = Entity.FindComponent<ComponentGltfPlayerAutoJump>();
            m_componentMiner = Entity.FindComponent<ComponentMiner>();
        }

        /// <summary>
        /// 仅参与有 AnimationController 的 ComponentHumanModel（服装 model 无 controller 自动排除），
        /// 避免本组件（Sync 有状态推进）一帧被多个 model 调用导致相位机错乱。
        /// </summary>
        public override bool ShouldApplyTo(ComponentModel componentModel)
            => componentModel is ComponentHumanModel && componentModel.AnimationController != null;

        /// <summary>
        /// 控制器就绪：写入各参数初始值（规则首帧评估前需已存在）。
        /// </summary>
        public override void OnControllerCreated(AnimationController controller) {
            controller.Parameters.SetString("JumpPhase", m_jumpPhase);
            controller.Parameters.SetBool("IsWakingUp", m_isWakingUp);
            controller.Parameters.SetBool("IsShivering", false);
            controller.Parameters.SetBool("IsTired", false);
            controller.Parameters.SetBool("IsHoldingMeleeWeapon", false);
        }

        /// <summary>
        /// 同步动画参数：每帧 controller.Update 之前调用，派发到各相位机 Update* 方法。
        /// </summary>
        public override void SyncAnimationParameters(AnimationController controller) {
            UpdateJumpPhase(controller);
            UpdateWakeUpState(controller);
            UpdateVitalState(controller);
            UpdateMeleeWeaponState(controller);
        }

        /// <summary>
        /// 跳跃相位状态机：维护 JumpPhase（Ground/Start/Loop/Land/ClimbUp）写入控制器。
        /// </summary>
        /// <remarks>
        /// 滞空动画分起跳/滞空循环/落地三段（Start/Loop/Land），是带历史依赖的状态序列
        /// （Start 播完才进 Loop、净下落超阈值才播 Land），纯条件规则无法表达。
        /// ClimbUp 为 AutoJump 越障专用相位，由攀爬动画（ClimbUp_1m_RM）的 root motion Override 接管位移，
        /// 不走物理跳跃（ComponentGltfPlayerAutoJump 触发时已撤销 JumpOrder）。
        /// 转换：
        /// - 离地边沿：上升（VelocityY &gt; JumpStartVelocityThreshold）→ Start；下降（走下悬崖等掉落）→ Loop
        /// - 落地边沿：净下落 &gt; JumpLandMinDropHeight → Land；否则 Ground（同高/短掉落不播 Land）
        /// - Jump_Start 播完（onComplete trigger "JumpStartComplete"）→ Loop
        /// - Jump_Land 播完（onComplete trigger "JumpLandComplete"）→ Ground
        /// - AutoJump 越障（ConsumeClimbUpPending）→ ClimbUp，期间保持不被离地/落地边沿覆盖
        /// - ClimbUp_1m_RM 播完（onComplete trigger "ClimbUpComplete"）→ Ground
        /// - 进水/飞行/梯子/骑乘/死亡 → 重置 Ground（避免出水/出飞行落地误播 Land）
        /// ClimbUp 期间 body 物理副作用（禁重力/碰撞/输入移动）由 API ApplyRootMotionPhysics 据 JSON physics 块自动应用。
        /// </remarks>
        void UpdateJumpPhase(AnimationController controller) {
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

            // AutoJump 越障标志（每帧消费清除，避免残留误触发后续离地）
            bool climbUpTriggered = m_componentAutoJump != null && m_componentAutoJump.ConsumeClimbUpPending();

            if (overridden) {
                // 进水/飞行/梯子/骑乘/死亡：重置相位
                m_jumpPhase = JumpPhaseGround;
            }
            else if (climbUpTriggered) {
                // AutoJump 越障：进 ClimbUp 相位，root motion Override 接管位移
                m_jumpPhase = JumpPhaseClimbUp;
            }
            else if (m_jumpPhase == JumpPhaseClimbUp) {
                // 攀爬中：保持 ClimbUp，仅 onComplete "ClimbUpComplete" 推进到 Ground
                // 不被离地/落地边沿覆盖（root motion 抬起会触发离地边沿，必须保护）
            }
            else if (onGround) {
                // 刚落地：净下落高度（起跳/下落时 Y - 当前 Y）超过 JumpLandMinDropHeight 才播 Land
                if (!m_prevOnGround) {
                    float dropped = m_takeoffY - componentBody.Position.Y;
                    m_jumpPhase = (dropped > JumpLandMinDropHeight) ? JumpPhaseLand : JumpPhaseGround;
                }
            }
            else {
                // 刚离地：上升=主动起跳→Start；下降=掉落→Loop
                // 持续空中时 Start→Loop 由 Jump_Start 完成事件推进，此处不动
                if (m_prevOnGround) {
                    m_takeoffY = componentBody.Position.Y;
                    m_jumpPhase = (velocityY > JumpStartVelocityThreshold) ? JumpPhaseStart : JumpPhaseLoop;
                }
            }
            m_prevOnGround = onGround;
            controller.Parameters.SetString("JumpPhase", m_jumpPhase);
        }

        /// <summary>
        /// 起床状态机：维护 IsWakingUp 布尔写入控制器（m_componentSleep 为空时跳过）。
        /// </summary>
        /// <remarks>
        /// 睡眠→醒来的过渡由 wakeup 动画（glb 内置 LayToIdle）一次性表达。
        /// - ComponentSleep.IsSleeping 下降沿（前一帧在睡、本帧醒来）→ IsWakingUp=true
        /// - wakeup 播完（onComplete trigger "WakeUpComplete"）→ IsWakingUp=false
        /// - 重新入睡或濒死（Health&lt;=0）→ IsWakingUp=false（清残留，防再睡/复活后卡过渡）
        /// IsWakingUp 为 true 时 Base 规则强制选 wakeup，播完才交还。
        /// </remarks>
        void UpdateWakeUpState(AnimationController controller) {
            if (m_componentSleep == null) {
                return;
            }
            bool sleeping = m_componentSleep.IsSleeping;
            if (sleeping) {
                m_isWakingUp = false;
            }
            else if (m_prevIsSleeping) {
                m_isWakingUp = true;
            }
            if (m_componentCreature.ComponentHealth.Health <= 0f) {
                m_isWakingUp = false;
            }
            m_prevIsSleeping = sleeping;
            controller.Parameters.SetBool("IsWakingUp", m_isWakingUp);
        }

        /// <summary>
        /// 闲时受寒/疲劳：维护 IsShivering、IsTired 布尔写入控制器（无状态，每帧重算）。
        /// </summary>
        /// <remarks>
        /// - IsShivering：患流感（ComponentFlu.HasFlu）或体温过低（Temperature &lt; 6）
        /// - IsTired：体力过低（Stamina &lt; 0.33）或睡眠过低（Sleep &lt; 0.2）
        /// 仅在站立 idle（SpeedAbs 已低于走/跑阈值）时由 Base 规则替换 idle；
        /// 蹲下 idle 时疲劳改用 kneeling_tired（Kneeling Tired），见 JSON 蹲下组。
        /// </remarks>
        void UpdateVitalState(AnimationController controller) {
            bool isShivering = (m_componentFlu != null && m_componentFlu.HasFlu)
                || (m_componentVitalStats != null && m_componentVitalStats.Temperature < 6f);
            bool isTired = m_componentVitalStats != null
                && (m_componentVitalStats.Stamina < 0.33f || m_componentVitalStats.Sleep < 0.2f);
            controller.Parameters.SetBool("IsShivering", isShivering);
            controller.Parameters.SetBool("IsTired", isTired);
        }

        /// <summary>
        /// 近战武器 idle：维护 IsHoldingMeleeWeapon 布尔写入控制器（无状态，每帧重算）。
        /// </summary>
        /// <remarks>
        /// 手持 WoodenClub/StoneClub/Spear/Machete 时 true（读 ComponentMiner.ActiveBlockValue 判 Block 类型）。
        /// 站立 idle 时由 Base 规则选 melee_idle（glb Sword_Idle）替换 idle（持剑优先于疲劳/受寒）；
        /// 持剑走/跑仍播 walk/run，蹲下/游泳/骑乘/跳跃等更高优先级规则不受影响。
        /// </remarks>
        void UpdateMeleeWeaponState(AnimationController controller) {
            bool holdingMeleeWeapon = false;
            if (m_componentMiner != null) {
                int value = m_componentMiner.ActiveBlockValue;
                if (value != 0) {
                    Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
                    holdingMeleeWeapon = block is WoodenClubBlock
                        || block is StoneClubBlock
                        || block is SpearBlock
                        || block is MacheteBlock;
                }
            }
            controller.Parameters.SetBool("IsHoldingMeleeWeapon", holdingMeleeWeapon);
        }

        /// <summary>
        /// 动画事件：处理跳跃/起床动画完成 trigger，推进状态。
        /// </summary>
        public override void HandleAnimationEvent(AnimationController controller, AnimationEvent animationEvent) {
            switch (animationEvent.Name) {
                case "JumpStartComplete": m_jumpPhase = JumpPhaseLoop; break;
                case "JumpLandComplete": m_jumpPhase = JumpPhaseGround; break;
                case "ClimbUpComplete": {
                    // root motion 完成：清速 + 物理恢复由 API ApplyRootMotionPhysics
                    // 在 config 切 null 时自动执行（ClearVelocityOnExit + RestoreRootMotionPhysics）。
                    m_jumpPhase = JumpPhaseGround;
                    break;
                }
                case "WakeUpComplete": m_isWakingUp = false; break;
            }
        }
    }
}
