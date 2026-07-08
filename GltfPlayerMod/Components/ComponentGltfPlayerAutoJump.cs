using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    /// <summary>
    /// glTF 玩家自动跳跃组件：检测到 AutoJump 越障（前方 1 米高可碰撞方块）时，
    /// 改用攀爬动画（ClimbUp_1m_RM，root motion Override 接管位移）替代原版物理跳跃。
    /// 继承 ComponentAutoJump 复用越障检测逻辑，仅在触发瞬间撤销 JumpOrder 并标记攀爬待处理，
    /// 由 ComponentGltfPlayerBaseController 消费标志进入 ClimbUp 相位。
    /// </summary>
    /// <remarks>
    /// 时序依据：AutoJump 更新顺序为 Default（0），ComponentLocomotion 为 Locomotion（1）。
    /// 故 base.Update 后 JumpOrder 尚未被 Locomotion 消费，可据其增量判定本帧是否触发越障。
    /// 触发则撤销 JumpOrder（禁物理跳）让 root motion 接管，并置攀爬标志供 BaseController 同帧消费。
    /// 第一人称回退：第一人称下 ComponentModel.Animate 不跑（IsVisibleForCamera=false），root motion 链失效，
    /// 故检测到第一人称时直接走基类原版物理跳（不撤销 JumpOrder、不置攀爬标志）。
    /// </remarks>
    public class ComponentGltfPlayerAutoJump : ComponentAutoJump {
        // 本帧是否检测到 AutoJump 越障触发（供 BaseController 消费进入 ClimbUp 相位）
        bool m_climbUpPending;

        // glTF 玩家（可空：仅 Player 实体挂载）；判第一人称视角用其 GameWidget
        ComponentPlayer m_componentPlayer;

        /// <summary>
        /// 消费并返回是否有待处理的攀爬触发；调用即清除标志。
        /// </summary>
        public bool ConsumeClimbUpPending() {
            if (m_climbUpPending) {
                m_climbUpPending = false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 加载：缓存 glTF 玩家（判第一人称视角用）。
        /// </summary>
        public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap) {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>();
        }

        /// <summary>
        /// 复用基类越障检测；若本帧 AutoJump 设置了 JumpOrder，撤销物理跳并标记攀爬。
        /// 第一人称下 root motion 失效，直接走基类原版物理跳（JumpOrder 生效，不撤销/不攀爬）。
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
            float before = m_componentCreature.ComponentLocomotion.JumpOrder;
            base.Update(dt);
            float after = m_componentCreature.ComponentLocomotion.JumpOrder;
            if (after > before && after > 0f) {
                m_componentCreature.ComponentLocomotion.JumpOrder = 0f;
                m_climbUpPending = true;
            }
        }
    }
}
