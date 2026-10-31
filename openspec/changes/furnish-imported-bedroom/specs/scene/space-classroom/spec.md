# scene/space-classroom Delta

## ADDED Requirements

### Requirement: 室内几何可以来自导入的场景包

系统 SHALL 支持从一份外部 GLB 加载整套室内几何（墙、地板、屋顶、家具、贴图），并在加载成功后以其替换程序化几何；导入房间 SHALL 自带座位锚点与朝向常量（`IMPORTED_ROOM_LAYOUT`），不得依赖对家具形状的自动识别。

#### Scenario: 导入房间可用
- **WHEN** `/assets/room/mita-room.glb` 或 `VITE_ROOM_GLB` 指定的 URL 加载成功
- **THEN** 场景展示该房间，地板对齐 y=0、椅子座位平移到原点，角色坐在该座位上面向房间，且不再渲染程序化教室的墙与地板

#### Scenario: 导入房间的家具被整组复制
- **WHEN** 场景需要在空墙前补一件组合家具（例如书桌与其上的显示器）
- **THEN** 该组合的所有网格一次复制并保持相对位置，不得出现只复制了显示器、主机留在原地的散件

### Requirement: 导入场景缺失时静默退回程序化几何

系统 MUST 在导入房间资源缺失或解析失败时退回程序化几何并保持可玩；仅在玩家显式指定了房间 URL 时记录一条可诊断的错误。

#### Scenario: 未放置房间素材
- **WHEN** 玩家在没有房间 GLB 的机器上启动前端
- **THEN** 游戏进入程序化教室，控制台没有"房间加载失败"这类噪声

#### Scenario: 显式指定但失败
- **WHEN** 玩家显式设置 `VITE_ROOM_GLB` 且该资源加载失败
- **THEN** 系统退回程序化教室，并在控制台记录一次带 URL 的错误

#### Scenario: 强制程序化
- **WHEN** 玩家设置 `VITE_ROOM_GLB=none`
- **THEN** 系统跳过导入分支，即使本机存在房间 GLB 也使用程序化几何

### Requirement: 导入房间的光照由场景侧统一

系统 SHALL 在导入房间时由场景侧补齐顶光与补光，并压低程序化教室的全局三盏灯，避免双重打光。

#### Scenario: 导入房间的灯光配置
- **WHEN** 导入房间加载完成
- **THEN** 房间里存在吸顶暖光与半球补光，程序化教室的 key/fill/rim 三盏被压到 `lightProfile` 给出的低强度，关闭阴影时这些灯也一起关闭阴影
