# 脚本层审查修复记录（M1 增量 2 前置）

> 对应审查：组内审查对 `scripts/*.ps1` 的五条结论（回滚安全性、提交失败恢复、EFS 还原、
> 注册表路径边界、并发互斥）。
> 对应契约：`docs/DESIGN.md` §6.3、§6.4、§6.6、§7.3、§8、§9.4。
> 本文件只记录"改了哪、怎么验"，不把未验证的项写成已完成。

## 1. 五条发现的落点

| # | 审查发现 | 修复 | 落点 | 回归用例 |
| --- | --- | --- | --- | --- |
| 1 | 回滚先把 live 移入 safety 再复制备份；且要求四类备份齐全，而同步只备份实际更新的组件 | 备份改为组件快照：同步写 `backup.json`（只记本次实际改动的组件，含 `previousDigest`）；回滚按快照执行，**先校验备份内容、再复制到 stage、再动 live**；没有摘要的 manifest 备份直接拒绝 | `sync-openai-bundled.ps1`（backup.json 骨架 → 逐组件 `state`/`digest`/`previousDigest`）、`rollback-openai-bundled.ps1`（选备份 → 校验 → 安全快照 → stage → 交换） | `rollback-refuses-corrupt-backup`、`rollback-single-component-backup`、`rollback-legacy-backup-without-four-components` |
| 2 | 同步中途失败不恢复已切换组件，可能留下混合版本 | 引入 §6.4 状态机（`preflight→planned→staged→verified→commit-started→finalize`）+ 写前落盘 journal；`commit-started` 之后的任何失败都把已切换组件退回原状（新内容移到 `rejected-*` 留证，备份回填），native host 清单一起回退 | 两份脚本的 catch/finally + `Invoke-CodexSyncComponentSwap` / `Repair-CodexSyncOperation -Goal Revert` | `sync-mid-commit-failure-reverts-everything`、`sync-first-component-failure-reverts-marketplace` |
| 3 | EFS 状态未还原（对 marketplace 父目录开启后没恢复） | 记录 `preEncrypted`/`enabledByThisRun`，`finally` 里只撤销本次自己的开启；恢复失败不静默（退出码 2 + 明确原状态/当前状态）；不碰用户既有加密状态 | `sync-openai-bundled.ps1`（EFS 预检/finally）、`Restore-CodexSyncEfsState` | `efs-restore-after-enable`、`efs-restore-failure-is-reported` |
| 4 | Chrome 注册表路径缺边界校验，可能覆盖非预期文件 | 注册表默认值视为外部数据：必须是名为 `com.openai.codexextension.json` 的文件、父目录落在 `LOCALAPPDATA`/`APPDATA`/CodexHome 之内、不含 `..`，否则拒绝写入 | `Assert-CodexSyncNativeHostManifestPath` | `native-host-manifest-path-boundary` |
| 5 | 没有并发互斥，同秒启动会争用同一备份目录 | 按 CodexHome 真实路径派生 `Local\CodexPluginSync-<hash>` 命名互斥（sync/rollback 共用）；运行 ID 由时间戳 + PID + 随机后缀组成，不再撞名 | `Enter-CodexSyncMutex` / `Get-CodexSyncTimestamp -Unique` | `mutex-blocks-concurrent-run` |

## 2. 顺带补的契约缺口

- **崩溃恢复有了判定依据**：journal 里每个 target 记 `oldDigest`/`newDigest`，恢复走 §6.6 的"存在性 + 树摘要"判据；判不出唯一动作时返回 `attention`（退出码 2），保留 journal 与快照，不猜。
- **备份摘要语义拆开**：`digest` = 同步后 live 内容，`previousDigest` = 备份（同步前）内容。回滚只拿 `previousDigest` 校验备份，避免"备份被篡改却校验通过"。
- **JSON 一律 UTF-8 无 BOM**：`native-messaging` 清单和 `backup.json` 之前用 `Set-Content -Encoding UTF8` 会带 BOM，Chrome 与 `ConvertFrom-Json` 都可能拒绝。现统一走 `Write-CodexSyncJsonFile`。
- **组件名含冒号**（`cache:browser`）不能直接当目录名，退回/恢复角标统一经 `Get-CodexSyncSafeName`。
- **fixture 隔离**：`-SourceBundle` 模式下若没有显式 `-NativeHostStateFile`/`-NativeHostRegistryPath`，native host 一律跳过（`result=skipped-fixture`）。原生脚本默认仍操作真实注册表。

## 3. 新增可测接口（只给 fixture 用）

| 参数 | 脚本 | 用途 |
| --- | --- | --- |
| `-SourceBundle <dir>` | sync | 用伪造的 `openai-bundled` 代替 AppX 定位（§16.1） |
| `-EfsMode Simulate` | sync | 只读记录 EFS 状态，不调用 `cipher`；**真机验收必须用默认 `Real`** |
| `-SkipProcessCheck` | sync / rollback | 跳过 Chrome/Codex 进程守护 |
| `-NativeHostStateFile <json>` | sync / rollback | 用工作区内的 JSON 文件代替 HKCU 注册表项 |
| `-NativeHostRegistryPath <key>` | sync | 覆盖注册表路径 |
| `-BackupId <id>` | rollback | 指定备份（`backup.json` 的 `backupId` 或目录名） |
| `CODEXSYNC_TEST_FAIL_AT=commit-N` | sync | 失败注入：第 N 个组件切换后抛错（生产环境读到空值即无副作用） |

## 4. 已验证

```text
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
  → 全部通过：15 个用例

MSBuild build\CodexPluginSync.csproj -p:Configuration=Release
  → CodexPluginSync.exe（单文件断言通过；仍有本机缺 4.8 targeting pack 的 MSB3644 警告）

build\bin\Release\CodexPluginSync.exe --selftest
  → SELFTEST PASS (12 cases)
```

## 6. 第二轮审查（矩阵与回滚 journal）的修复

> 状态：两处恢复契约缺口已由审查方（Cindy）就代码层面确认通过（2026-11-24）。
> 代码层结论：stage 完整性前置已生效；`Repair-CodexSyncOperation` 对 Recover/Revert 都先只读预检全部 target，
> 任意终态混合即 `attention`，不先移动 live，保留 journal 与安全快照。
> 验证：31/31 用例、构建、`--selftest`、真机写入式验收（sync → check → rollback → check）均已通过（2026-11-24）；
> EFS 主动 `cipher /e`、`cipher /d` 路径及 A–F 边界验证均已通过实机验收（详见 §8）；剩余边界见 §9。

**跑起来才发现的 bug（说明此前"未验证"的标记是必要的）：**

1. `@($list)` 对 `List[object]` 会抛 `Argument types do not match`（`List[string]` 不会）——
   `Repair-CodexSyncOperation` 的返回值改用 `.ToArray()`。
2. 函数局部变量 `$registryPath` 与参数 `$RegistryPath` 在 PowerShell 里是**同一个变量**（大小写不敏感），
   赋值后把 `fixture 守卫` 的条件踩成了假值 → fixture 运行会去写真实注册表指向的真实清单。
   已改名 `effectiveRegistryPath`；`fixture-guard-keeps-real-registry` 用例就是因这个 bug 才红。

### 6.1 崩溃恢复改成 §6.6 矩阵驱动

`Resolve-CodexSyncPendingOperation` 不再只看存在性，也不再"live 是旧的就把 journal 删了"。
现在的形状：

```text
Get-CodexSyncTargetSnapshot    → live/backup/stage(+safety) 的存在性 + 树摘要
Resolve-CodexSyncTargetRow     → 按 §6.6 的 11 行矩阵给出唯一动作与终态（含行号）
Repair-CodexSyncOperation      → 执行动作；逐个动作后校验摘要；写日志带 row=N
```

- **摘要参与判定**：`live/backup/stage` 都要算摘要并与 `oldDigest`/`newDigest` 比对；
  backup 内容与 `oldDigest` 不一致时落进行 11（`attention`），**不会**把不完整的备份回填到 live。
- **行 9/11 一律 `attention`**：保留 journal、备份、stage，不猜。
- **行 4/5/6/7 是"继续收尾"**：live 已是新内容时不再当错误处理，而是补 native host + `backup.json`
  `status=complete`，然后继续本次同步（该次会判定为"无需写入"）。
- **组件终态不一致**（部分行判为已提交、部分未提交）也归 `attention`，不自动收尾。
- 进程内失败另走 `-Goal Revert`：目标是把已切换组件退回操作前内容，同样按摘要判定、
  备份内容不可识别时直接 `attention`。

### 6.2 回滚 journal 在动 live 之前就写满

上一版的顺序错误：`rollback-commit-started` 写在 `targets` 为空时，target 里也没有摘要和安全快照路径。
现在：

```text
rollback-preflight        选备份 + 逐组件校验备份内容
rollback-safety-created   复制安全快照并校验（此时仍不动 live）
                          → 准备 stage、算 newDigest，写入 targets（含 safetyPath/oldDigest/newDigest）
rollback-commit-started   targets 已完整，从此任何崩溃都能按 §6.6 判定
rollback-committed / rollback-finalized
```

- `oldDigest` = 回滚前 live（= 安全快照）内容；`newDigest` = 要回填的备份内容。
- 回滚失败/崩溃的恢复走同一个 `Repair-CodexSyncOperation`，**不依赖进程内列表**（原来的
  `committedPlan` 与 `Restore-FromSafety` 已删除）。
- 回滚自己失败时退回安全快照，并校验回填后的摘要。

### 6.3 新增回归用例（16 个）

| 用例 | 断言的东西 |
| --- | --- |
| `recovery-row3-backup-generated-live-untouched` | 行 3：live 不动、回滚点保留、journal 清理 |
| `recovery-row8-live-missing-restores-backup` | 行 8：备份回填 live 并校验 |
| `recovery-row11-corrupt-backup-needs-attention` | 备份内容与 oldDigest 不符 → `attention`，不回填 |
| `recovery-row9-nothing-left-needs-attention` | 三份都不存在 → `attention`，保留 journal |
| `recovery-row6-committed-clears-stage` | 行 6：残留 stage 清理、新内容保留、判为已提交 |
| `rollback-crash-recovery-uses-safety-journal` | 只用 journal（safetyPath + 两端摘要）就能退回回滚前内容 |
| `rollback-failure-restores-from-safety` | 回滚提交中注入失败 → 全部组件退回回滚前内容 |
| `fixture-guard-keeps-real-registry` | 不传覆盖参数的 fixture 运行：日志有 `skipped-fixture`，真实清单 SHA-256 不变 |
| `recovery-row10-full-set-deletes-stage-keeps-backup` | 行 10：三份齐全未交换 → 删 stage、保留 backup、live 不动 |
| `recovery-incomplete-stage-needs-attention` | stage 摘要 ≠ newDigest → 行 11，`attention` 且保留 stage 现场 |
| `recovery-committed-with-corrupt-stage-needs-attention` | live=新、backup=旧、stage 损坏（row 6 的伪装）→ `attention`，不删 stage |
| `recovery-mixed-terminals-needs-attention` | sync 的 `committed + aborted` 混合 → `attention`，未先回退已提交组件 |
| `recovery-rollback-mixed-terminals-needs-attention` | rollback 的 `recovered + aborted` 混合 → `attention`，安全快照未被提前消费 |
| `revert-with-corrupt-backup-needs-attention` | `-Goal Revert` 时备份摘要不符 → `attention`，不回填、保留 journal |
| `native-host-junction-is-same-final-path` | 真机 bug 1：`latest` junction 与版本目录必须判为同一文件 |
| `rollback-preserves-junction-alias` | 真机 bug 2：回滚后 `chrome\latest` 必须仍是 ReparsePoint 且摘要不变 |

（总数由 15 增至 31；31 个已全部实跑通过，见下。）

### 6.3.1 最近一次验证结果（2026-11-24）

```text
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
  → 全部通过：31 个用例（跑前跑后真实 native-messaging 清单 SHA-256 一致）
MSBuild build\CodexPluginSync.csproj -p:Configuration=Release
  → CodexPluginSync.exe（发布目录仍只有 1 个 exe；仍有缺 4.8 targeting pack 的 MSB3644 警告）
build\bin\Release\CodexPluginSync.exe --selftest
  → SELFTEST PASS (12 cases)
```

### 6.4 矩阵 S 列按"完整新树"解释

§6.6 的 S 列写的是"新（完整）"，所以行 2/4/6/10 都要求 `stage 摘要 == newDigest`。
实现额外加了一条硬前置：`stage 存在但摘要不匹配`（拷了一半、或算不出摘要）直接落行 11
`attention`，**不**归到任何一行，也**不**删 stage（配套约束要求 attention 保留 stage 现场）。
对应地，`Repair-CodexSyncOperation` 里原来"不管哪行都清一下 stage"的兜底已删除，
两份脚本的 `finally` 在 journal 仍存在时也不再清 stage。

## 7. 事故记录（不得被测试结果抵消）

> 审查方要求保留：以下两起事件真实发生过，后续“测试全绿/构建通过”不构成对它们的抵消。

| 时间 | 事件 | 处置与现状 |
| --- | --- | --- |
| 2026-11-24 早些时候 | 加隔离之前的 fixture 测试走了真实 `HKCU` 注册项，把真实的 `%LOCALAPPDATA%\OpenAI\extension\com.openai.codexextension.json` 改写成 fixture 路径 | 已恢复；根因是 `$registryPath` 与参数 `$RegistryPath` 同名（PowerShell 大小写不敏感）踩掉了 fixture 守卫，现已改名 `effectiveRegistryPath`，并由 `fixture-guard-keeps-real-registry` 比对真实清单哈希锁住 |
| 2026-11-24 01:11 | 我为复现上述问题写的调试脚本误用了只读变量 `$HOME`（`$home` 同名），一次同步以 `-Root C:\Users\12540` 真跑：改写同一份真实清单，并在用户目录新建 `.tmp\`、`plugins\cache\openai-bundled\`、`backups\` 与一条 `logs\sync-openai-bundled-*.log` | 四个新建项按创建时间逐项核对后删除（`plugins\` 本身创建于 6 月，保留）；清单恢复为 `E:\CodexHome\plugins\cache\openai-bundled\chrome\latest\...`，与 `E:\CodexHome\backups\openai-bundled-sync-20261122-221338\com.openai.codexextension.json.previous` 哈希一致；之后全量测试跑前跑后该清单 SHA-256 未变 |

教训（已落进测试）：任何写真实外部状态的路径必须能用 fixture 证明“不写”，不能只靠代码走读；仅“测试全绿”不能证明“无副作用”。

## 8. 真机验收（2026-11-24，写入式）

在用户完全退出 Codex Desktop / Chrome / extension-host 后，对 `E:\CodexHome` 做了完整演练：

```text
--check（只读）                        Verdict ok / Update needed No
--sync（无参数）                        全部组件最新 → “无需写入”（幂等）
--sync -Force                          备份 status=complete；marketplace + 3 个缓存全部 staged/verified/swapped；
                                       native host updated；EFS preEncrypted=True / enabledByThisRun=False / restored
--check（同步后）                       Verdict ok
--rollback                             4 个组件全部 verify-backup ok + safety verified + commit restored；
                                       native host manifest 字节级回退；汇总校验通过
--check（回滚后）                       Verdict ok
```

回滚后逐项比对 **与同步前完全一致**：marketplace / browser / chrome / computer-use 四个树摘要相同；
清单 SHA-256 相同且回到 `...\chrome\latest\...`；`chrome\latest` 仍是 ReparsePoint；
无 journal、无 stage 残留。

### EFS（§8 真机验收点）

**两段验收：**

1. **既有加密状态保护（`E:\CodexHome` 实机）**：`marketplace` 父目录本来就已加密（preEncrypted=True），
   本次同步因 `enabledByThisRun=False` **没有开启 EFS**，收尾只观察并报 `result=restored`，
   没有误解除用户既有加密状态。
2. **主动 `cipher /e` → `cipher /d` 路径（工作区内未加密 NTFS 临时目录，`tests/efs-acceptance.ps1`）**：

```text
目标位于工作区内（硬校验）/ volume=D fs=NTFS / preEncrypted=False
cipher /e   exit=0  encrypted=True
新建文件   继承加密 = True；加密后本用户仍可正常读写
Restore(EnabledByThisRun=true)   restored=True  nowEncrypted=False
Restore(EnabledByThisRun=false)  restored=True  stillEncrypted=True（既有加密不被误解除）
撤销失败路径（目标不存在）      restored=False + 具体原因
清理：cipher /d 后删除临时目录，结果 = 干净
→ EFS 验收通过，exit 0
```

**这次验收又拓出一个真 bug**：`cipher /d` 在路径不存在时仍返回 0，函数当时会"宣称已恢复"。
已改为“**观察不到加密属性就不宣称成功**”（`Restored=false` + 具体原因），
两份脚本的 EFS 收尾同步受益；该判据已写进 `docs/DESIGN.md` §8.2 第 7 步（v0.4）。

**验收脚本自身的安全边界（已实测）**：

| 场景 | 结果 |
| --- | --- |
| A. 正常跑（自动唯一随机目录） | exit 0，`cipher /e→/d` 全绿，目录自动清理 |
| B. 目标已存在（预先造目录 + 标记文件） | **exit 1**，目录与标记文件原封不动（不再递归删除） |
| C. `-KeepWork` 保留的现场，后续运行 | 保留目录与其标记文件均未被删除 |
| D. 父目录是 junction 且指向仓外 | **exit 1**（`ABORT 父目录解析后的真实路径不在工作区内`），仓外目录里没有创建任何东西 |
| E. 显式 `-Scratch` 的父目录不存在 | **exit 1**（`ABORT 显式 -Scratch 的父目录不存在，脚本不代建`），父目录未被创建 |
| F. 显式 `-Scratch` 的父目录存在且在仓内 | **exit 0**，`ok 目标父目录（真实路径）位于工作区内`，跑完目录已删 |

E/F 是“显式 `-Scratch` 不得代建父目录”这条修正的专项回归；清理后 `tests\.work` 残留 0。

### 真机跑出来的两个 bug（已修 + 已加回归）

| # | 现象 | 根因 | 修法 |
| --- | --- | --- | --- |
| 1 | 清单指向 `...\chrome\latest\...`，`--sync` 每轮都报 `result=drift` 并准备重写 | 纯字符串比较，不识别 junction | `Resolve-CodexSyncFinalPath`（GetFinalPathNameByHandle）+ `Test-CodexSyncSameFinalPath`；用例 `native-host-junction-is-same-final-path` |
| 2 | 回滚校验必失败：`恢复内容复制后校验失败：cache:chrome` | `robocopy` 默认展开 junction，`chrome\latest` 被拷成真实目录，158 → 317 个文件 | 拷贝加 `/XJ` + `Copy-CodexSyncReparseLinks` 按原目标重建（悬挂目标用 `mklink /J` 兑回）；用例 `rollback-preserves-junction-alias` |

两个 bug 都是 fixture 跑不出来的（fixture 里没有 junction），只有真机验收能暴露。

## 9. 仍未验证 / 不做承诺

- **EFS**：`cipher /e` 开启、继承、`cipher /d` 撤销、失败上报已在工作区内未加密临时目录上验收通过
  （`tests/efs-acceptance.ps1`）。仍需注意的边界：真机 `marketplace` 目录在本机已加密，
  所以“在真实 CodexHome 上触发开启路径”依然未发生；D-01（是否应长期加密）仍待结论。
- **D-01**（marketplace 父目录是否该长期加密）仍待结论；当前实现只撤销本次自己的开启。
- **Codex 是否会自己重建 `chrome\latest`**：同步后新缓存来自 AppX 源包（不含该 junction），
  下次启动 Codex Desktop 时需确认它是否重建别名（目前清单指向版本目录，不依赖别名）。
- C# 侧（`src/CodexPluginSync`）已接管并已验证：`--check`、`--sync`（组件集合、stage、七阶段事务、§9.4 native host、§8 EFS）、
  `--rollback`（§7.3）与 `--selftest`（**21 cases**，2026-11-24 实测：`SELFTEST PASS (21 cases)`）。
  仍未做：`--cleanup-only`、`--export-diag`、崩溃后自动执行恢复矩阵（当前只报告 phase 并阻停）、
  native host 回滚的“删键”分支（见 `docs/M1-STATUS.md` 的“未实现”表）。
- 回滚的**真机**验收尚未做：C# 回滚只在临时目录与 fixture 上验证过（`rollback/restore`、
  `rollback/roundtrip`、`rollback/guards`），脚本层 `--rollback` 的真机演练见 §8。两者不能互相代替。
- 运行约束（与本次验收不矛盾）：**后续再次执行写入操作**时仍需先关闭 Chrome / Edge / Codex Desktop，
  否则进程守卫会拒绝写入（设计行为）。**本次**真机验收已在彻底退出这些进程后完成，详见 §8。
