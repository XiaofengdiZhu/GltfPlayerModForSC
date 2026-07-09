using System;
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

        // pickup 动画 source 名（GltfPlayer.json pickup 别名 → PickUp_Table）
        private const string PickupSource = "PickUp_Table";

        // 受击动画 source 名（须与 GltfPlayer.json attacked 别名 source 一致；换动画须同步改此处）
        private const string AttackedSource = "Pistol_Aim_Up";

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

        // 拾取物飞行检测：扫 SubsystemPickables 找飞向本玩家的 Pickable（FlyToGatherer 指向本实体 gatherer）
        private ComponentPickableGatherer m_componentPickableGatherer;
        private SubsystemPickables m_subsystemPickables;

        // pickup 锁：true=Base 层正在播 pickup，IsPickingUp 强制 true 防 FlyToGatherer 清空瞬间抖动重播。
        // 置锁靠检测 Base 层动画==PickUp_Table（非 flyingToMe）；解锁靠 PickUpComplete/PickUpInterrupt 事件 + 兜底（见 UpdatePickupState）。
        private bool m_pickupActive;

        // pickup 刚播完标志：PickUpComplete 事件置位，下帧 Sync 强制 IsPickingUp=false 一帧，让规则离开 pickup（path 变），
        // 以便 flyingToMe 仍 true 时重选 pickup 触发重切重播（preservePose 保持 pickup 末态，path 不变会跳过重切 → 只播一次）。
        private bool m_pickupJustCompleted;

        // 受击待处理：ComponentBody.Attacked 事件（仅攻击命中）置位，下帧 Sync 消费触发 attacked 动画。
        private bool m_attackedPending;

        // attacked 锁：true=UpperBody 层正在播 attacked，IsAttacked 强制 true 防 blend 期被打断/重复触发。
        // 置锁靠检测 UpperBody 层动画==AttackedSource（Pistol_Aim_Up）；解锁靠 AttackedComplete 事件 + 兜底（见 UpdateAttackedState）。
        private bool m_attackedActive;

        // attacked 刚播完标志：AttackedComplete 事件置位，下帧 Sync 强制 IsAttacked=false 一帧让规则离开 attacked（path 变），
        // 以便再次受击时重选 attacked 触发权重渐入重播（preservePose 保持后倾末态，path 不变会跳过重切 → 只播一次）。
        private bool m_attackedJustCompleted;

        // ===== head IK 视线追踪 =====
        // head IK 链名（OnControllerCreated 注册，SyncAnimationParameters 每帧 SetIKAim）
        private const string HeadIKChain = "Head";

        // head IK 链是否注册成功（OnControllerCreated 首注册 + Sync 兜底补注册）
        private bool m_headIKRegistered;

        // head aim 平滑是否已禁用（首次 SetIKAim 后置 AimSmoothTime=0，消除默认 150ms 跟随延迟）
        private bool m_headAimSmoothDisabled;

        // head 局部"脸朝前"轴（经颈弯曲，模型相关）。默认 +Z（game forward）。
        // 手测调：head 转错方向时改 (1,0,0)/(-1,0,0)/(0,0,-1) 等（见 Task 4）。
        private Vector3 m_headAimAxis = new Vector3(0f, 0f, 1f);

        // 模型空间 forward 符号：视线方向向量 z 分量符号。默认 +1（forward=+Z），实际 forward=-Z 改 -1。
        private const int HeadForwardSign = -1;

        // yaw（水平）/pitch（垂直）方向反转（转向反了改 true）
        private const bool InvertHeadYaw = false;
        private const bool InvertHeadPitch = false;

        // 最大转头角度（度，运行时转弧度钳制，防脖子转过头）
        private const float MaxHeadYawDegrees = 70f;    // 水平左右各 70°
        private const float MaxHeadPitchDegrees = 50f;  // 上下各 50°

        /// <summary>
        /// 当前是否在 ClimbUp 相位（攀爬动画进行中）。供 ComponentGltfPlayerAutoJump 抑制重复触发。
        /// </summary>
        public bool IsClimbing => m_jumpPhase == JumpPhaseClimbUp;

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
            m_componentPickableGatherer = Entity.FindComponent<ComponentPickableGatherer>();
            m_subsystemPickables = Project.FindSubsystem<SubsystemPickables>();

            // 订阅受击事件：ComponentBody.Attacked 仅在攻击命中时触发（Attackment.cs:227），环境伤害不触发。
            var componentBody = m_componentCreature.ComponentBody;
            if (componentBody != null) {
                componentBody.Attacked += delegate { m_attackedPending = true; };
            }
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
            // 新 controller：重置 IK 标志，强制重注册。换模型 SetModel 重建 controller，
            // m_headIKRegistered 仍 true 会使 RegisterHeadIK 早退 → 新 controller 无链 → head IK 静默失效。
            m_headIKRegistered = false;
            m_headAimSmoothDisabled = false;
            controller.Parameters.SetString("JumpPhase", m_jumpPhase);
            controller.Parameters.SetBool("IsWakingUp", m_isWakingUp);
            controller.Parameters.SetBool("IsShivering", false);
            controller.Parameters.SetBool("IsTired", false);
            controller.Parameters.SetBool("IsHoldingMeleeWeapon", false);
            controller.Parameters.SetBool("IsPickingUp", false);
            controller.Parameters.SetBool("IsAttacked", false);

            // 注册 head IK 链（[neck, head]，SingleBoneIK）。幂等去重，换模型自动重注册。
            // model 偶未就绪则返回 null，由 SyncAnimationParameters 兜底补注册。
            RegisterHeadIK(controller);
        }

        /// <summary>
        /// 同步动画参数：每帧 controller.Update 之前调用，派发到各相位机 Update* 方法。
        /// </summary>
        public override void SyncAnimationParameters(AnimationController controller) {
            UpdateJumpPhase(controller);
            UpdateWakeUpState(controller);
            UpdateVitalState(controller);
            UpdateMeleeWeaponState(controller);
            UpdatePickupState(controller);
            UpdateAttackedState(controller);
            UpdateMoveSpeed(controller);

            // head IK：兜底补注册（OnControllerCreated 时 model 未就绪则此处补）+ 每帧设 aim
            RegisterHeadIK(controller);
            UpdateHeadIK(controller);
        }

        /// <summary>
        /// 带符号水平移动速率 MoveSpeed（前进正/后退负/侧移正），供 walk/run/crouch_walk/swim 控制播放速率。
        /// </summary>
        /// <remarks>
        /// API 基类算的 Speed=Dot(velocity, forward)（前方分量，纯侧移≈0）、SpeedAbs=velocity.Length()（绝对值，无符号）。
        /// glTF walk/run 速率原用 [Speed]：纯侧移 Speed≈0 → 动画冻结（即便 SpeedAbs 非零已选中 walk）。
        /// MoveSpeed 用水平速度大小作幅度，符号按移动方向相对前方夹角：明显后退（cos&lt;-0.3，夹角&gt;107°）取负反向播，
        /// 其余（前进/侧移/静止）取正正向播。后退反向播 walk（腿后摆），侧移正向播（不冻结）。
        /// </remarks>
        void UpdateMoveSpeed(AnimationController controller) {
            var body = m_componentCreature.ComponentBody;
            if (body == null) {
                controller.Parameters.SetFloat("MoveSpeed", 0f);
                return;
            }
            Vector3 vel = body.Velocity;
            Vector3 velXZ = new Vector3(vel.X, 0f, vel.Z);
            float xzLen = velXZ.Length();
            // body 水平前方向（XZ 分量）
            Vector3 fwdFull = Matrix.CreateFromQuaternion(body.Rotation).Forward;
            Vector3 fwd = new Vector3(fwdFull.X, 0f, fwdFull.Z);
            float fwdLen = fwd.Length();
            float cos = 1f;
            if (xzLen > 0.001f && fwdLen > 0.001f) {
                cos = Vector3.Dot(velXZ, fwd) / (xzLen * fwdLen);
            }
            // 明显后退反向播，其余正向播
            float sign = cos < -0.3f ? -1f : 1f;
            controller.Parameters.SetFloat("MoveSpeed", sign * xzLen);
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
        /// 拾取动作：有 Pickable 飞向本玩家时触发 pickup 动画（PickUp_Table，完整播放）。
        /// </summary>
        /// <remarks>
        /// 触发源：ComponentPickableGatherer 把进入吸引距离（1.75m）且可拾取的 Pickable 设 FlyToPosition + FlyToGatherer=本 gatherer，
        /// Pickable 随后飞向玩家。本方法扫 SubsystemPickables.Pickables 找 FlyToGatherer 指向本实体的。
        /// IsPickingUp 仅高于 idle（见 JSON base 规则），走/跑/游泳/骑乘等更高优先级规则胜出时 pickup 不播。
        /// 防重入（m_pickupActive 锁）：锁语义=「pickup 正在 Base 层播放」。置锁靠检测 Base 层动画==PickUp_Table（非 flyingToMe），
        /// 故走/跑中 pickup 不播时不锁，拾取物飞走后 IsPickingUp 自然回落（不卡死）。
        /// 锁定期 IsPickingUp 强制 true，防 Pickable 飞到被拾取（FlyToGatherer 清空）瞬间抖动 → pickup 中途切走再重播。
        /// 解锁：pickup 播完（onComplete PickUpComplete）/被打断（onInterrupt PickUpInterrupt）事件 → m_pickupActive=false；
        /// 兜底：锁着但 Base 层已非 pickup（事件缺失/第一人称冻结后切回）→ 解锁防卡死。
        /// 重播：pickup 播完后 preservePose 保持末态，path 不变致规则跳过重切；m_pickupJustCompleted（PickUpComplete 置位）
        /// 下帧强制 IsPickingUp=false 让规则离开 pickup（path 变），flyingToMe 仍 true 时重选 → 重切重播（否则只播一次）。
        /// 仅 Player 实体挂 ComponentPickableGatherer；服装 model 无（gatherer=null → 不触发）。
        /// </remarks>
        void UpdatePickupState(AnimationController controller) {
            // pickup 刚播完：preservePose 保持末态致 path 不变，规则跳过重切 → 强制 IsPickingUp=false 一帧
            // 让规则选 idle/其他（path 变），下帧 flyingToMe=true 时重选 pickup（path 变）触发重切重播。
            if (m_pickupJustCompleted) {
                m_pickupJustCompleted = false;
                controller.Parameters.SetBool("IsPickingUp", false);
                return;
            }
            bool flyingToMe = false;
            if (m_componentPickableGatherer != null
                && m_subsystemPickables != null) {
                foreach (Pickable pickable in m_subsystemPickables.Pickables) {
                    if (pickable.FlyToPosition.HasValue
                        && pickable.FlyToGatherer == m_componentPickableGatherer) {
                        flyingToMe = true;
                        break;
                    }
                }
            }

            // Base 层当前是否在播 pickup。本方法在 controller.Update 前，读的是上帧 Update 后状态。
            // AnimationPlayer 过渡期返回 TargetPlayer，故 transition 切入/稳态/PreservePose 末态均命中。
            AnimationLayer baseLayer = null;
            AnimationLayer[] layers = controller.m_layers;
            if (layers != null) {
                foreach (AnimationLayer l in layers) {
                    if (l.Index == 0) {
                        baseLayer = l;
                        break;
                    }
                }
            }
            bool basePlayingPickup = baseLayer?.AnimationPlayer?.Animation?.Name == PickupSource;

            bool isPickingUp;
            if (m_pickupActive) {
                // pickup 正在播放：锁 IsPickingUp=true，等 PickUpComplete/PickUpInterrupt 事件解锁。
                if (!basePlayingPickup) {
                    // 兜底解锁：pickup 已不在 Base 层（被打断事件未到，或第一人称冻结后切回非 pickup 态）→ 解锁防卡死。
                    // 正常退出靠事件，此分支仅覆盖事件缺失/冻结边界。
                    m_pickupActive = false;
                    isPickingUp = flyingToMe;
                }
                else {
                    isPickingUp = true;
                }
            }
            else {
                // 未锁：IsPickingUp 直接反映 flyingToMe。规则按优先级决定是否真播 pickup——
                // 走/跑/游泳/骑乘等更高优先级规则胜出时 pickup 不播（不锁），拾取物飞走后 IsPickingUp 自然回落，不卡。
                isPickingUp = flyingToMe;
                // pickup 实际开始播放（规则选了它）→ 锁，确保播完整段不被 flyingToMe 抖动打断。
                // 比"凭 flyingToMe 置锁"晚一帧（transition 当帧切入，下帧 Sync 才检测到），窗口极小可接受。
                if (flyingToMe && basePlayingPickup) {
                    m_pickupActive = true;
                }
            }
            controller.Parameters.SetBool("IsPickingUp", isPickingUp);
        }

        /// <summary>
        /// 受击动作：被攻击命中时触发 attacked 动画（Pistol_Aim_Up 仅取首帧后倾姿势，blend 进出实现「后倾一瞬即回」）。
        /// </summary>
        /// <remarks>
        /// 触发源：ComponentBody.Attacked 事件（仅攻击命中，Attackment.cs:227），环境伤害不触发 → m_attackedPending=true。
        /// hit 状态层 UpperBody（boneMask spine_01 排除双臂 upperarm_l/r，保留肩膀 clavicle），[IsAttacked]==true 选 attacked 别名
        /// （endPhase=0 仅后倾首帧，目标 player 首帧即 IsPlaying=false）。
        /// CheckAnimationCompletion 的 blending 判据（过渡/权重渐入期不判完成）保证 onComplete 在权重渐入完成后才触发，
        /// 否则 blend 中误触发会刚切入就切回（见 AnimationController.CheckAnimationCompletion）。
        /// 防重入（m_attackedActive 锁）：锁语义=「attacked 正在 UpperBody 层播放」。置锁靠检测 UpperBody 层动画==AttackedSource，
        /// blend 期间 IsAttacked 强制 true 防中途切走。解锁：attacked blend 完触发 onComplete（AttackedComplete）→ m_attackedActive=false；
        /// 兜底：锁着但 UpperBody 已非 attacked（事件缺失/边界）→ 解锁防卡死。
        /// 重播：AttackedComplete 置 m_attackedJustCompleted，下帧强制 IsAttacked=false 一帧让规则离开 attacked（path 变），
        /// 再次受击（m_attackedPending）时重选 attacked（path 变）触发权重渐入重播。
        /// </remarks>
        void UpdateAttackedState(AnimationController controller) {
            // attacked 刚播完：强制 IsAttacked=false 一帧让规则选 null（path 变），UpperBody 停用；
            // 再次受击（m_attackedPending）时下帧重选 attacked（path 变）触发重播。
            if (m_attackedJustCompleted) {
                m_attackedJustCompleted = false;
                controller.Parameters.SetBool("IsAttacked", false);
                return;
            }

            // UpperBody 层当前是否在播 attacked。本方法在 controller.Update 前，读上帧 Update 后状态。
            // AnimationPlayer 过渡期返回 TargetPlayer，故 transition 切入/稳态/PreservePose 末态均命中。
            AnimationLayer upperBodyLayer = null;
            AnimationLayer[] layers = controller.m_layers;
            if (layers != null) {
                foreach (AnimationLayer l in layers) {
                    if (l.Name == "UpperBody") {
                        upperBodyLayer = l;
                        break;
                    }
                }
            }
            // UpperBody 层当前是否在「主动」播 attacked（Animation.Name == AttackedSource）。
            // 排除停用渐降期：渐降期主 player 仍按 preservePose 采样末态，但层已在淡出，不应视为「在播」
            // ——否则渐降期误判重新 IsAttacked=true 会打断停用，后仰永久保持。
            bool upperBodyPlayingAttacked = upperBodyLayer != null
                && !upperBodyLayer.m_deactivating
                && upperBodyLayer.AnimationPlayer?.Animation?.Name == AttackedSource;

            bool isAttacked;
            if (m_attackedActive) {
                // attacked 正在播放：锁 IsAttacked=true，等 AttackedComplete 事件解锁。不清 pending（备重播）。
                if (!upperBodyPlayingAttacked) {
                    // 兜底解锁：attacked 已不在 UpperBody 层（事件缺失/边界）→ 解锁防卡死。
                    m_attackedActive = false;
                    isAttacked = m_attackedPending;
                    m_attackedPending = false;
                }
                else {
                    isAttacked = true;
                }
            }
            else {
                // 未锁：pending 保持到 attacked 实际开始播放（upperBodyPlayingAttacked=true）才清 + 锁。
                // 否则 pending 一次性消费后下帧 IsAttacked=false，规则 null 立即停用——attacked 刚切入就被切走（完全无效）。
                // （与 pickup 不同：pickup 信号 flyingToMe 持续，下帧仍 true；attacked 信号事件单次触发，须保持到开播。）
                if (upperBodyPlayingAttacked) {
                    // attacked 已在播：锁 + 清 pending（播放期 IsAttacked=true 由锁保证）。
                    m_attackedActive = true;
                    isAttacked = true;
                    m_attackedPending = false;
                }
                else {
                    // 还没开始播（首帧或规则尚未选它）：IsAttacked=pending，pending 保持直到播放开始。
                    isAttacked = m_attackedPending;
                }
            }
            controller.Parameters.SetBool("IsAttacked", isAttacked);
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
                case "PickUpComplete": {
                    // pickup 播完：解锁 + 标记刚完成，下帧强制 IsPickingUp=false 离开 pickup（path 变）以便重播
                    m_pickupActive = false;
                    m_pickupJustCompleted = true;
                    break;
                }
                case "PickUpInterrupt": {
                    // pickup 被打断：解锁（Base 已被高优先级切走，path 已变，无需标记重播）
                    m_pickupActive = false;
                    break;
                }
                case "AttackedComplete": {
                    // attacked blend 完成：解锁 + 标记刚完成，下帧强制 IsAttacked=false 离开 attacked（path 变）以便重播
                    m_attackedActive = false;
                    m_attackedJustCompleted = true;
                    break;
                }
            }
        }

        /// <summary>
        /// 注册 head IK 链（[neck, head]，SingleBoneIK，aim 模式）。幂等：已注册跳过。
        /// </summary>
        /// <remarks>
        /// 文档 AnimationAdvancedTopics.md:315-333：OnControllerCreated 注册（每次 controller 创建/重建触发，去重幂等）。
        /// head 是 neck 子，maxChainLength=2 → 链 [neck, head]，SingleBoneIK 转 root（neck）让 end（head）朝目标。
        /// AimAxis 是 head 局部"脸朝前"轴（m_headAimAxis），经 endWorldTransform 变换得当前模型空间脸朝向。
        /// model 就绪由 ComponentModel.SetModel 守卫（OnControllerCreated 时 model 必非空）；
        /// 链构建失败（骨骼缺失/链<2）返回 null，m_headIKRegistered 保持 false，Sync 兜底重试。
        /// </remarks>
        void RegisterHeadIK(AnimationController controller) {
            if (m_headIKRegistered) {
                return;
            }
            IKChain chain = controller.RegisterAndBuildIKChain(HeadIKChain, "head", "SingleBoneIK", 2);
            if (chain != null) {
                chain.AimAxis = m_headAimAxis;
                m_headIKRegistered = true;
            }
        }

        /// <summary>
        /// 每帧设 head IK 目标方向：LookAngles（水平 yaw + 俯仰 pitch）合成模型空间视线方向。
        /// </summary>
        /// <remarks>
        /// LookAngles.X=水平视线 yaw（相对 body forward），.Y=俯仰 pitch（ComponentLocomotion:380 验证）。
        /// 模型空间目标方向 = body 初始 forward（+Z * HeadForwardSign）绕 up(+Y) 转 yaw + pitch：
        ///   dir = (sin(yaw)cos(pitch), sin(pitch), HeadForwardSign * cos(yaw)cos(pitch))
        /// IK 在层混合后求解（ComputeBoneTransforms:1223），看到 driver 已转的 pelvis → RotationBetweenVectors
        /// 自动算补偿，head 始终朝视线方向（身体追移动方向时 head 转回视线）。
        /// 停用（死亡/躺睡/攀爬）→ ClearIKTarget，head 放松回动画姿态。
        /// 钳制 yaw/pitch 到 MaxHeadYaw/Pitch 防脖子转过头。
        /// </remarks>
        void UpdateHeadIK(AnimationController controller) {
            // 停用条件：死亡/躺下/攀爬时 head 不追踪（与 bodyturn 层停用条件对齐：LieDownFactor==0）。
            // 用 LieDownFactor 而非 IsSleeping：起身过渡 IsSleeping 已 false 但 LieDownFactor>0（身体还躺），
            // 此时 IK 激活会 aim 异常。LieDownFactor 由 ComponentHumanModel.SyncAnimationParameters 写入（先于本参与者）。
            float lieDown = controller.Parameters.GetFloat("LieDownFactor");
            bool active = m_componentCreature.ComponentHealth.Health > 0f
                && lieDown == 0f
                && m_jumpPhase != JumpPhaseClimbUp;
            if (!active || !m_headIKRegistered) {
                controller.ClearIKTarget(HeadIKChain);
                return;
            }

            var lookAngles = m_componentCreature.ComponentLocomotion.LookAngles;
            float maxYaw = MathUtils.DegToRad(MaxHeadYawDegrees);
            float maxPitch = MathUtils.DegToRad(MaxHeadPitchDegrees);
            float yaw = MathUtils.Clamp(
                lookAngles.X * (InvertHeadYaw ? -1f : 1f), -maxYaw, maxYaw);
            float pitch = MathUtils.Clamp(
                lookAngles.Y * (InvertHeadPitch ? -1f : 1f), -maxPitch, maxPitch);

            // 模型空间视线方向（forward=±Z, up=+Y）
            float cosPitch = MathF.Cos(pitch);
            Vector3 dir = new Vector3(
                MathF.Sin(yaw) * cosPitch,
                MathF.Sin(pitch),
                HeadForwardSign * MathF.Cos(yaw) * cosPitch);

            controller.SetIKAim(HeadIKChain, dir, 1.0f);

            // 首次 SetIKAim 后禁用 aim 平滑（默认 AimSmoothTime=0.15s 致 head 落后摄像机 ~150ms）。
            // 视线追踪需 1:1 即时映射。GetIKTarget 返回 SetIKAim 创建的目标。
            if (!m_headAimSmoothDisabled) {
                IKTarget target = controller.GetIKTarget(HeadIKChain);
                if (target != null) {
                    target.AimSmoothTime = 0f;
                }
                m_headAimSmoothDisabled = true;
            }
        }
    }
}
