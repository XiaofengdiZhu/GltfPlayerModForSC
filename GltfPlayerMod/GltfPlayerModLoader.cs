using System;
using System.Collections.Generic;
using Engine;
using Engine.Animation;
using Engine.Graphics;
using Game.Animation.Drivers;

namespace Game {
    public class GltfPlayerModLoader: ModLoader {
        const string IdleAnimationName = "Idle_Loop";

        public override void __ModInitialize() {
            ModsManager.RegisterHook("OnLoadingFinished", this);
            ModsManager.RegisterHook("OnWidgetContentsLoaded", this);
            ModsManager.RegisterHook("OnPlayerModelWidgetMeasureOverride", this);

            // 注册 glTF 玩家躯干转向 + 头部追踪 driver（Additive，JSON bodyturn 层引用 source:"driver:GltfBodyTurn"）
            AnimationDriverManager.Register<GltfBodyTurnDriver>("GltfBodyTurn");
        }

        public override void OnLoadingFinished(List<Action> actions) {
            CharacterSkinsManager.AddEmptySkin = true;
            CharacterSkinsManager.UseEmptySkinAsDefault = true;
        }

        public override void OnWidgetContentsLoaded(Widget widget) {
            if (widget is PlayerModelWidget playerModelWidget) {
                playerModelWidget.AnimateHeadSeed = 0;
                playerModelWidget.AnimateHandsSeed = 0;
            }
        }

        // 让 PlayerModelWidget 中的 gltf PlayerModel 持续循环播放内置 Idle_Loop 动画。
        // 仅在此 hook 内建一次 player；驱动（Update/Sample）由 ModelWidget.Update 负责
        // （AnimationEnabled 默认 true）。PlayerModelWidget.MeasureOverride 已不再每帧
        // RemoveModel+AddModel（仅 PlayerClass 变即 dirty 时 swap），故 bone buffer 跨帧存活，
        // Update 阶段采样不被 Draw 阶段冲掉。
        // PlayerClass 变 → dirty → swap（RemoveModel 清旧 player）→ 本 hook 见新模型无 player → 建新 player。
        public override void OnPlayerModelWidgetMeasureOverride(PlayerModelWidget playerModelWidget) {
            Model playerModel = playerModelWidget.PlayerModel;
            if (playerModel == null) {
                return;
            }
            ModelWidget modelWidget = playerModelWidget.m_modelWidget;
            if (modelWidget.GetAnimationPlayer(playerModel) == null) {
                modelWidget.PlayAnimation(playerModel, IdleAnimationName, loop: true); // 无 Idle_Loop 则 no-op
            }
        }
    }
}
