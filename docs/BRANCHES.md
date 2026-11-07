# 分支与恢复点

2026-11-07 将已完成的功能线统一到 `main`，GitHub 默认分支也设置为 `main`。
这条历史是此前开发分支的直接后代，保留全部源码提交，没有重写历史或 squash。

- `main`：后续开发与发布主线，包含已验证的语言、ISO、章节、界面、范围控制、
  插件更新、Infuse DV 兼容与普通 ISO / MKV 选择。
- `recovery/pre-infuse-chapters-20261101`：保留用户要求的章节开发前源码恢复点；
  独立工作区为相邻的 `EmbyBlurayHelper-pre-infuse-chapters`。该分支包含独有的
  恢复提交，故不作为无关分支删除。

已完成且提交均在 main 历史中的 feature、fix 与 experiment 分支可删除。
删除的是分支名，不是源码或历史；取消的 P8.1 ISO 研究记录仍在
`docs/INFUSE-DV-P81-ISO-EXPERIMENT.md`。

| 恢复点 | 提交 / 标签 |
| --- | --- |
| 章节开发前的恢复源码 | `3349a37` / `recovered/pre-infuse-chapters-20261101-2106` |
| 早期回归检查点 | `32031f9` / `checkpoint/2026-11-03-infuse-regression` |
| 普通 MKV 选项开发前，已有 DV 稳定功能 | `2efb56a` / `checkpoint/2026-11-07-before-container-choice` |
| 停止的 P8.1 ISO 实验 | `1e384ca`，仅隔离脚本和研究资料 |
| 新增普通 MKV 选项 | `7517d9f` |

后续较大功能建议使用短期 feature 分支，验证完成后合回 main 并删除已合并的
分支名；需要长期回退的版本使用标签保留，不依赖堆积开发分支。
