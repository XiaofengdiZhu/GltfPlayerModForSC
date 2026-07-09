using System;
using System.Collections.Generic;
using Engine.Animation;
using Game.Animation.Drivers;

namespace Game {
    public class GltfPlayerModLoader:ModLoader {
        public override void __ModInitialize() {
            ModsManager.RegisterHook("OnLoadingFinished", this);
            ModsManager.RegisterHook("OnWidgetContentsLoaded", this);

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
    }
}