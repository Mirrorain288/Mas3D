# Spec Delta

## Purpose

把默认角色从 `Monika.vrm` 换成 `Monkia.vrm`，并把"署名必须写对"与"表情只有一个写入者"固化为要求。

## MODIFIED Requirements

### Requirement: 角色许可与署名义务在界面可见

系统 MUST 在游戏界面内提供玩家可发现的署名信息，注明**当前实际加载的**角色模型作者与使用限制（非商用）；该信息 MUST NOT 依赖外部链接才可见，且 MUST 由模型自身的 VRM meta（`title`/`author`，VRM 1.0 为 `name`/`authors`）派生，而不是在界面代码里写死。

#### Scenario: 查看署名
- **WHEN** 玩家以默认配置打开游戏
- **THEN** 界面显示角色作者 `LilowLeeLani`、模型名 `Monkia` 与"仅限非商业使用"的说明

#### Scenario: 换模型后署名同步
- **WHEN** 通过 `VITE_VRM_URL` 换成另一副 VRM（例如回退到 `Monika.vrm`）
- **THEN** 页脚显示那副模型 meta 里的作者与模型名，不残留上一副模型的署名

#### Scenario: meta 缺失
- **WHEN** 模型文件没有声明作者字段
- **THEN** 页脚仍显示"仅限非商业使用"与模型缺失作者的说明，不显示其它模型的名字

### Requirement: 角色按 VRM 0.0 装载并探测可用通道

系统 SHALL 加载部署配置指定的 VRM（默认 `/assets/monika/Monkia.vrm`，可由 `VITE_VRM_URL` 覆盖），并从中探测：人形骨骼是否完整、可用表情 preset 集合、可用 morph target、SpringBone 链数量。探测结果 SHALL 作为后续所有表现能力的能力上限。

#### Scenario: 探测成功
- **WHEN** 角色文件加载完成
- **THEN** 系统得到可查询的能力表（骨骼/表情/眨眼通道是否存在），并且不因缺失某个通道而在运行时抛错

#### Scenario: 缺少 lookAt 装配
- **WHEN** 角色未声明 first-person lookAt 装配（两副模型都是如此）
- **THEN** 系统改用直接驱动眼球骨骼并叠加头颈旋转来实现视线，且视线功能仍然可用

## ADDED Requirements

### Requirement: 表情与口型只有一个写入者

系统 MUST 保证面部 `Fcl_*` 形变在任一时刻只有一个写入者：加载完成后 MUST 注销 three-vrm 表情管理器里的全部表达式，使 `FaceDriver` 独占写入；MUST NOT 依赖模型 `blendShapeGroups` 的 binds 是否为空。

#### Scenario: 模型自带 blendshape binds
- **WHEN** 加载的 VRM 在 `blendShapeGroups` 里带真实 bind（`{index, mesh, weight}`）
- **THEN** 表情与口型权重在整个播放过程中保持驱动值，不出现"先写进去、约 1 秒后归零"

#### Scenario: 模型没有 binds
- **WHEN** 加载的 VRM 的 binds 全部为空
- **THEN** 行为与之前一致，表情与口型照常写入，且注销操作不抛错

### Requirement: 落座时按模型自身脚掌厚度贴地

系统 MUST 让任何加载的角色在坐姿下脚底落在地面上，同时保持 hips 对齐由场景量出的座面高度；补偿 MUST 只调整小腿弯曲，MUST NOT 整体升降模型或按身高缩放。

#### Scenario: 换上腿更长的模型
- **WHEN** 加载的模型站立脚踝高度与标定模型不同（例如 0.100 m 的新模型 vs 0.061 m 的旧模型）
- **THEN** 加载后脚踝停在"该模型脚掌厚度"对应的高度上，误差不超过 1 cm，hips 仍在座面上

#### Scenario: 缺少小腿骨骼
- **WHEN** 模型的 humanoid 里没有小腿骨骼（或膝盖没有垂直杠杆）
- **THEN** 系统跳过补偿、不抛错，角色仍以标定姿态落座
