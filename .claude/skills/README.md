# 第三方 project skills：mattpocock-skills

这个目录下（除本文件和 `LICENSE-mattpocock-skills.txt` 外）的所有子目录，都是从
[mattpocock/skills](https://github.com/mattpocock/skills)（Matt Pocock 的 Claude Code
技能集，MIT 协议，官方 Claude Code plugin marketplace 里的 `mattpocock-skills` 插件的
上游源码仓库）手动复制进来的，版本对应上游 `.claude-plugin/plugin.json` 的
`1.2.3`（复制时的最新 commit：`c55ee46073ed923f86ce59a5eb3b6d895095d1b7`）。

**为什么是手动复制，不是装插件**：这个 Claude Code 环境不支持 `/plugin` 命令，没法走
`/plugin install mattpocock-skills` 这条官方路径。手动复制的代价：装的是这个 commit 的
快照，上游更新以后不会自动跟进，需要重新拉取、重新对比复制；而且这里只拿了
`.claude-plugin/plugin.json` 里 `skills` 数组列出的 25 个正式技能（`engineering/`
18 个 + `productivity/` 7 个），上游仓库里的 `deprecated/`/`in-progress/`/`misc/`
三个目录不属于插件正式发布的内容，没有一并复制。

**协议**：上游 `LICENSE`（MIT，Copyright (c) 2026 Matt Pocock）原样保留在同目录的
`LICENSE-mattpocock-skills.txt`。

**跟本项目已有技能的重名**：这批技能里的 `code-review` 会覆盖/遮蔽这个 Claude Code
会话原本就提供的同名 `code-review` 技能（两者内容不同）——项目目录下的这份，在
EveryStage 仓库范围内会优先命中。
