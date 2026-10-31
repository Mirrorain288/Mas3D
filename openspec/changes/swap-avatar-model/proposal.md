# Proposal

## Why

要把 3D 教室里的角色换掉。新模型 `素材/3705117291637205523.vrm` 体检结论是 ready：VRM 0.0、`title: Monkia`、`author: LilowLeeLani`、54 根 humanoid 骨骼齐全、57 个面部 morph 与现模型**同名同序**（`Fcl_MTH_A`=39、`Fcl_ALL_Joy`=3 都在原位），因此不需要重生成 `face.map.json`。许可取自 hub.vroid.com 的 Other 条款：再分发 allow、修改 allow、**署名 required**、企业商用 disallow、个人商用 disallow、性表现 disallow —— 与现模型同一档，可以入库。

但现有实现把模型文件名、页脚署名、素材清单三处写死成 `Monika.vrm` / `tyler1234506`：直接换文件会让界面署名说谎，而署名是这套许可下的强制义务。换模型还当场暴露了一个运行期缺陷：**带真实 blendshape binds 的 VRM 会被 three-vrm 的表情管理器按帧清零**（实测 joy 权重 0.8 保持约 1.05 s 后归零，旧模型 binds 全空所以从没触发），表现为表情和口型偶发失灵。

## What Changes

- **默认角色模型改为 `Monkia.vrm`**（17,143,268 字节），`Monika.vrm` 继续入库作为回退；两者都在 `.gitignore` 白名单里。
- **换模只需一个环境变量**：`VITE_VRM_URL`（构建期或开发期设置），代码里不再出现具体模型文件名。
- **页脚署名从 VRM meta 派生**（`title` / `author`），换任何模型都不会说错作者；meta 缺失时回落到通用文案。
- **表情只有一个写入者**：加载后注销 three-vrm 表情管理器里的全部表达式（`detachExpressions`），`FaceDriver` 独占 `Fcl_*` 形变，修掉 binds 模型的表情归零。
- 同步素材清单、许可段与换模文档（README、tools/README、assets.manifest.json）。
- 不做：运行时热切换模型（要重建 VRM 绑定）、按身高自动缩放坐姿（仍按 `SIT_POSE` 标定，新模型脚低约 2 cm）。

## Capabilities

### Modified Capabilities

- `avatar/monika-runtime`：装载哪副 VRM 变成部署配置（默认 `Monkia.vrm`）；署名义务从"写死作者"改为"跟着实际加载的模型走"；新增"表情形变只有一个写入者"的约束。

## Impact

- 代码：`apps/web/src/avatar/MonikaAvatar.tsx`、`apps/web/src/avatar/vrmExpressions.ts`（新）、`apps/web/src/App.tsx`、`apps/web/src/scene/Scene.tsx`
- 素材与配置：`apps/web/public/assets/monika/Monkia.vrm`（新入库）、`.gitignore`、`tools/assets.manifest.json`、`tools/README.md`、`README.md`
- 测试与工具：`apps/web/src/avatar/__tests__/vrmExpressions.test.ts`（新）、`apps/web/e2e/smoke.mjs`（换模参数化）、`apps/web/e2e/vrm-probe.mjs`（新）、`tools/inspect-vrm.mjs`（新）
