你现在接手 D:\Mas3D 这个仓库（Mas3D，Windows 桌面 3D 应用，Unity 6.6 + URP）。

开工前请按顺序做三件事，做完再动手改代码：
1. 读 D:\Mas3D\HANDOFF.md（接手说明：现状、环境、命令、11 条坑）。
2. 读 D:\Mas3D\AGENTS.md 与 D:\Mas3D\docs\superpowers\specs\2026-11-01-unity-rebuild-design.md。
3. 跑 powershell -NoProfile -ExecutionPolicy Bypass -File tools\check.ps1，把结果贴给我。
   今天的基线**不是全绿**：web / unity-edit / player 绿，unity-play 6 红、smoke 1 红。
   红的原因全部写在 HANDOFF「交接时的真实基线」表里——房间资产换成了新卧室（节点名 mesh_###），
   6 条 PlayMode 用例和 smoke 的 Cancel 回位还在用旧房间的网格名/坐标。这不是你弄坏的，别去回滚资产。

硬规则：
- 不要自己 git commit / git push，提交前问我。
- openspec/ 是唯一需求依据；改行为先改 openspec/changes/。
- 验证要贴真实输出（测试文件、构建退出码、截图），不要只说「应该可以」。

当前任务（按优先级）：
1. 给平层的三扇门加开/关交互（卧室↔客厅↔开放厨房），门是实体网格，玩家现在撞上就停。
2. 客厅上方有 15 m² 二层楼板（y=1.77）但源资产没有楼梯：补一段楼梯，或者把二层封掉。
3. 从室外看窗外星空片是一大块斜插面片，室内正常；外部取景时裁掉。

0. （先做这个）把那 6 条旧房间用例与锚点表按新房间几何重算：改 apps\unity\Assets\Tests\PlayMode\
   下的名字与坐标，以及 RoomLayout 的锚点；判据是离墙 ≥0.35 m、离静态碰撞体 ≥0.45 m。
   顺带查 smoke 的 Cancel 回位 0.180 m（判据 0.05 m）。
1. 给平层的三扇门加开/关交互（卧室↔客厅↔开放厨房），门是实体网格，玩家现在撞上就停。
2. 客厅上方有 15 m² 二层楼板（y=1.77）但源资产没有楼梯：补一段楼梯，或者把二层封掉。
3. 从室外看窗外星空片是一大块斜插面片，室内正常；外部取景时裁掉。

先只做第 0 项：先给我一个方案（改哪些文件、怎么测），我确认后再写代码。
