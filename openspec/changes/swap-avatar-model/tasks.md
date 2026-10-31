# Tasks

## 1. 素材与许可

- [x] 1.1 复制 `素材/3705117291637205523.vrm` 到 `apps/web/public/assets/monika/Monkia.vrm`（17,143,268 字节，sha256 `0a43adbe…e67`），验证 `git status` 不再忽略它
- [x] 1.2 `.gitignore` 的 `monika/` 白名单同时放行 `Monika.vrm` 与 `Monkia.vrm`，验证 `git check-ignore -v` 对 `Monkia.vrm` 只命中否定模式、对已跟踪的 `Monika.vrm` 无输出，且 `git status` 报未跟踪
- [x] 1.3 `tools/assets.manifest.json` 增加 `Monkia.vrm` 条目（source/target/bytes/sha256/tracked），验证 `pnpm assets:check` 退出码 0
- [x] 1.4 README 与 `tools/README.md` 的许可段写入新模型作者 `LilowLeeLani`、条款摘要与"必须署名"，验证两处作者名与 `VRM.meta.author` 一致

## 2. 代码接线

- [x] 2.1 `MonikaAvatar.tsx` 默认模型改为 `/assets/monika/Monkia.vrm`，仍由 `VITE_VRM_URL` 覆盖，验证构建产物内联该路径
- [x] 2.2 页脚署名改为运行时派生：`MonikaAvatar` 读 `meta.title`/`meta.author`（VRM 1.0 兜 `meta.name`/`meta.authors`），经 `onCredit` 上报 `App`
- [x] 2.3 `detachExpressions()` 在构造 `FaceDriver` 之前调用，验证 `vrmExpressions.test.ts` 4 例通过，`vrm-probe` 采样 1.2 s 内 `Fcl_ALL_Joy` 0.8 与 `Fcl_MTH_A` 1.0 都不被清零
- [x] 2.4 `smoke.mjs` 支持 `VITE_VRM_URL` 参数化并保留诊断字段，`vrm-probe.mjs` 默认指向新模型
- [x] 2.5 落座按模型脚掌厚度补偿：`seatFit.ts` 的 `solveKneeDelta` + 站立包围盒量脚掌厚度，结果写进 `stats().seatFit`，验证 `seatFit.test.ts` 7 例通过

## 3. 验证

- [x] 3.1 `node tools/inspect-vrm.mjs` 两种模型均 ready；`tsc --noEmit` 退出码 0
- [x] 3.2 `pnpm -r test` 全过（protocol 20 / motion 9 / web 104）
- [x] 3.3 冒烟：默认（新模型）9/9 且页脚含 `LilowLeeLani`；`VITE_VRM_URL` 切回旧模型 9/9 且页脚含 `tyler1234506`
- [x] 3.4 `pnpm build` 成功；`openspec validate --all` 通过
- [x] 3.5 冒烟 10/10（两副模型）：脚踝误差 0.3 mm（Monkia，补偿 -0.774 rad）与 1.5 mm（Monika，补偿 -0.576 rad），截图 `.superpowers/shots/seatfit-{monkia,monika}.png` 记录鞋底落在桌下地面上