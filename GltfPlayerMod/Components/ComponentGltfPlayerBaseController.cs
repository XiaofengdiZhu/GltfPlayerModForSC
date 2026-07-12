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
        private const string OverhandThrowSource = "OverhandThrow";

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

        // ===== 近战攻击（动画驱动，Activity 层）=====
        // attack source 名（须与 GltfPlayer.json attack_cross/attack_jab 别名 source 一致；换动画须同步改）
        private const string AttackCrossSource = "Punch_Cross"; // 右手（parity 0）
        private const string AttackJabSource = "Punch_Jab";     // parity 1

        // 攻击状态机：Idle（无攻击）→Winding（pending 启动，等 MeleeImpact）→Impact（命中后）→Idle（MeleeAttackComplete）
        private enum AttackState { Idle, Winding, Impact }
        private AttackState m_attackState = AttackState.Idle;

        // 左右手交替：0=Punch_Cross 右手，1=Punch_Jab；MeleeAttackComplete 时翻转（下次另一手）。首次 0=右手先。
        private int m_attackParity;

        // attack 锁：true=Activity 层正在播 attack。置锁靠检测层动画==Punch_Cross/Jab（仿 m_attackedActive）。
        // 兜底解锁靠 MeleeAttackComplete latch（m_attackJustCompleted）。防 blend 期 pending 抖动中途切走。
        private bool m_attackActive;

        // attack 刚播完：MeleeAttackComplete 置位，下帧强制 IsAttacking=false 让规则离开 attack（path 变）以便重播/交还。
        private bool m_attackJustCompleted;

        // 攻击连击模式（MeleeImpact 事件 Data 解析）。
        private enum AttackComboMode {
            // 等 MeleeAttackComplete 事件才接受下次攻击（默认）。
            Complete,
            // MeleeImpact 后即可重触发（parity 翻转下一手）。
            Impact,
        }

        private AttackComboMode m_attackComboMode = AttackComboMode.Complete;

        // ===== 全动作 Activity 动画（Dig/Place/Use/Interact/Aim/fire/throw）=====
        // 引用（可空：服装 model 无 miner/player）
        private ComponentPlayer m_componentPlayer;
        private SubsystemTerrain m_subsystemTerrain;
        private SubsystemProjectiles m_subsystemProjectiles;

        // Place/Use：pending 即时派发（ExecutePlace/Use 当帧），anim cosmetic 单次播（loop=false）；
        // onComplete（PlaceLoopComplete/UseLoopComplete）→ justCompleted → 下帧强制 IsPlacing/IsUsing=false 停。
        // 注：loop 必须 false——CheckAnimationCompletion 仅 !isLooping 才触发 onComplete（AnimationController.cs:668-681），
        // loop=true 的 onComplete 永不触发 → IsPlacing 卡死 → 动画无限循环。
        private bool m_placingPlant;
        private bool m_placeJustCompleted;     // PlaceLoopComplete → 下帧强制 IsPlacing=false 停
        private bool m_useJustCompleted;

        // Interact：pending 延迟到 InteractImpact 事件才 ExecuteInteract；m_interactActive 防 pending 期每帧重分类。
        private bool m_interactActive;
        private bool m_interactChest;          // pending 目标分类（用公开 InteractPendingValue 取按下时存的目标）
        private bool m_interactJustCompleted;

        // fire/throw：ProjectileAdded 事件 latch（仅弓→fire）；投掷物走 Aim pending（IsPending(Aim) 触发 throw_overhand）。
        private bool m_projectileFirePending;  // OnProjectileAdded 置位（仅本玩家，持弓/弩/火枪）
        private bool m_throwRequest;           // Base 路径待播（首帧设，次帧判 Base 是否播 throw）
        private bool m_throwActive;            // Base 路径播放锁（开播到 ThrowComplete/打断兜底）
        private bool m_fireJustCompleted;
        private bool m_throwJustCompleted;     // Base ThrowComplete latch
        private bool m_throwUpperRequest;      // Activity 路径待播（首帧设，次帧判 Activity 是否播 throw）
        private bool m_throwUpperActive;       // Activity 路径播放锁（上半身 throw，开播到 ThrowComplete/高优先级打断）
        private bool m_throwUpperJustCompleted;// Activity ThrowComplete latch

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
        private const float MaxHeadYawDegrees = 60f;    // 水平左右各 60°，超过了效果不好
        private const float MaxHeadPitchDegrees = 80f;  //  垂直最大 80°
        private const float MinHeadPitchDegrees = -15f; // 垂直最小 -15°，更低效果不好

        // head 俯仰校正（度，向下压）：模型 head face-forward 与 AimAxis(+Z) 有 ~45° pitch 偏移，
        // 致 IK 把 +Z 对到水平 dir 时 head 实际仰 ~45°。从 pitch 减此值补偿（手测定）。
        private const float HeadPitchCorrectionDegrees = 45f;

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
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>();
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>();
            m_subsystemProjectiles = Project.FindSubsystem<SubsystemProjectiles>();

            // 订阅受击事件：ComponentBody.Attacked 仅在攻击命中时触发（Attackment.cs:227），环境伤害不触发。
            var componentBody = m_componentCreature.ComponentBody;
            if (componentBody != null) {
                componentBody.Attacked += delegate { m_attackedPending = true; };
            }
            // 订阅抛射物生成：仅运行时发射触发（世界加载不触发），按 OwnerEntity==本实体过滤本玩家发射/投掷。
            if (m_subsystemProjectiles != null) {
                m_subsystemProjectiles.ProjectileAdded += OnProjectileAdded;
            }
        }

        /// <summary>
        /// 抛射物生成事件：仅本玩家（OwnerEntity==Entity）发射/投掷时置 latch，UpdateFireState/UpdateThrowState 消费。
        /// </summary>
        void OnProjectileAdded(Projectile projectile) {
            if (projectile.OwnerEntity != Entity) {
                return;
            }
            // 仅弓/弩/火枪走 ProjectileAdded 触发 fire（松手即抛）。投掷物走 Aim pending（throw_overhand 播 25% 才抛），
            // 25% 后 FireProjectile 也触发本事件——此处忽略投掷物防重触 throw（pending 路径不依赖此事件）。
            int blockValue = m_componentMiner != null ? m_componentMiner.ActiveBlockValue : 0;
            Block block = blockValue != 0 ? BlocksManager.Blocks[Terrain.ExtractContents(blockValue)] : null;
            if (block is BowBlock || block is CrossbowBlock || block is MusketBlock) {
                m_projectileFirePending = true;
            }
        }

        /// <summary>
        /// 退订抛射物事件，防死控制器被 subsystem 委托保活。
        /// </summary>
        public override void Dispose() {
            if (m_subsystemProjectiles != null) {
                m_subsystemProjectiles.ProjectileAdded -= OnProjectileAdded;
            }
            base.Dispose();
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
            controller.Parameters.SetBool("IsAttacking", false);
            controller.Parameters.SetFloat("AttackComboParity", 0f);
            controller.Parameters.SetBool("IsDigging", false);
            controller.Parameters.SetBool("IsDiggingPlant", false);
            controller.Parameters.SetBool("IsPlacing", false);
            controller.Parameters.SetBool("IsPlacingPlant", false);
            controller.Parameters.SetBool("IsUsing", false);
            controller.Parameters.SetBool("IsInteracting", false);
            controller.Parameters.SetBool("IsInteractChest", false);
            controller.Parameters.SetBool("IsAiming", false);
            controller.Parameters.SetBool("IsFiring", false);
            controller.Parameters.SetBool("IsThrowing", false);
            controller.Parameters.SetBool("IsThrowingUpperBody", false);

            // 换模型（SetModel 重建 controller）时重置 latch 字段，防残留驱动 Update* 致虚假 dig/place/use/interact/fire/throw 循环。
            m_digActive = false;
            m_digPlantCached = false;
            m_digJustCompleted = false;
            m_placingPlant = false;
            m_placeJustCompleted = false;
            m_useJustCompleted = false;
            m_interactActive = false;
            m_interactChest = false;
            m_interactJustCompleted = false;
            m_projectileFirePending = false;
            m_throwRequest = false;
            m_throwActive = false;
            m_fireJustCompleted = false;
            m_throwJustCompleted = false;
            m_throwUpperRequest = false;
            m_throwUpperActive = false;
            m_throwUpperJustCompleted = false;
            // 旧动作 latch（attack/pickup/attacked）同步重置，防换模型残留（m_attackState=Winding 残留致虚假 attack 重播 + 过期 MeleeImpact）。
            m_attackState = AttackState.Idle;
            m_attackActive = false;
            m_attackJustCompleted = false;
            m_pickupActive = false;
            m_pickupJustCompleted = false;
            m_attackedActive = false;
            m_attackedPending = false;
            m_attackedJustCompleted = false;
            // 配置 Attack/Place/Use/Interact 走 pending。仅 glTF 玩家有此 controller；
            // dae/AI 的 miner 无 controller → m_requiresPending 恒 None → 原版立即执行。每次 controller 重建（换模型）重设，幂等。
            if (m_componentMiner != null) {
                m_componentMiner.SetRequiresPending(ComponentMiner.PendingAction.Attack);
                m_componentMiner.SetRequiresPending(ComponentMiner.PendingAction.Place);
                m_componentMiner.SetRequiresPending(ComponentMiner.PendingAction.Use);
                m_componentMiner.SetRequiresPending(ComponentMiner.PendingAction.Interact);
                // Aim pending：投掷物松手不立即抛，存 pending 等 throw_overhand 播 25%（AimImpact event）/被打断时 ExecuteAim 抛。
                // 仅投掷物（API ComponentMiner.Aim 拦截处按 ThrowableBlockBehavior 判定）；弓/弩/火枪不 pending（松手即抛走 ProjectileAdded）。
                m_componentMiner.SetRequiresPending(ComponentMiner.PendingAction.Aim);
            }

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
            UpdateAttackState(controller);
            UpdateDigState(controller);
            UpdatePlaceState(controller);
            UpdateUseState(controller);
            UpdateInteractState(controller);
            UpdateAimState(controller);
            UpdateFireState(controller);
            UpdateThrowState(controller);
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
            // 瞬时 WalkOrder（LastWalkOrder）→ WalkOrderX/Y，供 GltfBodyTurnDriver 自算带符号转向角
            // （Vector2.Angle(UnitY, WalkOrder)，纯后退归零/斜后转向）。不用平滑 HeadingOffset：后者爬升致后退"先转后瞬间转正"。
            var lastWalk = m_componentCreature.ComponentLocomotion.LastWalkOrder;
            controller.Parameters.SetFloat("WalkOrderX", lastWalk?.X ?? 0f);
            controller.Parameters.SetFloat("WalkOrderY", lastWalk?.Y ?? 0f);
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
        /// - AutoJump 越障（ConsumeClimbUp(out climbDir)）→ ClimbUp，body.Rotation 即时转朝爬向；期间保持不被离地/落地边沿覆盖
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

            // AutoJump 越障标志（每帧消费清除，避免残留误触发后续离地）；climbDir=世界水平爬向
            Vector3 climbDir = default;
            bool climbUpTriggered = m_componentAutoJump != null && m_componentAutoJump.ConsumeClimbUp(out climbDir);

            if (overridden) {
                // 进水/飞行/梯子/骑乘/死亡：重置相位
                m_jumpPhase = JumpPhaseGround;
            }
            else if (climbUpTriggered) {
                // AutoJump 越障：进 ClimbUp 相位，root motion Override 接管位移
                m_jumpPhase = JumpPhaseClimbUp;
                // 多向转体：body.Rotation 转朝爬向 climbDir，同一前向 clip 即物理移向障碍
                // （TranslationApplier 用 body.Rotation 重定向根运动 localVel→world）。
                // 纯前向（Dot≈1）不转，保持原前爬行为；爬完留向（不恢复，玩家鼠标自转）。
                // 时序：本 Sync 在 controller.Update 前、ComponentModel 根运动前 → 当帧根运动即见新 yaw。
                Vector3 fwdF = new Vector3(componentBody.Matrix.Forward.X, 0f, componentBody.Matrix.Forward.Z);
                if (fwdF.LengthSquared() > 1e-6f && Vector3.Dot(climbDir, Vector3.Normalize(fwdF)) < 0.999f) {
                    // 本引擎 body forward=-Z 绕 +Y：forward=(-sin yaw, 0, -cos yaw)。
                    // 要 forward 对齐 climbDir → yaw=atan2(-climbDir.X, -climbDir.Z)（直接反解，无符号歧义）。
                    float targetYaw = MathF.Atan2(-climbDir.X, -climbDir.Z);
                    componentBody.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, targetYaw);
                }
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
                int blockValue = m_componentMiner.ActiveBlockValue;
                if (blockValue != 0) {
                    Block block = BlocksManager.Blocks[Terrain.ExtractContents(blockValue)];
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
            bool basePlayingPickup = BaseLayerIsPlaying(controller, PickupSource);

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
        /// 攻击动作（动画驱动，Activity 层）：读 miner.IsPending(Attack) → IsAttacking 触发 attack_cross/attack_jab 交替。
        /// Activity 层 attack 别名 events 灌 MeleeImpact@0.3；MeleeImpact 事件（HandleAnimationEvent）调 miner.ExecuteHit。
        /// </summary>
        /// <remarks>
        /// 与 UpdateAttackedState 同构（锁/latch/层查）。锁语义=「attack 正在 Activity 层播放」，置锁靠检测层动画
        /// ==Punch_Cross/Jab（排除停用渐降期 m_deactivating），防 blend 期 IsAttacking 抖动。
        /// 状态机：Idle→Winding（pending 启动）→Impact（MeleeImpact 事件设）→Idle（MeleeAttackComplete 事件设 justCompleted）。
        /// parity：0=Punch_Cross 右手，1=Punch_Jab，MeleeAttackComplete 时翻转（当前攻击期间稳定，完成后翻为下次）。
        /// comboMode=impact：Impact 态遇新 pending 立即重触发（parity 翻转下一手）；complete：等 MeleeAttackComplete。
        /// 重播：MeleeAttackComplete 置 m_attackJustCompleted，下帧强制 IsAttacking=false 让规则离开 attack（path 变），
        /// 下次 pending 重选 attack（path 变）触发重切重播（preservePose 保末态，path 不变会跳过重切→只播一次）。
        /// glTF 攻击不 Poke（chop 留给 dig）；HitInterval 硬底线在 miner.Hit 入口不变。
        /// </remarks>

        void UpdateAttackState(AnimationController controller) {
            // attack 刚播完：强制 IsAttacking=false 一帧让规则选 null（path 变），Activity 停用；
            // 下次 pending（miner.IsPending(Attack)）时下帧重选 attack（path 变）触发重播。
            if (m_attackJustCompleted) {
                m_attackJustCompleted = false;
                m_attackState = AttackState.Idle;
                controller.Parameters.SetBool("IsAttacking", false);
                controller.Parameters.SetFloat("AttackComboParity", m_attackParity);
                return;
            }

            bool pending = m_componentMiner != null && m_componentMiner.IsPending(ComponentMiner.PendingAction.Attack);

            // Activity 层当前是否在播 attack。本方法在 controller.Update 前，读上帧 Update 后状态。
            // AnimationPlayer 过渡期返回 TargetPlayer，故 transition 切入/稳态均命中；排除停用渐降期防误判。
            AnimationLayer activityLayer = null;
            AnimationLayer[] layers = controller.m_layers;
            if (layers != null) {
                foreach (AnimationLayer l in layers) {
                    if (l.Name == "Activity") {
                        activityLayer = l;
                        break;
                    }
                }
            }
            bool activityPlayingAttack = activityLayer != null
                && !activityLayer.m_deactivating
                && (activityLayer.AnimationPlayer?.Animation?.Name == AttackCrossSource
                    || activityLayer.AnimationPlayer?.Animation?.Name == AttackJabSource);

            // 状态机推进
            switch (m_attackState) {
                case AttackState.Idle:
                    if (pending) {
                        m_attackState = AttackState.Winding;
                    }
                    break;
                case AttackState.Winding:
                    // 等 MeleeImpact 事件（HandleAnimationEvent）→ Impact；未 impact 前缓冲新 pending（不重播）。
                    break;
                case AttackState.Impact:
                    if (m_attackComboMode == AttackComboMode.Impact && pending) {
                        // impact 模式：impact 后即可重触发（parity 翻转下一手）；HitInterval 硬底线仍在 miner.Hit 入口。
                        m_attackParity ^= 1;
                        m_attackState = AttackState.Winding;
                    }
                    // complete 模式：等 MeleeAttackComplete 事件 → justCompleted latch → Idle。
                    break;
            }

            // 锁：开播（Activity 见 attack）即锁防 blend 期掉。
            if (activityPlayingAttack) {
                m_attackActive = true;
            }
            else if (m_attackActive) {
                // 兜底解锁：attack 离开 Activity 层但 MeleeAttackComplete 未到（被打断无 onInterrupt / 换模型 controller 重建字段残留）
                // → 重置锁 + 状态机防 IsAttacking 卡死。仿 UpdateAttackedState:520-527 / UpdatePickupState:451-458 兜底。
                // 安全：Winding 蓄力期（pending 已设但 Activity 尚未切入）m_attackActive 仍 false → 此分支不触发，isAttacking 经状态机保持 true 不掉。
                m_attackActive = false;
                m_attackState = AttackState.Idle;
                m_componentMiner?.ClearIsPending(ComponentMiner.PendingAction.Attack);  // 同步清 miner pending 防永久锁（否则入口排他锁卡死后续动作）
            }

            bool isAttacking = m_attackState != AttackState.Idle || m_attackActive;
            controller.Parameters.SetBool("IsAttacking", isAttacking);
            controller.Parameters.SetFloat("AttackComboParity", m_attackParity);
        }

        /// <summary>
        /// Dig 动作（cosmetic，Activity 层）：PokingPhase>0 判挖掘中。目标 CrossBlock（草/花等 X 形植被）→ dig_harvest（Farm_Harvest），
        /// 否则 dig_chop（TreeChopping_Loop）。Dig 不走 pending（连续渐进），vanilla ComponentMiner 自行挖掘，动画纯装饰。
        /// 播完语义（仿 attack/attacked）：dig_harvest/chop loop=false + onComplete(DigLoopComplete)。m_digActive 锁保持播放中 IsDigging=true，
        /// 停止挖掘（PokingPhase 归零）后 anim 继续到 onComplete 才停（不截断）。onComplete→justCompleted→SetBool false 一帧（path 变）→
        /// 仍挖则下帧重选重播，停则停用。植物瞬毁 DigCellFace 仅首帧有效 → m_digPlantCached 缓存，PokingPhase 期沿用。
        /// </summary>
        bool m_digPlantCached;  // dig 目标植物分类缓存：DigCellFace 有效时更新，植物瞬毁仅首帧有效，PokingPhase 期沿用
        bool m_digActive;  // dig 播放锁：挖中（PokingPhase>0）置 true，停挖后保持到 DigLoopComplete（dig_chop 播完）才停。避免停挖瞬断 anim
        bool m_digJustCompleted;  // dig 刚播完（DigLoopComplete 事件）：下帧强制 IsDigging=false 一帧（path 变）重播/停

        void UpdateDigState(AnimationController controller) {
            // dig 刚播完：强制 IsDigging=false 一帧让规则选 null（path 变）。仍挖→下帧重选重播；停→停用（anim 已 onComplete 自然播完）。
            if (m_digJustCompleted) {
                m_digJustCompleted = false;
                m_digActive = false;
                controller.Parameters.SetBool("IsDigging", false);
                if (m_componentMiner == null || m_componentMiner.PokingPhase <= 0f) {
                    m_digPlantCached = false;
                }
                return;
            }

            bool digging = m_componentMiner != null && m_componentMiner.PokingPhase > 0f;

            // place/use/interact/fire/throw 期间压制 dig：dig_chop 与 place_chop/use_chop/interact_*/shoot_pistol 同在 Activity 层（Override 互斥），
            // throw_overhand 在 Base 层与 dig_harvest 同层；抢层时 dig（Activity 的 dig_chop 或 Base 的 dig_harvest）被遮蔽，
            // 跨层抢不触发 onInterrupt → 既无 DigLoopComplete 也无 DigInterrupt 清 m_digActive，锁卡住 → IsDigging 残留致 dig 重播。被压制时直接清 m_digActive。
            // aimPending：投掷物 Aim pending 期（throw_overhand 尚未开播的首帧）也压制，防 OnAim Completed 的 Poke(false) 残留 PokingPhase 触发 dig_chop。
            bool aimPending = m_componentMiner != null && m_componentMiner.IsPending(ComponentMiner.PendingAction.Aim);
            bool digSuppressed = aimPending
                              || controller.Parameters.GetBool("IsPlacing")
                              || controller.Parameters.GetBool("IsUsing")
                              || controller.Parameters.GetBool("IsInteracting")
                              || controller.Parameters.GetBool("IsFiring")
                              || controller.Parameters.GetBool("IsThrowing")
                              || controller.Parameters.GetBool("IsThrowingUpperBody");
            if (digSuppressed) {
                m_digActive = false;
            }
            else if (digging) {
                m_digActive = true;
            }
            // 停挖后不清（保持 dig anim 播完）；DigLoopComplete（HandleAnimationEvent）/ m_digJustCompleted 分支负责清。

            // 分类（DigCellFace 有效时更新；植物瞬毁多帧无效，用缓存）
            if (digging && m_componentMiner.DigCellFace.HasValue && m_subsystemTerrain != null) {
                CellFace c = m_componentMiner.DigCellFace.Value;
                int blockValue = m_subsystemTerrain.Terrain.GetCellValue(c.X, c.Y, c.Z);
                Block block = BlocksManager.Blocks[Terrain.ExtractContents(blockValue)];
                m_digPlantCached = block is CrossBlock;
            }

            // IsDigging：未被 place/use/interact 压制时，挖中（PokingPhase>0）或播放锁中（停挖后 anim 播完前保持）
            bool isDigging = !digSuppressed && (digging || m_digActive);
            controller.Parameters.SetBool("IsDigging", isDigging);
            controller.Parameters.SetBool("IsDiggingPlant", m_digPlantCached);
        }

        /// <summary>
        /// Place 动作（Activity 层）：pending 即时派发。检测 IsPending(Place) 当帧即 ExecutePlace(Pending)（放置+清 pending），
        /// 方块立即生效（1 帧延迟）；IsPlacing=true 触发 place 循环动画。持有 SaplingBlock/SeedsBlock → place_water（Farm_Watering），
        /// 否则 place_chop（TreeChopping_Loop）。循环末（PlaceLoopComplete）：播放中又收到新 place → 续播；否则停。
        /// </summary>
        void UpdatePlaceState(AnimationController controller) {
            if (m_placeJustCompleted) {
                m_placeJustCompleted = false;
                controller.Parameters.SetBool("IsPlacing", false);
                return;
            }
            if (m_componentMiner != null
                && m_componentMiner.IsPending(ComponentMiner.PendingAction.Place)) {
                int blockValue = m_componentMiner.ActiveBlockValue;
                // ExecutePlace(Reraycast) 触发时刻重射线；未命中/DoPlace 失败返 false（pending 已清，下帧不重入）→ 不播 place anim。
                bool placed = m_componentMiner.ExecutePlace(ComponentMiner.TargetMode.Reraycast);
                if (placed) {
                    Block block = blockValue != 0 ? BlocksManager.Blocks[Terrain.ExtractContents(blockValue)] : null;
                    m_placingPlant = block is SaplingBlock || block is SeedsBlock;
                    controller.Parameters.SetBool("IsPlacingPlant", m_placingPlant);
                    controller.Parameters.SetBool("IsPlacing", true);
                }
            }
        }

        /// <summary>
        /// Use 动作（Activity 层）：pending 即时派发（同 UpdatePlaceState）。ExecuteUse(Pending) 当帧生效。
        /// 普通方块统一 use_chop（TreeChopping_Loop）。
        /// </summary>
        void UpdateUseState(AnimationController controller) {
            if (m_useJustCompleted) {
                m_useJustCompleted = false;
                controller.Parameters.SetBool("IsUsing", false);
                return;
            }
            if (m_componentMiner != null
                && m_componentMiner.IsPending(ComponentMiner.PendingAction.Use)) {
                // ExecuteUse(Reraycast) 失败（等级不足/DoUse 失败，pending 已清，下帧不重入）→ 不播 use anim。
                bool used = m_componentMiner.ExecuteUse(ComponentMiner.TargetMode.Reraycast);
                if (used) {
                    controller.Parameters.SetBool("IsUsing", true);
                }
            }
        }

        /// <summary>
        /// Interact 动作（Activity 层）：pending 延迟派发。检测 IsPending(Interact) → 自射线分类目标（箱子?）→ IsInteracting=true 播动画。
        /// m_interactActive 防 pending 期每帧重分类（pending 持续到 impact 才清）。InteractImpact 事件（chest 0.5/general 0.4）调 ExecuteInteract(Pending)。
        /// 目标是 ChestBlock → interact_chest（Chest_Open）；否则 interact_general（Interact speed 2）。ClassifyInteractChest 用 InteractPendingValue 分类。
        /// </summary>
        void UpdateInteractState(AnimationController controller) {
            bool isPending = m_componentMiner != null
                && m_componentMiner.IsPending(ComponentMiner.PendingAction.Interact);
            if (m_interactJustCompleted) {
                m_interactJustCompleted = false;
                controller.Parameters.SetBool("IsInteracting", false);
                return;
            }
            if (isPending
                && !m_interactActive) {
                m_interactChest = ClassifyInteractChest();
                controller.Parameters.SetBool("IsInteractChest", m_interactChest);
                controller.Parameters.SetBool("IsInteracting", true);
                m_interactActive = true;
            }
        }

        /// <summary>
        /// Aim 动作（cosmetic hold，Activity 层）：轮询 ComponentPlayer.m_aim.HasValue + 持有 Bow/Crossbow/Musket。
        /// 瞄准中 → aim_pistol（Pistol_Idle_Loop）循环；松手/换武器即停。开火动画由 UpdateFireState（ProjectileAdded）触发。
        /// </summary>
        void UpdateAimState(AnimationController controller) {
            bool aiming = false;
            if (m_componentPlayer != null && m_componentPlayer.m_aim.HasValue && m_componentMiner != null) {
                int blockValue = m_componentMiner.ActiveBlockValue;
                if (blockValue != 0) {
                    Block block = BlocksManager.Blocks[Terrain.ExtractContents(blockValue)];
                    aiming = block is BowBlock || block is CrossbowBlock || block is MusketBlock;
                }
            }
            controller.Parameters.SetBool("IsAiming", aiming);
        }

        /// <summary>
        /// 开火 latch 消费（Activity fire）：OnProjectileAdded 置 m_projectileFirePending（仅本玩家持弓/弩/火枪）→ IsFiring=true
        /// （Activity shoot_pistol / Pistol_Shoot）。投掷物不走此（Aim pending 驱动 UpdateThrowState）。ShootComplete 事件下帧停。
        /// </summary>
        void UpdateFireState(AnimationController controller) {
            if (m_fireJustCompleted) {
                m_fireJustCompleted = false;
                controller.Parameters.SetBool("IsFiring", false);
                return;
            }
            // 仅弓/弩/火枪（OnProjectileAdded 已过滤）。投掷物不走此（Aim pending 驱动 throw_overhand，见 UpdateThrowState）。
            if (m_projectileFirePending) {
                m_projectileFirePending = false;
                controller.Parameters.SetBool("IsFiring", true);
            }
        }

        /// <summary>
        /// 投掷动作（双路径状态机）：IsPending(Aim)（投掷物松手，API ComponentMiner.Aim 拦截存 pending）→ 首帧按"是否站定地面空闲"选路径：
        /// 站定地面空闲（IsOnGround + SpeedAbs≤0.2 + 非蹲/睡/水/骑/死）→ Base 层全身 throw_overhand（IsThrowing）；
        /// 其余（移动/飞行/蹲/蹲走/游泳/骑乘等）→ Activity 层上半身 throw_overhand（IsThrowingUpperBody，boneMask=spine_01 子树，下半身保留 Base 状态）。
        /// 首帧选定后锁住不互转。
        /// 双路径各持双锁（request 待播 + active 播放锁），仿 UpdatePickupState：
        /// 首帧设 param+request 后 return（规则下帧评估）→ 次帧判该层是否真播 throw → 锁。
        /// 切换语义：Base 锁中 throw 离开 Base（移动 walk 胜出）→ 立即抛（需求：静→移动打断即抛）；
        /// Activity 锁中 throw 离开 Activity（仅高优先级 Activity 规则抢，如 IsFiring/IsAttacking）→ 立即抛（需求：高优先级打断），
        /// 静止不抢 Activity throw（IsThrowingUpperBody 锁不受 SpeedAbs 影响，需求：移→静止继续播到投出）。
        /// 双抛防护：两 param 互斥（首帧选一）+ ExecuteAim 幂等（消费 m_aimPendingRay 置 null，二次调 no-op）。
        /// Poke(false) 清理：OnAim Completed（ExecuteAim 内）副作用设 PokingPhase=0.0001，各结束分支清 0 防 UpdateDigState 误播 dig_chop。
        /// ThrowComplete 事件（onComplete，两路径共用）→ 由 active 锁判哪条完成 → 下帧停。
        /// </summary>
        void UpdateThrowState(AnimationController controller) {
            // Base 路径完成
            if (m_throwJustCompleted) {
                m_throwJustCompleted = false;
                m_throwActive = false;
                ClearThrowPokingPhase();
                controller.Parameters.SetBool("IsThrowing", false);
                return;
            }
            // Activity 路径完成
            if (m_throwUpperJustCompleted) {
                m_throwUpperJustCompleted = false;
                m_throwUpperActive = false;
                ClearThrowPokingPhase();
                controller.Parameters.SetBool("IsThrowingUpperBody", false);
                return;
            }

            bool aimPending = m_componentMiner != null && m_componentMiner.IsPending(ComponentMiner.PendingAction.Aim);
            bool basePlayingThrow = BaseLayerIsPlaying(controller, OverhandThrowSource);
            bool actPlayingThrow = ActivityLayerIsPlaying(controller, OverhandThrowSource);

            // === Base 路径锁中（全身 throw）===
            if (m_throwActive) {
                if (basePlayingThrow) {
                    // 继续全身 throw，等 25% AimImpact event / ThrowComplete。
                    controller.Parameters.SetBool("IsThrowing", true);
                }
                else {
                    // Base throw 断（移动 walk 胜出 / 高优先级打断）→ 立即抛（需求：静→移动打断即抛）。aimPending 已消费则 no-op。
                    m_throwActive = false;
                    if (aimPending) {
                        m_componentMiner.ExecuteAim(ComponentMiner.TargetMode.Pending);
                    }
                    ClearThrowPokingPhase();
                    controller.Parameters.SetBool("IsThrowing", false);
                }
                return;
            }
            // === Activity 路径锁中（上半身 throw）===
            if (m_throwUpperActive) {
                if (actPlayingThrow) {
                    // 继续上半身（移动/静止都继续，需求：移→静止不断）。
                    controller.Parameters.SetBool("IsThrowingUpperBody", true);
                }
                else {
                    // Activity throw 断（仅高优先级 Activity 规则抢，如 IsFiring）→ 立即抛（需求：高优先级打断）。
                    m_throwUpperActive = false;
                    if (aimPending) {
                        m_componentMiner.ExecuteAim(ComponentMiner.TargetMode.Pending);
                    }
                    ClearThrowPokingPhase();
                    controller.Parameters.SetBool("IsThrowingUpperBody", false);
                }
                return;
            }

            // === 未锁 ===
            if (!aimPending) {
                m_throwRequest = false;
                m_throwUpperRequest = false;
                controller.Parameters.SetBool("IsThrowing", false);
                controller.Parameters.SetBool("IsThrowingUpperBody", false);
                return;
            }

            // Base 路径次帧（上帧设 request+IsThrowing，规则已评估）
            if (m_throwRequest) {
                if (basePlayingThrow) {
                    m_throwRequest = false;
                    m_throwActive = true;
                    controller.Parameters.SetBool("IsThrowing", true);
                }
                else {
                    // 静止触发但 Base 没播（异常/首帧未切入）→ 兜底立即抛。
                    m_throwRequest = false;
                    controller.Parameters.SetBool("IsThrowing", false);
                    m_componentMiner.ExecuteAim(ComponentMiner.TargetMode.Pending);
                    ClearThrowPokingPhase();
                }
                return;
            }
            // Activity 路径次帧
            if (m_throwUpperRequest) {
                if (actPlayingThrow) {
                    m_throwUpperRequest = false;
                    m_throwUpperActive = true;
                    controller.Parameters.SetBool("IsThrowingUpperBody", true);
                }
                else {
                    // 移动触发但 Activity 没播（BlendDuration 首帧未切入/规则失配）→ 兜底立即抛。
                    m_throwUpperRequest = false;
                    controller.Parameters.SetBool("IsThrowingUpperBody", false);
                    m_componentMiner.ExecuteAim(ComponentMiner.TargetMode.Pending);
                    ClearThrowPokingPhase();
                }
                return;
            }

            // === 首帧：aimPending 刚到，按"是否站定地面空闲"选路径（选定后锁住不互转）===
            // Base 层 throw 仅"站定地面空闲"时胜出——Base 规则顺序里这些优先于 IsThrowing 会盖过：
            //   dead / climbup / land / ride / wakeup / sleep(LieDownFactor) / water / !onGround(jump) / crouch / run / walk。
            // 任一命中则 Base 不播 throw，改走 Activity 上半身（boneMask=spine_01 子树不冲突 Base 下半身状态）。
            // 故 groundedIdle 须排除上述全部；漏任一则误判→走 Base→Base 不播 throw→兜底立即抛（抛射仍发生，仅丢投掷动画）。
            // 用 m_jumpPhase==JumpPhaseGround 替 IsOnGround：Ground 是唯一站定空闲相位（排除 Land/Start/Loop/ClimbUp）。
            // 注意飞行/水/梯子/骑乘/死亡时 UpdateJumpPhase overridden 会 reset m_jumpPhase=Ground，故仍需 !IsFlying/!IsInWater/!IsRiding/!IsDead 显式排除。
            // 维护：Base 层规则演变（加新优先于 throw 的状态）时须同步此判据。
            bool groundedIdle = m_jumpPhase == JumpPhaseGround
                             && controller.Parameters.GetFloat("SpeedAbs") <= 0.2f
                             && controller.Parameters.GetFloat("CrouchFactor") <= 0f
                             && controller.Parameters.GetFloat("LieDownFactor") <= 0f
                             && !controller.Parameters.GetBool("IsInWater")
                             && !controller.Parameters.GetBool("IsRiding")
                             && !controller.Parameters.GetBool("IsDead")
                             && !controller.Parameters.GetBool("IsFlying")
                             && !controller.Parameters.GetBool("IsWakingUp");
            if (groundedIdle) {
                // 站定地面 → Base 全身路径
                m_throwRequest = true;
                controller.Parameters.SetBool("IsThrowing", true);
            }
            else {
                // 移动/飞行/蹲/蹲走/游泳/骑乘等 → Activity 上半身路径
                m_throwUpperRequest = true;
                controller.Parameters.SetBool("IsThrowingUpperBody", true);
            }
        }

        /// <summary>清 OnAim Completed 副作用 Poke(false) 设的 PokingPhase 残留，防投掷后 UpdateDigState 误播 dig_chop。</summary>
        void ClearThrowPokingPhase() {
            // 投掷时持雪球/炸弹等不挖（无 DigCellFace），PokingPhase 仅由 Poke(false) 启动；直接清 0 不影响真挖（真挖不进 throw 路径）。
            // 打断跨帧时 ComponentMiner.Update 已 tick 涨过 0.001，故不限阈值全清（>0 即清）。
            if (m_componentMiner != null && m_componentMiner.PokingPhase > 0f) {
                m_componentMiner.PokingPhase = 0f;
            }
        }

        /// <summary>
        /// Base 层（Index==0）当前是否播指定 source（查 Animation.Name）。层回退/双锁判定共用，替内联遍历。
        /// </summary>
        static bool BaseLayerIsPlaying(AnimationController controller, string sourceName) {
            AnimationLayer[] layers = controller.m_layers;
            if (layers != null) {
                foreach (AnimationLayer l in layers) {
                    if (l.Index == 0) {
                        return l.AnimationPlayer?.Animation?.Name == sourceName;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Activity 层（上半身，boneMask=spine_01 子树）当前是否播指定 source。查 Animation.Name，排除停用渐降期 m_deactivating。
        /// 双路径投掷判定移动→Activity throw 用。仿 UpdateAttackState 的 Name=="Activity" 判定。
        /// </summary>
        static bool ActivityLayerIsPlaying(AnimationController controller, string sourceName) {
            AnimationLayer[] layers = controller.m_layers;
            if (layers != null) {
                foreach (AnimationLayer l in layers) {
                    if (l.Name == "Activity") {
                        return !l.m_deactivating && l.AnimationPlayer?.Animation?.Name == sourceName;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Interact 目标是否箱子：用公开 InteractPendingValue 取按下交互键时存的目标方块值（地形/移动方块通用）。
        /// </summary>
        bool ClassifyInteractChest() {
            if (m_componentMiner == null) {
                return false;
            }
            // 用 pending 存的真实目标（按下交互键时准星射线命中），非自眼位射线——
            // 自定义模型 EyeRotation 可能与相机视角不同步导致自射线 miss。
            int blockValue = m_componentMiner.InteractPendingValue;
            if (blockValue == 0) {
                return false;
            }
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(blockValue)];
            return block is ChestBlock;
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
                case "MeleeImpact": {
                    // Data: "targetMode|comboMode"（如 "reraycast|complete"）；缺省 reraycast|complete。
                    // targetMode 固定 Reraycast（impact 时刻重射线取当前目标）；comboMode 解析 impact/complete。
                    // ApplyAnimationEvents（AnimationController.cs）把 alias events 的 Data 直传本事件 Parameter。
                    string data = animationEvent.Parameter as string;
                    var mode = ComponentMiner.TargetMode.Reraycast;
                    AttackComboMode combo = AttackComboMode.Complete;
                    if (!string.IsNullOrEmpty(data)) {
                        string[] parts = data.Split('|');
                        if (parts.Length > 0 && parts[0].Equals("reraycast", StringComparison.OrdinalIgnoreCase)) {
                            mode = ComponentMiner.TargetMode.Reraycast;
                        }
                        if (parts.Length > 1 && parts[1].Equals("impact", StringComparison.OrdinalIgnoreCase)) {
                            combo = AttackComboMode.Impact;
                        }
                    }
                    m_attackComboMode = combo;
                    m_attackState = AttackState.Impact;
                    m_componentMiner?.ExecuteHit(mode);
                    break;
                }
                case "MeleeAttackComplete": {
                    // 攻击动画完成：翻转 parity（下次另一手）+ 标记刚完成（下帧强制 IsAttacking=false 交还/重播）。
                    int __oldParity = m_attackParity;
                    m_attackParity ^= 1;
                    m_attackActive = false;
                    m_attackJustCompleted = true;
                    break;
                }
                case "DigLoopComplete": {
                    // dig 单次播完（loop=false）：标记完成，下帧强制 IsDigging=false（path 变）重播或停。
                    m_digActive = false;
                    m_digJustCompleted = true;
                    break;
                }
                case "DigInterrupt": {
                    // dig 被抢中断（place/use/attacked 等切走 dig_chop/dig_harvest，未自然播完无 DigLoopComplete）：
                    // 清 m_digActive 防 IsDigging 卡 true（否则 place 后 dig_chop 规则匹配重播第二遍）。挖中下帧重设。
                    m_digActive = false;
                    break;
                }
                case "PlaceLoopComplete": {
                    // place 播完（onComplete）或被打断（onInterrupt）均走此：标记完成，下帧强制 IsPlacing=false 停。
                    // onInterrupt 设计差异：place/use/interact 是 latch 触发（非持续条件），onInterrupt 复用 *Complete 走 justCompleted
                    // 才能主动清 IsXxx param 离开 anim；dig/pickup 用单独 *Interrupt trigger（仅清锁），因靠持续条件下帧自然评估。
                    m_placeJustCompleted = true;
                    break;
                }
                case "UseLoopComplete": {
                    // use 播完（onComplete）或被打断（onInterrupt）均走此（同 PlaceLoopComplete）。
                    m_useJustCompleted = true;
                    break;
                }
                case "InteractImpact": {
                    // Data: "targetMode"（pending/reraycast）；缺省 reraycast。impact 时刻重射线取当前目标（与 MeleeImpact 一致），
                    // data 显式 "pending" 当前不生效（targetMode 固定 Reraycast），保留解析仅为对称/未来扩展。
                    var mode = ComponentMiner.TargetMode.Reraycast;
                    string data = animationEvent.Parameter as string;
                    if (!string.IsNullOrEmpty(data)
                        && data.Split('|')[0].Equals("reraycast", StringComparison.OrdinalIgnoreCase)) {
                        mode = ComponentMiner.TargetMode.Reraycast;
                    }
                    m_componentMiner?.ExecuteInteract(mode);
                    break;
                }
                case "InteractComplete": {
                    // interact 播完或被打断均走此：清锁 + 分类标志（m_interactChest/IsInteractChest 防残留），标记完成下帧强制 IsInteracting=false。
                    m_interactActive = false;
                    m_interactChest = false;
                    controller.Parameters.SetBool("IsInteractChest", false);
                    m_interactJustCompleted = true;
                    break;
                }
                case "ShootComplete": {
                    m_fireJustCompleted = true;
                    break;
                }
                case "ThrowComplete": {
                    // 两路径共用 throw_overhand alias（onComplete=ThrowComplete）。由 active 锁判哪条完成：
                    if (m_throwUpperActive) m_throwUpperJustCompleted = true;
                    else m_throwJustCompleted = true;
                    break;
                }
                case "AimImpact": {
                    // throw_overhand 播到 25%（events）或被打断（onInterrupt）均走此：ExecuteAim 抛射（Pending=松手存的 ray）。
                    // 防双抛：ExecuteAim 消费 m_aimPendingRay 置 null，25% event 抛后若再 onInterrupt → null no-op。
                    m_componentMiner?.ExecuteAim(ComponentMiner.TargetMode.Pending);
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
            // 停用条件：死亡/躺下/攀爬时 head 不追踪。ClimbUp 期 head 与 bodyturn 两层皆停（JSON bodyturn condition 已排 ClimbUp），
            // head 另外排死亡/躺下；攀爬时 head 放松回动画姿态。
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
            float minPitch = MathUtils.DegToRad(MinHeadPitchDegrees);
            float yaw = MathUtils.Clamp(
                lookAngles.X * (InvertHeadYaw ? -1f : 1f), -maxYaw, maxYaw);
            // pitch 先钳到玩家意图 [MinHeadPitchDegrees, MaxHeadPitchDegrees]（非对称），再减 HeadPitchCorrection
            // 补偿模型 face-forward 与 AimAxis(+Z) 的 ~45° 偏移（水平视线时 head 实际仰，向下压平）。
            float pitch = MathUtils.Clamp(
                lookAngles.Y * (InvertHeadPitch ? -1f : 1f), minPitch, maxPitch)
                - MathUtils.DegToRad(HeadPitchCorrectionDegrees);

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
