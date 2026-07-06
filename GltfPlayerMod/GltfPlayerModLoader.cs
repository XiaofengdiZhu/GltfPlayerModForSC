using System;
using System.Collections.Generic;

namespace Game {
    public class GltfPlayerModLoader:ModLoader {
        public override void __ModInitialize() {
            ModsManager.RegisterHook("OnLoadingFinished", this);
            ModsManager.RegisterHook("OnWidgetContentsLoaded", this);
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