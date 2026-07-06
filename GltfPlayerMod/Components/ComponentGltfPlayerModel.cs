using System;
using Engine.Animation;
using Engine.Graphics;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    /// <summary>
    /// glTF 玩家模型组件。
    /// 继承 ComponentHumanModel，复用基类的模型加载、参数同步、Animate 短路逻辑。
    /// 第一阶段：模型动画完全由 AnimationController（Human.json 配置）驱动，
    /// 此类仅留作后续阶段扩展（上半身动画、手动穿插动画等）。
    /// </summary>
    /// <remarks>
    /// 工作原理：
    /// - 基类 ComponentHumanModel.Animate() 检测到 AnimationController != null 时直接 return，
    ///   跳过旧的 6 骨骼过程式动画（AnimateCreatureFallback）。
    /// - 基类 ComponentHumanModel.SyncAnimationParameters() 每帧同步 IsDead/IsInWater/IsOnGround/
    ///   VelocityXZ/WalkSpeed 等参数，Human.json 的状态规则据此选择 glb 内置动画。
    /// - 基类 ComponentHumanModel.SetModel() 用 FindBone("Body"/"Head"/"Hand1"/...)，
    ///   因 Human.json 配了 boneAliases，会解析到 glb 真实骨骼，蹲下等基类逻辑不会 NRE。
    /// </remarks>
    public class ComponentGltfPlayerModel : ComponentHumanModel {
        // 第一阶段无需任何代码。基类已处理一切。
        // AnimateCreature 是 abstract，基类 ComponentHumanModel 已 override 了它（检测控制器后 return），
        // 所以此处不必再 override。

        // TODO（后续阶段）：override SyncAnimationParameters / Animate 接入攻击、挖掘、瞄准等上半身动画。
    }
}
