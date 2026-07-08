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
    /// </remarks>
    public class ComponentGltfPlayerAutoJump : ComponentAutoJump {
        // 本帧是否检测到 AutoJump 越障触发（供 BaseController 消费进入 ClimbUp 相位）
        bool m_climbUpPending;

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
        /// 复用基类越障检测；若本帧 AutoJump 设置了 JumpOrder，撤销物理跳并标记攀爬。
        /// </summary>
        public override void Update(float dt) {
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
