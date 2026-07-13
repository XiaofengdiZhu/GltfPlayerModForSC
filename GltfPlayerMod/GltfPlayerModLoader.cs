using System;
using System.Collections.Generic;
using Engine;
using Engine.Animation;
using Engine.Graphics;
using Game.Animation.Drivers;

namespace Game {
    public class GltfPlayerModLoader:ModLoader {
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
        // 必须在 MeasureOverride hook 内每帧驱动：
        // PlayerModelWidget.MeasureOverride 每帧 RemoveModel+AddModel 重建 bone buffer（同 Model 实例），
        // ModelWidget.RemoveModel 已解耦（不删 player 条目）→ player 跨帧存活，时间连续。
        // 但 ModelWidget.Update（Update 阶段）采样进 buffer 后，Draw 阶段的 Measure 会把 buffer 换成空，
        // 故关掉 AnimationEnabled（避免双重 Update(dt) 推进时间 + 无效采样），由本 hook 在 buffer 重建后
        //（Draw 的 ProcessBoneHierarchy 之前）独占驱动 + 采样。
        public override void OnPlayerModelWidgetMeasureOverride(PlayerModelWidget playerModelWidget) {
            Model playerModel = playerModelWidget.PlayerModel;
            if (playerModel == null) {
                return;
            }
            ModelWidget modelWidget = playerModelWidget.m_modelWidget;
            // 本 mod 全局接管所有 PlayerModelWidget 的动画：强制 Idle 预览，关掉 ModelWidget 自动驱动。
            // 副作用：若未来有特性想用标准 Update 路径驱动 PlayerModelWidget 动画，会被本 mod 静默禁用。
            // 原版 PlayerModelWidget 无 controller/player，关掉无影响（SetBoneTransform 手动摆姿态不受波及）。
            modelWidget.AnimationEnabled = false;

            AnimationPlayer player = modelWidget.GetAnimationPlayer(playerModel);
            if (player == null || player.Animation?.Name != IdleAnimationName) {
                player = modelWidget.PlayAnimation(playerModel, IdleAnimationName, loop: true);
                if (player == null) {
                    return; // 该模型无 Idle_Loop 动画（非 gltf 玩家模型等），跳过
                }
            }

            Matrix?[] boneTransforms = modelWidget.m_boneTransforms[playerModel];
            for (int i = 0; i < boneTransforms.Length; i++) {
                boneTransforms[i] = null;
            }
            player.Update(Time.FrameDuration);
            player.SampleBoneTransforms(boneTransforms);
            player.SamplePointerTargets(playerModel);
            player.SampleMorphWeights(playerModel);
        }
    }
}
