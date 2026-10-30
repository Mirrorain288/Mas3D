# tools/

本目录只放**构建期脚本**，不参与前端打包。运行环境：Node ≥ 22（见根 `package.json` 的 `engines`）。

## 资产管线

```bash
pnpm assets        # 从 素材/ 派生到 apps/web/public/assets/，并把 bytes/sha256 回写清单
pnpm assets:check  # 只校验：每个 target 是否存在、字节数与 sha256 是否与清单一致
```

- `素材/` 是本地原始素材（**不入库**，见根 `.gitignore`）。
- `apps/web/public/assets/` 是派生结果（**不入库**，唯一例外是 `monika/Monika.vrm`，它已获再分发许可，见下）。
- `assets.manifest.json` 是唯一事实来源：每条 `{ source, target, kind, transform }`，生成后追加 `bytes`/`sha256`。
- `--check` 不写任何文件，不符合时打印 `missing: <target>` / `size: …` / `sha256: …` 并以退出码 1 结束，CI 与验收都跑它。

### transform 取值

| transform | 行为 |
| --- | --- |
| `copy` | 原样复制（GLB / VRM） |
| `resize:<px>` | `sharp` 等比缩到宽 `<px>`，输出 JPEG q85（mozjpeg） |
| `webp:<px>` | `sharp` 等比缩到宽 `<px>`，输出 WebP q82（PBR 贴图） |

### 清单里有什么

| kind | 条目 | 来源 |
| --- | --- | --- |
| `hdri` | `hdri/night-sky-2k.jpg`、`hdri/night-sky-1k.jpg` | `NightSkyHDRI008_8K_TONEMAPPED.jpg` |
| `pbr` | `pbr/{clay,wood-floor,ceiling}-{color,normal,roughness}@1k.webp` | `Clay002_4K-JPG`、`WoodFloor070_4K-JPG`、`OfficeCeiling001_2K-JPG` |
| `glb` | `classroom/{desk,chair,blackboard,light}.glb` | `StylooClassroomAssetPack …/classroom/GLTF/{desk,chair,blackboardbig,lamp}.glb` |
| `vrm` | `monika/Monika.vrm`（`tracked: true`） | `素材/莫妮卡.vrm`（23,081,108 字节） |

## 为什么天空盒不是 EXR

原始文件 `NightSkyHDRI008_8K_HDR.exr` 是 **112,336,889 字节** 的 8K OpenEXR。它能被 three 的 `EXRLoader` 读取，但：

1. 112 MB 无法随前端发布，也不该进仓库；
2. 我们的场景是「太空教室」——天空只提供背景与一点点环境光，不需要 HDR 高光保真；
3. `sharp` 读不了 EXR（预编译 libvips 未启用 OpenEXR），要用 `oiiotool` 才能降采样，等于给构建加一个系统级依赖。

所以清单只用同目录的 `NightSkyHDRI008_8K_TONEMAPPED.jpg`（8K，已 tonemap）派生出 2K/1K JPEG：2K 给天空盒背景，1K 给低配或远处。**如果以后确实需要 HDR 高光**（例如做金属反射），把 `oiiotool` 装进开发机，补两条 `transform: "exr:<px>"` 的条目即可，前端 `assets.ts` 会按 manifest 找文件，不需要改场景代码。

## 许可

- `Monika.vrm`：作者 **tyler1234506**，reference `DDLC blazer`，许可证 `Other`（hub.vroid.com）。允许再分发与修改，**禁止商业使用**，必须署名 → 页面页脚保留署名（任务 4.7 / 5.x 已记）。改 FACE 形变只改运行时，不重写该文件。
- Styloo 教室包：随包 README 声明，仅用于非商用作品；本项目只取 4 个 GLB，不打包其原始贴图集以外内容。
- Poly Haven 材质与 HDRI：CC0。
