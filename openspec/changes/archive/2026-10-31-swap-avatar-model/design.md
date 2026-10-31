# Design

## Decisions

### D1 署名从 VRM meta 派生，而不是写死常量

`loaded.meta.title` / `loaded.meta.author` 在 VRM 0.0 里是标准字段，两副模型都填了；页脚由 `MonikaAvatar` 通过 `onCredit` 回调上报，`App` 渲染。写死常量的方案在换模型时必然说谎，而"署名 required"是这套许可下的硬义务，说谎不是排版问题。VRM 1.0 用 `meta.name` / `meta.authors`，取值时两种情况都兜住；都取不到时回落到"该模型未声明作者，仅限非商业使用"。

### D2 两副模型都入库，默认切到新的

再分发许可允许把 VRM 放进公开仓库，所以 `Monkia.vrm` 直接入库；`Monika.vrm` 保留，用 `VITE_VRM_URL=/assets/monika/Monika.vrm` 一键回退。代价是克隆体积从 23 MB 变成 40 MB，收益是回退不需要翻历史或重新下载。如果之后确定不再回退，删掉旧文件即可（历史里仍在）。

### D3 `VITE_VRM_URL` 是唯一的换模开关

模型文件名只出现在 `.gitignore` 白名单、素材清单和文档里，代码读 `import.meta.env.VITE_VRM_URL`，为空时回落到默认常量。这样换模不用改代码、不用重新生成映射、也不会影响已发布的构建（Vite 在构建期内联该变量）。

### D4 表情形变必须只有一个写入者

VRM 的 `blendShapeGroups` 可以带 bind（`{index, mesh, weight}`）。带 binds 时 three-vrm 的 `VRMExpressionManager.update()` 会把自己的权重（默认 0）写回同一批 morph target，把 `FaceDriver` 的直写覆盖掉——实测新模型 joy 权重 0.8 在约 1.05 s 后归零，旧模型因为 binds 全空所以从未触发。管理器没有开关，只能 `unregisterExpression` 全部注销；binds 留在材质上，但没人再写。注销逻辑抽成纯函数 `detachExpressions()` 便于单测。

### D5 坐姿按脚掌厚度补偿膝盖，不整体缩放模型

`applySitPose` / `placeSeated` 把 hips 对齐座面，但座面高度是场景量出来的（`classroom.ts` 的 `HIP_ABOVE_SEAT`），和小腿长度没有关系：腿长的模型脚会陷进地板。

最初的想法是"整体升降模型"，那会让她浮在座面上；按身高整体缩放又会连带改变手的位置、命中盒与相机取景。最后选了第三条：**hips 不动，只把差量折成小腿的额外弯曲**——膝盖多弯一点把脚抬回地面，视觉上就是"坐得更收"，几何上是唯一不动其它部位的解。

标定姿态本身其实也是埋着的：实测两副模型的脚踝都落在地面附近，而站立时的脚踝高度是 0.100 m（Monkia）与 0.061 m（Monika），也就是脚底比地板低 6–10 cm，只是课桌挡住看不见。因此目标高度不能写死常量，必须从模型自身量：

- **脚掌厚度 = 站立（绑定姿态）脚踝高度 − 站立包围盒最低点**。不能直接用脚踝世界高度：不同模型的原点未必在地面（Monika 的网格最低点在原点下方 2.2 cm，脚踝高度 0.061；Monkia 的最低点正好在原点，脚踝 0.100 —— 差值是鞋底厚度），差值才是与原点无关的模型属性。
- 求解用割线/牛顿（`apps/web/src/avatar/seatFit.ts` 的 `solveKneeDelta`）：先在 ±0.12 rad 两个采样估局部斜率，再迭代到 2 mm 以内，不假设骨骼的旋转正负方向；没有垂直杠杆（骨骼缺失）时返回 0，不写坏姿态。
- 结果写进 `stats().seatFit = { clearanceM, kneeDelta }`，冒烟断言的正是它（见 tasks 3.5）。

## Risks / Trade-offs

- **许可**：两副模型都是 `personal_commercial_use: disallow` + `credit: necessary`。页脚必须始终可见且写对作者；本项目整体非商用。
- **仓库体积**：40 MB 的二进制史。若改用 Git LFS 需要重写历史，暂不做。
- **未知来源模型**：`inspect-vrm.mjs` 只能验证结构与命名，验证不了授权；入库前必须像这次一样拿到许可页。
- **表情管理器注销的副作用**：如果将来要用 three-vrm 内置表情（例如对着别人的 VRM 做眨眼），需要先 `registerExpression` 回来；当前管线不依赖它。

## Verification

- `node tools/inspect-vrm.mjs` 对新模型输出 `verdict: ready`，且 57 个 target 与 `face.map.json` 顺序一致（已实测）。
- `pnpm -r test` 含 `vrmExpressions` 的 4 条单测；`tsc --noEmit` 干净。
- `node apps/web/e2e/smoke.mjs` 对新模型 8/8（含"表情形变被驱动"权重 0.68）；用 `VITE_VRM_URL` 切回旧模型同样 8/8。
- `node apps/web/e2e/vrm-probe.mjs` 采样 1.2 s 内 joy 权重不归零。
- 页脚文案在两种模型下分别显示各自作者（DOM 断言，见 tasks 3.3）。
