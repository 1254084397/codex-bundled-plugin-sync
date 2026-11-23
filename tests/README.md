# tests

脚本层（`scripts/*.ps1`）的行为测试。**不使用 Pester**，只依赖 Windows PowerShell 5.1，
在任何一台 Windows 机器上 `powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1` 就能跑。

## 隔离原则

- 每个用例在 `tests\.work\<case>\` 下自建 fixture：伪造的 `CodexHome` + 伪造的 `openai-bundled`。
- 不读 AppX、不碰真实注册表、不调用 `cipher`（`-EfsMode Simulate`）、不检查真实 Chrome 进程。
  native-messaging 的"注册表项"用 fixture 内的 `native-host-state.json` 代替。
- 跑完（全部通过时）自动删除 `tests\.work`；失败时保留现场。
- 组件级的失败注入通过 `CODEXSYNC_TEST_FAIL_AT=commit-N`，只在设置时生效。

## 用法

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Filter rollback   # 只跑名字含 rollback 的
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -KeepWork          # 保留 fixture 现场
```

另有一个独立的真机脚本（不属于 fixture 套件，会真实调用 `cipher`）：

```powershell
powershell -ExecutionPolicy Bypass -File tests\efs-acceptance.ps1
```

它只在工作区内的**未加密 NTFS 临时目录**上执行 `cipher /e` → 继承检查 → `cipher /d`，
验证“主动开启/撤销”与“既有加密状态不被误解除”，并在 `finally` 清理；
目标位于 `CODEX_HOME` 下或已加密时直接拒绝执行。

退出码：0 = 全绿，1 = 有失败。

## 用例与契约对照

| 用例 | 覆盖 |
| --- | --- |
| `sync-creates-complete-backup` | §6.3 备份骨架 → `status=complete`；journal 清理；stage 清理；native host 更新 |
| `sync-second-run-is-noop` | 无变化不写入、不产生新备份 |
| `sync-backup-records-only-changed-components` | 备份只记录本次实际改动的组件 |
| `rollback-restores-pre-sync-content` | §7.3 目录 + native host 一起回滚；安全快照留存 |
| `rollback-refuses-corrupt-backup` | 备份摘要与记录不符 → 拒绝回滚，live 不被动 |
| `rollback-single-component-backup` | 单项备份可回滚；`existedBefore=false` 语义为删除 |
| `rollback-legacy-backup-without-four-components` | 旧布局只读兼容，不再要求四类备份齐全 |
| `sync-mid-commit-failure-reverts-everything` | §6.4 提交后失败 → 全部退回原状 |
| `sync-first-component-failure-reverts-marketplace` | 第一个组件切换后失败同样退回 |
| `journal-recovery-restores-missing-live` | §6.6 live 缺失 → 从备份恢复 |
| `journal-unknown-digest-needs-attention` | 摘要都不匹配 → needs-attention，保留 journal，不动 live |
| `mutex-blocks-concurrent-run` | 并发互斥（跨进程），不同 CodexHome 不互相阻塞 |
| `efs-restore-after-enable` / `efs-restore-failure-is-reported` | §8 只撤销本次开启；撤销失败如实上报 |
| `native-host-manifest-path-boundary` | §9.4 文件名/父目录/`..`/空值边界 |
| `recovery-row3-backup-generated-live-untouched` | §6.6 行 3：live 不动、回滚点保留 |
| `recovery-row8-live-missing-restores-backup` | §6.6 行 8：备份回填并校验 |
| `recovery-row11-corrupt-backup-needs-attention` | §6.6 行 11：备份内容不符 → attention，不回填 |
| `recovery-row9-nothing-left-needs-attention` | §6.6 行 9：三份都不存在 → attention |
| `recovery-row6-committed-clears-stage` | §6.6 行 6：已提交态清理残留 stage |
| `rollback-crash-recovery-uses-safety-journal` | §6.4/§7.3：回滚崩溃后仅凭 journal 退回安全快照 |
| `rollback-failure-restores-from-safety` | §7.3：回滚提交中失败 → 全部退回回滚前内容 |
| `fixture-guard-keeps-real-registry` | fixture 不传覆盖参数时不碰真实注册表/真实清单 |
| `recovery-row10-full-set-deletes-stage-keeps-backup` | §6.6 行 10：删 stage、保留 backup |
| `recovery-incomplete-stage-needs-attention` | §6.6 行 11：stage 不完整 → attention 且保留现场 |
| `native-host-junction-is-same-final-path` | 真机 bug 1：junction 别名不得被当成 drift |
| `rollback-preserves-junction-alias` | 真机 bug 2：回滚必须保留 junction 且不改摘要 |

EFS 用例使用注入替身，只验证状态机；真机 EFS 行为按 `docs/DESIGN.md` §8 必须在真机验收。

> 注意：`fixture-guard-keeps-real-registry` 会比对真实 native-messaging 清单的 SHA-256，
> 它此前抓到过一个真 bug：函数局部变量 `$registryPath` 与参数 `$RegistryPath` 同名（大小写不敏感），
> 把守卫条件踩成假值。另有两个只有真机才暴露的 junction bug（清单路径不识别 junction、
> robocopy 把 junction 展开成真实目录）也已有回归用例。
> 最近一次全量运行：**31/31 通过**（2026-11-24）。
