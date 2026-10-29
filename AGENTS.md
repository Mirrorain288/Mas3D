# D:\Mas3D 项目约定

## 双工作流：OpenSpec + Superpowers

本仓库同时启用两套技能，全部位于 `.dsh/skills/`。

**OpenSpec（需求与变更文档）**
`openspec-explore` `openspec-propose` `openspec-update-change` `openspec-apply-change` `openspec-sync-specs` `openspec-archive-change`

**Superpowers（设计、实施、测试、审查）**
`using-superpowers` `brainstorming` `writing-plans` `executing-plans` `subagent-driven-development` `test-driven-development` `systematic-debugging` `verification-before-completion` `requesting-code-review` `receiving-code-review` `finishing-a-development-branch` `dispatching-parallel-agents` `using-git-worktrees` `writing-skills` `diagnosing-superpowers`

### 开始任何开发任务前

1. 先读 `.dsh/skills/using-superpowers/SKILL.md` 并遵循它。
2. 需求不清楚：用 `openspec-explore` 摸清现状与方案；需要发散设计时用 `brainstorming`。
3. 需求定稿：用 `openspec-propose`，产出写入 `openspec/changes/<change-id>/`（proposal.md、specs/、design.md、tasks.md）。
4. 实施：用 `writing-plans` 排任务，再用 `subagent-driven-development` 或 `executing-plans` 逐项执行，同步勾选 tasks.md。
5. 完成前：用 `verification-before-completion` 给出实际验证结果，必要时 `requesting-code-review`。
6. 我确认验收后：用 `openspec-archive-change` 归档并同步主规格。

### 约束

- 不要自行 `git commit` 或 `git push`，需要提交时先问我。
- `openspec/` 是唯一的需求与验收依据，不要另建重复的需求文档。
- 改动前先跑 `openspec validate`，确认变更文档结构完整。