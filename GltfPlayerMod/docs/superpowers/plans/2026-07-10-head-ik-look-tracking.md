# glTF 玩家 Head IK 视线追踪 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** glTF 玩家头部用 IK 追踪视线方向（水平 yaw + 俯仰 pitch，上下左右都跟摄像机），替代当前 `GltfBodyTurnDriver` 的 head pitch；身体转向（pelvis yaw）保留在 driver。

**Architecture:** head 从 bodyturn driver 抽出，交给 `SingleBoneIK`。driver 瘦身为只转 pelvis 朝移动方向。`ComponentGltfPlayerBaseController`（ComponentAnimationParticipant）在 `OnControllerCreated` 注册 head IK 链（`[neck, head]`），每帧 `SyncAnimationParameters` 调 `SetIKAim` 设 head 目标方向（`LookAngles.X/Y` 合成的模型空间向量）。IK 在动画层混合**之后**求解（`AnimationController.ComputeBoneTransforms:1223`），自动看到 driver 已转的 pelvis 并补偿——head 始终朝视线方向，与身体转多少无关，绕过颈弯曲致 head 局部坐标系旋转的单轴抵消难题。死亡/躺睡/攀爬时 `ClearIKTarget` 放松头部。

**Tech Stack:** C# mod（.NET），`Engine.Animation` IK 系统（`SingleBoneIK` / `IKUtils.RotationBetweenVectors` / `ApplyModelRotation`），`ComponentAnimationParticipant` 钩子。仅改 mod，SurvivalcraftApi（IK API）不动。

---

## 背景与已确认事实

- **IK 在层混合后跑**：`AnimationController.Update`（规则评估 + 层 Update）→ `ComponentModel.Animate` 调 `ComputeBoneTransforms`（层混合 + driver Apply + `m_ikSolver.Solve` at `:1223`）。driver（bodyturn 层，Additive）改 pelvis 在前，IK Solve 在后看到结果。
- **SyncAnimationParameters 时序**：`ComponentModel.Animate:182-183` → `SyncAnimationParameters()`（自身 virtual）→ `SyncParticipants()`（转发参与者，调 `SyncAnimationParameters(controller)`）→ `controller.Update` → `ComputeBoneTransforms`（IK Solve）。**参与者设的 aim 当帧 IK Solve 用**。文档 `AnimationAdvancedTopics.md:343` 明确推荐此钩子设 IK 目标。
- **OnControllerCreated 注册 IK**：文档 `AnimationAdvancedTopics.md:315-333`——每次 controller 创建/重建触发，内部去重幂等，换模型自动重注册。BaseController 已有此钩子。
- **LookAngles 来源**：`ComponentLocomotion.LookAngles`（Vector2，鼠标累积，弧度）。`.X`=水平视线 yaw（相对 body forward），`.Y`=俯仰 pitch。`ComponentHumanModel:271-272` 已写 `LookAngleX/LookAngleY` 参数（但本计划直接读 `ComponentLocomotion.LookAngles`，不经参数）。`.Y` 绕 right 轴=`ComponentLocomotion:380` 验证为 pitch。
- **骨骼层级**：`root→pelvis→spine_01→spine_02→spine_03→neck→head`（`boneAliases` 含 `"Head":"head"`）。head 父=neck。IK 链 `end="head" maxChainLength=2` → `[neck, head]`，SingleBoneIK 转 root（neck）让 head（end）朝目标。
- **SingleBoneIK 行为**：`currentAimDir = TransformNormal(chain.AimAxis, head.endWorldTransform)`（head 当前模型空间脸朝向，含 pelvis 转 + 颈弯曲）；`modelRotation = RotationBetweenVectors(currentAimDir, targetDir)`；`ApplyModelRotation(neck, modelRotation)` 保持 neck 世界位置不变。targetDir 是**模型空间**方向。head 目标 = body 初始 forward（+Z）绕 up 转 `LookAngleX` + pitch，是相对身体初始朝向的视线 → body 转了（pelvis 追移动方向）head 自动补偿转回视线方向。
- **构建命令**：`dotnet build GltfPlayerMod.csproj -c Debug`（工作目录 `D:\Projects\GltfSupportForSC\GltfPlayerModForSC\GltfPlayerMod`）。mod 引用已编译的 SurvivalcraftApi dll，改 mod 只构建 mod。

## File Structure

| 文件 | 职责 | 改动 |
|------|------|------|
| `Components/GltfBodyTurnDriver.cs` | 全身转向 driver（Additive） | **瘦身**：删 head pitch 全部逻辑，只留 pelvis yaw 转向。`TargetBones` → `[pelvis]` |
| `Components/ComponentGltfPlayerBaseController.cs` | 玩家动画参与者 | **加** head IK：`OnControllerCreated` 注册链 + `SyncAnimationParameters` 加 `UpdateHeadIK`（每帧 `SetIKAim`/`ClearIKTarget`） |
| `Assets/Animations/GltfPlayer.json` | 动画配置 | bodyturn driverArgs 删 `InvertHeadPitch`（driver 不再用 head pitch） |

SurvivalcraftApi（IK 系统、`ComponentLocomotion.LookAngles`、`AnimationController` IK API）**不改**——API 现成。

---

## Task 1: GltfBodyTurnDriver 瘦身（删 head，只留 pelvis）

**Files:**
- Modify: `Components/GltfBodyTurnDriver.cs`（整文件重写）

head 追踪交给 IK 后，driver 不再碰 head。删所有 head 相关：`HeadBone`/`HeadYawAxis`/`PitchAxis`/`HeadPitchScale`/`InvertHeadPitch`/`LookAngleYParam`/`m_lookAngleY` + `SampleTransforms` 的 head pitch 块 + `Update` 读 `LookAngleY`。`TargetBones` 只剩 `BodyRootBone`。

- [ ] **Step 1: 整文件重写 GltfBodyTurnDriver.cs**

替换 `Components/GltfBodyTurnDriver.cs` 全文为：

```csharp
using Engine;
using Engine.Animation;
using Engine.Graphics;

namespace Game.Animation.Drivers {
    /// <summary>
    /// glTF 玩家全身转向驱动器（Additive，叠加在 Base 层 walk/idle 动画之上）。
    /// </summary>
    /// <remarks>
    /// 复刻原版 dae 玩家行为：全身（躯干+四肢含双腿）转向移动方向。
    /// BodyRoot（pelvis，腿+躯干共同根）绕 YawAxis 转 HeadingOffset，带动躯干+双臂+双腿整体朝行走方向。
    /// 侧移时全身转约 90° 朝侧面（朝行走方向走），而非锁在摄像机方向。
    /// head 视线追踪不在此 driver（head 局部坐标系经颈弯曲旋转，单轴抵消 headingOffset 非纯 yaw），
    /// 改由 ComponentGltfPlayerBaseController 注册的 SingleBoneIK 链在层混合后求解（自动补偿 pelvis 转）。
    /// 参数 HeadingOffset 由 ComponentHumanModel.SyncAnimationParameters 写入 controller（玩家实体共用）。
    /// Additive 混合（AnimationBlender: existing * incoming）→ 全身在 walk/idle 基础上叠加转向，腿/臂摆动动画保留。
    /// 只写 pelvis，其余骨骼该层无值 → blender 跳过；pelvis 子树（spine 链/腿/臂）经骨骼层级继承旋转。
    /// 旋转轴经 property 可配（JSON driverArgs）：glTF 骨骼局部坐标系因模型/烘焙旋转而异，YawAxis 默认 Y，
    /// 若表现为侧翻（绕水平轴）说明该骨骼局部 Y 非垂直，改 YawAxis 为 "X" 或 "Z" 试。
    /// 后退不转（|HeadingOffset| > MaxHeadingAngle 视为后退，保持面向前方）。
    /// 死亡/躺下由 JSON bodyturn 规则停用本层（condition 排除 IsDead/LieDownFactor），避免乱转。
    /// </remarks>
    public class GltfBodyTurnDriver : IAnimationDriver {
        public string Name => "GltfBodyTurn";
        public AnimationBlendMode BlendMode => AnimationBlendMode.Additive;

        // 目标骨骼（骨骼名经 property 配置后失效缓存重建，参考 LookAtDriver）
        public string[] TargetBones => m_cachedTargetBones ??= [BodyRootBone];
        string[] m_cachedTargetBones;

        /// <summary>身体根骨骼名（转向目标，腿+躯干共同根，带动全身含四肢）</summary>
        public string BodyRootBone {
            get;
            set {
                field = value;
                m_cachedTargetBones = null;
            }
        } = "pelvis";

        /// <summary>转向（yaw）参数名</summary>
        public string HeadingOffsetParam { get; set; } = "HeadingOffset";

        /// <summary>转向（yaw）轴："X"/"Y"/"Z"，默认 Y。若侧翻改 X 或 Z</summary>
        public string YawAxis { get; set; } = "Y";

        /// <summary>转向幅度比例（1.0=原版全量，纯侧移约 90°；调小可减弱）</summary>
        public float HeadingScale { get; set; } = 1.0f;

        /// <summary>后退阈值（弧度）：|headingOffset| 超过此值视为后退，不转身体（保持面向前方）。π=180°纯后退，π/2≈90°侧移</summary>
        public float MaxHeadingAngle { get; set; } = 2.4f;

        /// <summary>转向方向反转（左右转向反了改 true）</summary>
        public bool InvertYaw { get; set; } = false;

        float m_headingOffset;

        public void Update(float deltaTime, AnimationParameters parameters) {
            m_headingOffset = parameters.GetFloat(HeadingOffsetParam);
        }

        public void SampleTransforms(Matrix?[] boneTransforms, Model model) {
            // 后退（|headingOffset|>MaxHeadingAngle）不转身体，保持面向前方
            bool backward = MathUtils.Abs(m_headingOffset) > MaxHeadingAngle;
            float heading = backward ? 0f : m_headingOffset * HeadingScale;
            if (heading == 0f) {
                return;
            }
            float yawAngle = InvertYaw ? -heading : heading;

            // 全身转向移动方向：BodyRoot（pelvis）绕 YawAxis 转 headingOffset，带动躯干+四肢
            ModelBone rootBone = model.FindBone(BodyRootBone, false);
            if (rootBone != null) {
                boneTransforms[rootBone.Index] = CreateRotationForAxis(YawAxis, yawAngle);
            }
        }

        Matrix CreateRotationForAxis(string axis, float angle) {
            return axis?.ToUpperInvariant() switch {
                "X" => Matrix.CreateRotationX(angle),
                "Y" => Matrix.CreateRotationY(angle),
                "Z" => Matrix.CreateRotationZ(angle),
                _ => Matrix.CreateRotationY(angle)
            };
        }
    }
}
```

- [ ] **Step 2: 构建 mod 验证编译**

Run（工作目录 `D:\Projects\GltfSupportForSC\GltfPlayerModForSC\GltfPlayerMod`）：
```
dotnet build GltfPlayerMod.csproj -c Debug
```
Expected: 0 错误 0 警告。若报 `HeadBone`/`InvertHeadPitch` 等未找到 → JSON/其他文件仍引用已删 property，检查 Task 3 是否提前需做（JSON driverArgs 引用 `InvertHeadPitch` 不影响编译——JSON 是数据，但确认无 C# 引用残留）。

- [ ] **Step 3: Commit**

```
git add Components/GltfBodyTurnDriver.cs
git commit -m "refactor(driver): 瘦身 GltfBodyTurnDriver 删除 head pitch，只留 pelvis yaw 转向

head 视线追踪改由 IK 接管（SingleBoneIK 链），driver 不再处理 head。
删除 HeadBone/HeadYawAxis/PitchAxis/HeadPitchScale/InvertHeadPitch/LookAngleYParam
及相关字段与 SampleTransforms head pitch 块。TargetBones 缩为 [pelvis]。
保留 pelvis 绕 YawAxis 转 HeadingOffset + 后退不转（MaxHeadingAngle）。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 2: JSON bodyturn driverArgs 删 InvertHeadPitch

**Files:**
- Modify: `Assets/Animations/GltfPlayer.json:330-345`（bodyturn state）

driver 不再用 head pitch，JSON `driverArgs` 的 `InvertHeadPitch` 失效（数据无害但误导），删除。`YawAxis:"Z"` 保留（身体转向轴）。

- [ ] **Step 1: 改 bodyturn state 的 driverArgs**

将 `Assets/Animations/GltfPlayer.json` 中（约 :333-339）：

```json
          "animation": {
            "source": "driver:GltfBodyTurn",
            "driverArgs": { "YawAxis": "Z", "InvertHeadPitch": true }
          }
```

改为：

```json
          "animation": {
            "source": "driver:GltfBodyTurn",
            "driverArgs": { "YawAxis": "Z" }
          }
```

- [ ] **Step 2: 构建 mod 验证 JSON 仍被资源管线打包**

```
dotnet build GltfPlayerMod.csproj -c Debug
```
Expected: 0 错误 0 警告（JSON 是嵌入资源，构建打包无报错）。

- [ ] **Step 3: Commit**

```
git add Assets/Animations/GltfPlayer.json
git commit -m "chore(json): bodyturn driverArgs 删除失效的 InvertHeadPitch

head pitch 改由 IK 接管，driver 不再读此 property。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 3: BaseController 加 head IK 链注册 + UpdateHeadIK

**Files:**
- Modify: `Components/ComponentGltfPlayerBaseController.cs`（加字段/常量 + OnControllerCreated 注册 + SyncAnimationParameters 调 UpdateHeadIK + 新方法 RegisterHeadIK/UpdateHeadIK）

`OnControllerCreated` 注册 IK 链（幂等，换模型自动重注册）。`SyncAnimationParameters` 末尾兜底重注册（OnControllerCreated 时 model 偶未就绪则补注册）+ 调 `UpdateHeadIK` 每帧设 aim。

- [ ] **Step 1: 加 using System（MathF.Sin/Cos）**

文件顶部 using 区（`using Engine;` 前）加：
```csharp
using System;
```

- [ ] **Step 2: 加 head IK 常量与字段（类字段区，`m_attackedJustCompleted` 字段后，约 :82）**

在 `private bool m_attackedJustCompleted;` 后加：

```csharp
        // ===== head IK 视线追踪 =====
        // head IK 链名（OnControllerCreated 注册，SyncAnimationParameters 每帧 SetIKAim）
        private const string HeadIKChain = "Head";

        // head IK 链是否注册成功（OnControllerCreated 首注册 + Sync 兜底补注册）
        private bool m_headIKRegistered;

        // head 局部"脸朝前"轴（经颈弯曲，模型相关）。默认 +Z（game forward）。
        // 手测调：head 转错方向时改 (1,0,0)/(-1,0,0)/(0,0,-1) 等（见 Task 4）。
        private Vector3 m_headAimAxis = new Vector3(0f, 0f, 1f);

        // 模型空间 forward 符号：视线方向向量 z 分量符号。默认 +1（forward=+Z），实际 forward=-Z 改 -1。
        private const int HeadForwardSign = 1;

        // yaw（水平）/pitch（垂直）方向反转（转向反了改 true）
        private const bool InvertHeadYaw = false;
        private const bool InvertHeadPitch = false;

        // 最大转头角度（度，运行时转弧度钳制，防脖子转过头）
        private const float MaxHeadYawDegrees = 70f;    // 水平左右各 70°
        private const float MaxHeadPitchDegrees = 50f;  // 上下各 50°
```

- [ ] **Step 3: OnControllerCreated 加 IK 链注册（约 :121-129）**

将：
```csharp
        public override void OnControllerCreated(AnimationController controller) {
            controller.Parameters.SetString("JumpPhase", m_jumpPhase);
            controller.Parameters.SetBool("IsWakingUp", m_isWakingUp);
            controller.Parameters.SetBool("IsShivering", false);
            controller.Parameters.SetBool("IsTired", false);
            controller.Parameters.SetBool("IsHoldingMeleeWeapon", false);
            controller.Parameters.SetBool("IsPickingUp", false);
            controller.Parameters.SetBool("IsAttacked", false);
        }
```
改为：
```csharp
        public override void OnControllerCreated(AnimationController controller) {
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
```

- [ ] **Step 4: SyncAnimationParameters 加兜底注册 + UpdateHeadIK（约 :134-142）**

将：
```csharp
        public override void SyncAnimationParameters(AnimationController controller) {
            UpdateJumpPhase(controller);
            UpdateWakeUpState(controller);
            UpdateVitalState(controller);
            UpdateMeleeWeaponState(controller);
            UpdatePickupState(controller);
            UpdateAttackedState(controller);
            UpdateMoveSpeed(controller);
        }
```
改为：
```csharp
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
```

- [ ] **Step 5: 加 RegisterHeadIK + UpdateHeadIK 方法（类末尾，HandleAnimationEvent 方法后，约 :492 类闭合括号前）**

在 `HandleAnimationEvent` 方法的闭合 `}` 后、类的闭合 `}` 前加：

```csharp
        /// <summary>
        /// 注册 head IK 链（[neck, head]，SingleBoneIK，aim 模式）。幂等：已注册跳过。
        /// </summary>
        /// <remarks>
        /// 文档 AnimationAdvancedTopics.md:315-333：OnControllerCreated 注册（每次 controller 创建/重建触发，去重幂等）。
        /// head 是 neck 子，maxChainLength=2 → 链 [neck, head]，SingleBoneIK 转 root（neck）让 end（head）朝目标。
        /// AimAxis 是 head 局部"脸朝前"轴（m_headAimAxis），经 endWorldTransform 变换得当前模型空间脸朝向。
        /// RegisterAndBuildIKChain 在 model 未就绪时返回 null（m_headIKRegistered 保持 false，Sync 兜底重试）。
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
            // 停用条件：死亡/躺睡/攀爬时 head 不追踪（与 bodyturn 层停用条件对齐）
            bool active = m_componentCreature.ComponentHealth.Health > 0f
                && (m_componentSleep == null || !m_componentSleep.IsSleeping)
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
        }
```

- [ ] **Step 6: 构建 mod 验证编译**

```
dotnet build GltfPlayerMod.csproj -c Debug
```
Expected: 0 错误 0 警告。常见错：
- `IKChain`/`IKChain.AimAxis` 未找到 → 确认 `using Engine.Animation;`（文件已有，:2）。
- `ComponentLocomotion.LookAngles` 不可见 → 确认 `m_componentCreature.ComponentLocomotion` 非空（:195 已用，可见）。
- `MathUtils.DegToRad`/`MathUtils.Clamp` 未找到 → 确认 `using Engine;`（:1，MathUtils 在 Engine 命名空间）。

- [ ] **Step 7: Commit**

```
git add Components/ComponentGltfPlayerBaseController.cs
git commit -m "feat(player): head IK 视线追踪（水平 yaw + 俯仰 pitch 跟摄像机）

OnControllerCreated 注册 SingleBoneIK 链 [neck, head]，SyncAnimationParameters
每帧 SetIKAim 设模型空间视线方向（LookAngles.X/Y 合成）。IK 在层混合后求解，
自动补偿 driver 已转的 pelvis，head 始终朝视线方向，绕过颈弯曲致单轴抵消失效。
死亡/躺睡/攀爬 ClearIKTarget 放松头部。yaw/pitch 钳制防过度转头。

AimAxis/forward 符号/反转 默认值待 Task 4 实测调参。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 4: 实测调参（AimAxis / forward 符号 / 反转 / 角度限制）

**Files:**
- Modify: `Components/ComponentGltfPlayerBaseController.cs`（仅改 Task 3 加的常量/字段默认值）

mod 动画行为靠手测调参（无单测）。AimAxis（head 局部脸朝前轴）和视线方向符号取决于具体模型骨骼朝向，无法静态确定，必须运行时观测调。在 headless GPU 测试环境（参考 memory `technical_headless-testing`）或实际游戏运行观测。

- [ ] **Step 1: 构建当前状态进游戏观测**

```
dotnet build GltfPlayerMod.csproj -c Debug
```
部署到测试环境运行。第三人称操控玩家。

- [ ] **Step 2: 观测 head 朝向，按表调参**

操控玩家鼠标左右看（yaw）+ 上下看（pitch），观察 head 朝向。对照下表改 `m_headAimAxis` / `HeadForwardSign` / `InvertHeadYaw` / `InvertHeadPitch`（`Components/ComponentGltfPlayerBaseController.cs` Task 3 Step 2 所加）：

| 现象 | 含义 | 调整 |
|------|------|------|
| head 完全不动 | AimAxis 错（currentAimDir 已等于 targetDir，或轴使旋转退化） | 改 `m_headAimAxis` 试 `(1,0,0)` → `(-1,0,0)` → `(0,1,0)` → `(0,-1,0)` → `(0,0,-1)` |
| 左右看正确，上下反 | pitch 符号反 | `InvertHeadPitch = true` |
| 上下看正确，左右反 | yaw 符号反 | `InvertHeadYaw = true` |
| 上下反 + 左右反 | 两者都反 | `InvertHeadYaw = true; InvertHeadPitch = true` |
| head 朝后（180° 错） | forward 符号反 | `HeadForwardSign = -1` |
| head 侧倒（roll）/ 脸朝向不对 | AimAxis 与 forward 约定不匹配 | `m_headAimAxis` 与 `HeadForwardSign` 须一致约定：AimAxis 是 head 局部脸朝前轴，HeadForwardSign 是模型空间 forward（z）符号。两者同改保持一致 |

每次改 1 个参数，重新构建观测。目标：左右看 → head 左右转跟视线；上下看 → head 上下俯仰跟视线；身体侧走（pelvis 转）→ head 仍朝视线方向（不跟身体转）。

- [ ] **Step 3: 调角度限制（可选）**

若 head 转得过猛（脖子转断）或过软：调 `MaxHeadYawDegrees`（默认 70）/ `MaxHeadPitchDegrees`（默认 50）。原版 dae head 上下约 ±45°、水平约 ±80°（参考 `ComponentHumanModel` fallback `:336-342` 钳制 80°/45°）。

- [ ] **Step 4: 构建 + 确认调参稳定**

```
dotnet build GltfPlayerMod.csproj -c Debug
```
反复观测至 head 左右/上下都跟摄像机视线、身体侧走 head 不跟转。记录最终参数值。

- [ ] **Step 5: Commit（调参结果）**

```
git add Components/ComponentGltfPlayerBaseController.cs
git commit -m "fix(player): 调 head IK AimAxis/forward 符号/反转参数

实测确定 [填最终值]：m_headAimAxis=(x,y,z), HeadForwardSign=±1,
InvertHeadYaw=true/false, InvertHeadPitch=true/false。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

（commit message 里 `[填最终值]` 处替换为 Step 4 实测确定的实际数值。）

---

## Task 5: 全量回归验证

**Files:** 无改动（验证 task）

确认 head IK 不破坏既有动画，且与身体转向协同正确。

- [ ] **Step 1: 构建**

```
dotnet build GltfPlayerMod.csproj -c Debug
```
Expected: 0 错误 0 警告。

- [ ] **Step 2: head 视线追踪场景验证**

第三人称操控玩家，逐项确认：
- **站立静止左右看**：head 水平转跟鼠标 yaw，身体不转（pelvis HeadingOffset≈0）。
- **站立静止上下看**：head 俯仰跟鼠标 pitch。
- **前进走/跑**：身体朝前（pelvis 不转），head 朝视线（直视前方时头正）。
- **侧走（纯左右移动）**：身体（含双腿+躯干+双臂）转向移动方向（~90°），**head 仍朝视线方向**（不跟身体转侧面）——本计划核心目标。
- **斜走**：身体部分转向移动方向，head 朝视线。
- **后退**：身体不转（MaxHeadingAngle），head 朝视线。
- **死亡**：head 放松（ClearIKTarget），随死亡动画。
- **躺下睡觉**：head 放松。
- **攀爬（AutoJump 越障 ClimbUp）**：head 放松，随攀爬动画。

- [ ] **Step 3: 既有动画回归（IK/driver 改动不破坏 Base/Activity/UpperBody/Ride 层）**

逐项确认 walk/run/idle/crouch/swim/fly/jump(Start/Loop/Land)/climbup/pickup/attacked/melee_idle/sleep/wakeup/ride 正常播放（IK 仅作用于 head/neck，driver 仅 pelvis，不影响其他骨骼）：
- walk/run 不冻结（MoveSpeed 修复仍在）。
- attacked 后倾、pickup 拾取、melee 持剑 idle 正常。
- 攀爬 root motion 正常（head IK 停用不影响 ClimbUp）。

- [ ] **Step 4: 第一人称切回第三人称边界**

第一人称不渲染自己（不跑 Animate/IK）。切回第三人称：head IK 恢复正常追踪（`OnControllerCreated` 重建 controller 时重注册 IK 链）。确认无残留乱转。

- [ ] **Step 5: 无回归则完成，无需 commit**

若 Step 2-4 全过，功能完成。若发现问题，回到对应 Task 修。

---

## 风险与对策

| 风险 | 等级 | 对策 |
|------|------|------|
| **AimAxis 无法静态确定，需实测** | 中 | Task 4 专设调参步骤 + 参数对照表。glTF 玩家模型已转 game Z-forward（root bone 处理），默认 +Z 大概率接近，微调即可 |
| **head IK 与 driver/walk 动画 head 旋转叠加** | 低 | IK 在层混合后求解（覆盖式），driver 已删 head 部分。walk/idle 的 head 动画（颈部摆动）在 IK 前，IK 在其基础上 aim 修正——颈部小幅摆动 + aim 朝视线，符合自然 |
| **OnControllerCreated 时 model 未就绪致 IK 链注册失败** | 低 | `RegisterHeadIK` 幂等 + SyncAnimationParameters 每帧兜底补注册（m_headIKRegistered 标志） |
| **脖子转不过大角度视线（LookAngleX 大时）** | 低 | yaw/pitch 钳制 MaxHeadYaw/Pitch。超出范围 head 转到极限停住（原版同此） |
| **第一人称 IK 链残留** | 低 | 第一人称不跑 Animate（不 SetIKAim），controller 重建时 OnControllerCreated 重注册。切第三人称自愈 |
| **head 链 root=neck 转动带 head，但 neck 旋转可能影响颈以下** | 低 | SingleBoneIK 转 neck（head 父），neck 子树只含 head，不影响 spine/躯干。ApplyModelRotation 保持 neck 世界位置不变 |

## 关键文件

- `Components/GltfBodyTurnDriver.cs`（瘦身：删 head，只 pelvis yaw）
- `Components/ComponentGltfPlayerBaseController.cs`（加 head IK：注册 + 每帧 aim）
- `Assets/Animations/GltfPlayer.json`（bodyturn driverArgs 删 InvertHeadPitch）
- 参考（不改）：`SurvivalcraftApi/Engine/Engine.Animation/IK/`（SingleBoneIK/IKUtils/IKChain）、`AdvancedGltfFoxModForSC/.../ComponentFoxStareBehavior.cs`（IK 用法范例）
