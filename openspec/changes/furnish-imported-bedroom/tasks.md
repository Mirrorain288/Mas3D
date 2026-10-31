# Tasks

## 1. 导入与整备

- [x] 1.1 `roomDressing.ts`：`materialKindFor` 映射三处白模与屋顶，`dressImportedRoom` 换材质并返回可释放的 textures
- [x] 1.2 `createRoomLights`：月光 + 台灯 + 半球 + 吸顶暖光，带 1024 阴影与 `setShadows`
- [x] 1.3 `classroom.ts`：`buildImportedRoom` 对齐地板与座位、材质 `DoubleSide`、返回 `lightProfile`
- [x] 1.4 `lights.ts` / `Scene.tsx`：暴露 fill/rim 并按 `lightProfile` 压全局灯

## 2. 空墙

- [x] 2.1 `decorPlacements()` 两条摆放（书桌组与抽屉柜），整组 `sources`
- [x] 2.2 `furnishImportedRoom`：承接母本世界矩阵、premultiply 世界 Y、绕包围盒中心转、贴地/贴墙
- [x] 2.3 `VITE_ROOM_DECOR=none|tag` 调试开关

## 3. 默认加载

- [x] 3.1 `DEFAULT_IMPORTED_ROOM` 探路式加载，失败静默退回程序化教室
- [x] 3.2 `VITE_ROOM_GLB=none` 强制程序化；显式 URL 失败时打 `console.error`

## 4. 验证

- [x] 4.1 `roomDressing.test.ts` 6 例（映射表 + `decorPlacements` 结构约束）
- [x] 4.2 `e2e/smoke.mjs` 钉死 `VITE_ROOM_GLB=none`，10 项全过
- [x] 4.3 `e2e/room-preview.mjs` 无参自动加载 → 36 网格 / 29,537 三角面 / 31 材质，pageErrors 空
- [x] 4.4 `e2e/room-angles.mjs` 四机位对照，右墙空墙已补

## 5. 文档与工具

- [x] 5.1 `tools/blender/export-room.py`、`tools/blender/inspect-parts.py`
- [x] 5.2 README 新增"换房间（自备卧室场景）"

## 6. 待办

- [ ] 6.1 提交与推送（需用户确认）
- [ ] 6.2 `openspec archive` 归档本 change
