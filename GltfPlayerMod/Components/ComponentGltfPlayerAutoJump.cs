using Engine;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    /// <summary>
    /// glTF 玩家自动跳跃组件：地面行走中沿移动意图方向约 ClimbTriggerDistance 处有 1 米高可碰撞障碍时，
    /// 改用攀爬动画（ClimbUp_1m_RM，root motion Override 接管位移）替代原版物理跳跃。
    /// 继承 ComponentAutoJump 复用其可爬判定，但用前瞻距离触发（替代基类的贴脸碰撞触发），
    /// 仅在触发瞬间撤销 JumpOrder 并标记攀爬待处理（含爬向），由 ComponentGltfPlayerBaseController 消费标志进入 ClimbUp 相位。
    /// </summary>
    /// <remarks>
    /// 多向支持（前/斜前/纯侧，排除后退）：climb clip 唯一且根运动只向前，但 TranslationApplier 用 body.Rotation 重定向
    /// 根运动（localVel → world）。故检测到任意可爬方向时，由 BaseController 在进 ClimbUp 时把 body.Rotation 转朝该方向，
    /// 同一前向 clip 即物理移向障碍（无需方向性动画）。
    ///
    /// 检测方向 = 玩家移动意图（body-local WalkOrder → 世界）：X(strafe) 沿 body.Right、Y(forward) 沿 body.Forward 合成水平向量。
    /// - 排除后退：intent 与 forward 夹角 &gt; 135°（正后 90° 锥，Dot &lt; BackwardExcludeCos）不爬，走原版物理跳。
    /// - 无输入（松键）intent≈0 → 不爬。
    /// DDA 沿 intent 射线遍历穿过的格（内层循环任意方向通用，对角线/纯侧皆可），reach 内找第一个可爬障碍。
    ///
    /// 前瞻触发：基类依赖 CollisionVelocityChange（已撞障碍、贴脸）才触发，但 ClimbUp_1m_RM 动画设计为
    /// 前方约 0.8m 起步攀爬——贴脸起步会使 root motion 推模型穿模且结束位置偏远。
    /// 故本类 TryClimb 用 DDA 沿 intent 射线遍历穿过的格，reach（= ClimbTriggerDistance）内
    /// 找第一个可爬障碍（可碰撞、上方不可碰撞、非 NoAutoJump）即触发；命中则撤销 JumpOrder + 置攀爬标志
    /// + 记爬向 + 设 m_lastAutoJumpTime（门控基类同帧贴脸判定）。
    ///
    /// 时序依据：AutoJump 更新顺序为 Default（0），ComponentLocomotion 为 Locomotion（1）。
    /// 故 base.Update 后 JumpOrder 尚未被 Locomotion 消费，可据其增量判定本帧是否触发越障。
    /// 第一人称回退：第一人称下 ComponentModel.Animate 不跑（IsVisibleForCamera=false），root motion 链失效，
    /// 故检测到第一人称时直接走基类原版物理跳（不撤销 JumpOrder、不置攀爬标志）。
    /// </remarks>
    public class ComponentGltfPlayerAutoJump : ComponentAutoJump {
        // 前瞻触发距离（米）：climb 动画 ClimbUp_1m_RM 设计为前方约此距离起步攀爬 1m 障碍。
        // 基类贴脸触发（CollisionVelocityChange）从 0m 起步 → 穿模 + 结束偏远；前瞻修正起步位置。
        const float ClimbTriggerDistance = 0.8f;

        // 最小爬升高度差（米）：DDA 命中的障碍格顶面需高出玩家脚至少此值才视为可爬障碍。
        // 防 climb 结束帧（玩家陷障碍顶 ~0.03m 未弹起）前瞻把前方平地格误判为可爬 → 重触发爬空中。
        const float MinClimbHeightDiff = 0.9f;

        // 后退排除阈值（Dot(intent, forward)）：cos(135°)≈-0.707。intent 与 forward 夹角 > 135°（正后 90° 锥）不爬。
        // 手测可调（-0.5=120° 收紧后退排除，-0.707=135° 当前）。
        const float BackwardExcludeCos = -0.707f;

        // 本帧是否检测到攀爬触发（前瞻命中 或 基类贴脸兜底），供 BaseController 消费进入 ClimbUp 相位
        bool m_climbUpPending;

        // 本帧触发的爬向（世界水平单位向量，与 m_climbUpPending 同寿命；BaseController 消费时读取）
        Vector3 m_climbDirection;

        // glTF 玩家（可空：仅 Player 实体挂载）；判第一人称视角用其 GameWidget
        ComponentPlayer m_componentPlayer;

        // Base 层相位机（可空）；读 IsClimbing 抑制 climb 进行中的重复触发
        ComponentGltfPlayerBaseController m_baseController;

        /// <summary>
        /// 消费并返回是否有待处理的攀爬触发；调用即清除标志，并通过 climbDir 输出本次爬向（世界水平单位向量）。
        /// </summary>
        public bool ConsumeClimbUp(out Vector3 climbDir) {
            if (m_climbUpPending) {
                m_climbUpPending = false;
                climbDir = m_climbDirection;
                return true;
            }
            climbDir = default;
            return false;
        }

        /// <summary>
        /// 加载：缓存 glTF 玩家（判第一人称视角用）。
        /// </summary>
        public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap) {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>();
            m_baseController = Entity.FindComponent<ComponentGltfPlayerBaseController>();
        }

        /// <summary>
        /// 前瞻检测触发攀爬；未命中则 base.Update 兜底（原版贴脸 AutoJump）。
        /// 第一人称下 root motion 失效，直接走基类原版物理跳（不撤销/不攀爬）。
        /// </summary>
        public override void Update(float dt) {
            // 第一人称回退原版物理跳（Animate 不跑 → root motion 链失效）
            if (m_componentPlayer != null
                && m_componentPlayer.PlayerData != null
                && m_componentPlayer.GameWidget != null
                && m_componentPlayer.GameWidget.IsEntityFirstPersonTarget(Entity)) {
                base.Update(dt);
                return;
            }

            // climb 动画进行中抑制重复触发：root motion 推 Position 进障碍格会让前瞻/兜底
            // 误判同一障碍可爬 → 爬空中。JumpPhase=ClimbUp 期间跳过全部检测。
            if (m_baseController != null && m_baseController.IsClimbing) {
                // climb 进行中跳过 base.Update，须手动重置碰撞标志（基类每帧 Update 末尾重置）保持契约
                m_collidedWithBody = false;
                return;
            }

            bool onGround = m_componentCreature.ComponentBody.StandingOnValue.HasValue;

            // 前瞻命中：地面 + intent 方向 + 距障碍 < ClimbTriggerDistance → 提前触发攀爬（替代基类贴脸触发）
            if (onGround
                && m_subsystemTime.GameTime - m_lastAutoJumpTime > 0.3
                && TryClimb(out Vector3 dir, distanceGated: true)) {
                m_componentCreature.ComponentLocomotion.JumpOrder = 0f;
                m_climbUpPending = true;
                m_climbDirection = dir;
                m_lastAutoJumpTime = m_subsystemTime.GameTime;
            }

            // base.Update：前瞻未命中时原版贴脸 AutoJump 兜底；前瞻命中时被 0.3s 门控跳过（仅重置 collidedWithBody）
            float before = m_componentCreature.ComponentLocomotion.JumpOrder;
            base.Update(dt);
            float after = m_componentCreature.ComponentLocomotion.JumpOrder;

            // 兜底撤销：base 贴脸触发时（前瞻漏判），仅地面 + intent 方向可爬才撤销改攀爬
            if (after > before && after > 0f) {
                if (onGround && TryClimb(out Vector3 dir2, distanceGated: false)) {
                    m_componentCreature.ComponentLocomotion.JumpOrder = 0f;
                    m_climbUpPending = true;
                    m_climbDirection = dir2;
                }
            }
        }

        /// <summary>
        /// 移动意图方向可爬障碍检测：DDA 沿 intent（body-local WalkOrder → 世界水平）射线遍历穿过的格，
        /// reach 内找第一个可爬障碍。支持前/斜前/纯侧（排除后退）。
        /// </summary>
        /// <param name="climbDir">命中时输出爬向（世界水平单位向量 = intent）；未命中 default。</param>
        /// <param name="distanceGated">true 要求障碍在 ClimbTriggerDistance 内（前瞻）；false 贴脸也接受（兜底撤销判定）。</param>
        /// <returns>true 表示 intent 方向有 1m 可爬障碍。</returns>
        bool TryClimb(out Vector3 climbDir, bool distanceGated) {
            climbDir = default;
            if (!(SettingsManager.AutoJump || m_alwaysEnabled)) return false;
            if (m_componentCreature.ComponentBody.CrouchFactor != 0f) return false;

            // 移动意图（body-local WalkOrder → 世界水平）：X=strafe(右) 沿 Right，Y=forward(前) 沿 Forward
            Vector2? lastWalkOrder = m_componentCreature.ComponentLocomotion.LastWalkOrder;
            if (!lastWalkOrder.HasValue) return false;
            ComponentBody body = m_componentCreature.ComponentBody;
            Vector3 rightF = Flatten(body.Matrix.Right);
            Vector3 fwdF = Flatten(body.Matrix.Forward);
            if (rightF == Vector3.Zero || fwdF == Vector3.Zero) return false;
            Vector3 sum = rightF * lastWalkOrder.Value.X + fwdF * lastWalkOrder.Value.Y;
            if (sum.LengthSquared() < 1e-6f) return false;          // 无输入（松键）→ 走物理跳
            Vector3 intent = Vector3.Normalize(sum);
            // 排除后退：intent 与 forward 夹角 > 135°（正后 90° 锥）不爬
            if (Vector3.Dot(intent, fwdF) < BackwardExcludeCos) return false;

            Vector3 pos = body.Position;
            int posX = Terrain.ToCell(pos.X);
            int posZ = Terrain.ToCell(pos.Z);
            int cellY = Terrain.ToCell(pos.Y);
            var terrain = m_subsystemTerrain.Terrain;

            // DDA 沿 intent 射线遍历穿过的格，reach 内找第一个可爬障碍。
            // 内层循环任意方向通用（stepX/stepZ ±1/0 + 出面 t + 擦角防护）：前/斜前/纯侧皆可。
            float reach = distanceGated ? ClimbTriggerDistance : 1.0f;
            int stepX = 0, stepZ = 0;
            if (intent.X > 1e-3f) stepX = 1; else if (intent.X < -1e-3f) stepX = -1;
            if (intent.Z > 1e-3f) stepZ = 1; else if (intent.Z < -1e-3f) stepZ = -1;

            int curX = posX, curZ = posZ;
            float t = 0f;
            bool climbable = false;
            for (int i = 0; i <= 4; i++) {
                if (!(curX == posX && curZ == posZ)) {
                    int cv = terrain.GetCellValue(curX, cellY, curZ);
                    int cvu = terrain.GetCellValue(curX, cellY + 1, curZ);
                    int cvuu = terrain.GetCellValue(curX, cellY + 2, curZ);
                    Block blk = BlocksManager.Blocks[Terrain.ExtractContents(cv)];
                    Block blku = BlocksManager.Blocks[Terrain.ExtractContents(cvu)];
                    Block blkuu = BlocksManager.Blocks[Terrain.ExtractContents(cvuu)];
                    // 可爬障碍：障碍格可碰撞 + 上方 2 格（cellY+1 站脚、cellY+2 头顶）均不可碰撞。
                    // 仅检 cellY+1 会让玩家爬进 1 格净空的凹位卡住（玩家站立需 2 格净空）。
                    bool c = !blk.NoAutoJump
                        && blk.GetIsCollidable(body, cv)
                        && !blku.GetIsCollidable(body, cvu)
                        && !blkuu.GetIsCollidable(body, cvuu);
                    if (c) { climbable = true; break; }
                }
                if (i == 4) break;
                // 当前格 intent 出面 t（朝 step 方向的面）
                float tx = float.MaxValue, tz = float.MaxValue;
                if (stepX > 0) tx = (curX + 1 - pos.X) / intent.X;
                else if (stepX < 0) tx = (curX - pos.X) / intent.X;
                if (stepZ > 0) tz = (curZ + 1 - pos.Z) / intent.Z;
                else if (stepZ < 0) tz = (curZ - pos.Z) / intent.Z;
                float nextT = MathUtils.Min(tx, tz);
                if (nextT <= t) nextT = t + 1e-3f;   // 防射线擦角卡在同一格
                if (nextT > reach) break;            // 超出 reach 不再扫
                t = nextT;
                if (tx <= tz) curX += stepX;         // stepX=0 时 tx=Max，不会走此支
                else curZ += stepZ;
            }

            // 高度差门控：障碍格顶面（cellY+1）距玩家脚 < MinClimbHeightDiff 时不爬。
            // 爬 1m 障碍 diff≈1；climb 结束陷障碍顶 diff≈0.03；下坡/平地 diff≤0 → 均不爬。
            if (climbable && (cellY + 1) - pos.Y < MinClimbHeightDiff) return false;
            if (!climbable) return false;
            climbDir = intent;
            return true;
        }

        // 向量投影到水平面（XZ）并归一化；零向量返回零。
        static Vector3 Flatten(Vector3 v) {
            Vector3 f = new(v.X, 0f, v.Z);
            if (f != Vector3.Zero) f = Vector3.Normalize(f);
            return f;
        }
    }
}
