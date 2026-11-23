# 设计文档：Codex Bundled Plugin Sync

> 状态：草案（v0.1，2026-06）
> 适用范围：本仓库的图形界面（EXE）重构计划
> 相关文件：[README](../README.md)、[scripts/](../scripts/)、[sync-codex-bundled.cmd](../sync-codex-bundled.cmd)

---

## 1. 这个软件为什么存在

### 1.1 它解决的具体故障

Codex Desktop 以 Windows AppX（Microsoft Store 包）形式安装，它的内置插件（`openai-bundled`）位于安装目录内：

```
C:\Program Files\WindowsApps\OpenAI.Codex_<版本>_<架构>__<发布者哈希>\
  app\resources\plugins\openai-bundled\
```

而 Codex 运行时要用的插件，并不直接从安装目录加载，而是先"物化（materialize）"到用户目录：

| 位置 | 作用 |
| --- | --- |
| `%USERPROFILE%\CodexHome\.tmp\bundled-marketplaces\openai-bundled` | 持久化 Marketplace（插件市场源） |
| `%USERPROFILE%\CodexHome\plugins\cache\openai-bundled\<插件>\<版本>` | 浏览器 / Chrome / 电脑操作插件的缓存 |
| `%USERPROFILE%\CodexHome\.tmp\bundled-marketplaces\openai-bundled\.materialization-key` | Codex 自己记录的"这次实际物化了哪些插件、哪个版本" |

问题出在**更新**：

1. Codex Desktop 通过 Store 更新时，AppX 目录整包替换，路径里带版本号，`WindowsApps` 下的旧目录直接被移除。
2. 用户目录里的 Marketplace 和缓存是**另一份拷贝**，不会跟着更新。
3. Windows 不允许 Codex 自己回写 `WindowsApps`，也不保证每次启动都重新物化。

结果就是一个极难自查的状态：Desktop 已经 26.2.0，但浏览器插件还在跑 1.4.0 的旧缓存，或 Chrome 扩展找不到 native messaging 宿主（因为 `extension-host.exe` 的路径里嵌了旧版本号）。

### 1.2 它做的事，一句话

> 把 AppX 安装包里的内置插件，安全地同步到 `CodexHome` 的 Marketplace 和缓存目录，并修好 Chrome 扩展的 native messaging 路径 —— 带校验、带备份、可回滚。

具体是四个动作：

| 动作 | 内容 |
| --- | --- |
| **检查** | 比对 Desktop 版本 / Marketplace / 缓存三者，回答"要不要同步"，全程只读 |
| **同步** | 复制 → SHA-256 全量校验 → 切目录；替换前自动备份；顺带修正 Chrome native messaging 注册表里的 exe 路径 |
| **回滚** | 用备份把 Marketplace 和缓存恢复成同步前的样子 |
| **清理** | 删除过期 stage 目录、旧备份、旧日志，避免 `CodexHome` 越用越大 |

### 1.3 谁会用到它

| 用户 | 触发场景 | 关心什么 |
| --- | --- | --- |
| 普通 Codex Desktop 用户 | 更新 Desktop 后浏览器/电脑操作插件失灵、反复提示更新、Chrome 扩展连不上 | 点一下就修好，看不懂日志也没关系 |
| 折腾型用户 | 自己切换版本、改过 `CODEX_HOME`、装在非系统盘 | 能看清到底哪一项不一致，能回滚 |
| 企业/多机环境 | 批量部署、离线安装、给同事装 | 可脚本化、退出码、幂等 |
| 项目维护者 | 每次 Desktop 更新后要重复同样的手工操作 | 少踩坑、有日志能定位 |

### 1.4 有没有必要（诚实版）

**有必要的情形：**

- 你把 Codex Desktop 当日常工具用，并且依赖浏览器 / Chrome / 电脑操作这三个内置插件。Desktop 每次更新后，它们是最容易静默失效的部分，且失败表现是"功能没了"而不是"报错"。
- 你的 `CodexHome` 在非系统盘、被迁移过、或曾经手工改动过，状态本身就已经漂移。
- 你需要可回滚 —— 手工复制粘贴出错后没有退路，而这个工具是"先备份再替换"。

**没必要的情形（别为了用而用）：**

- 你只用 Codex 的对话/代码能力，从不用内置插件。那同步不做也没有可感知影响。
- OpenAI 后续把物化逻辑改成"每次启动自检并自动重物化"。**这是最可能的失效路径**，一旦 Desktop 自己处理了，本工具的 `--check` 会永远返回"不需要更新"，项目就该归档。
- 你在 macOS / Linux 上 —— 路径与打包方式完全不同，本工具不适用。

**判断标准一句话**：如果你曾经因为 Desktop 更新后"浏览器插件突然不能用了"而去手工翻 `WindowsApps` 目录，你就需要它；如果你从没遇到过，可以先只跑 `--check` 观察一两次。

### 1.5 它与官方的关系

非官方社区工具，不隶属于 OpenAI，也不修改 Store 应用的任何文件。所有写入都发生在 `%USERPROFILE%\CodexHome`（或用户指定的目录）与 `HKCU` 下的一条 native messaging 注册表项，且每次替换前都留备份。AppX 安装目录**只读**。

---

## 2. 目标与非目标

### 2.1 目标

1. **单文件分发**：一个 exe，双击可用，零运行时依赖，可放在任意目录。
2. **普通用户可用**：状态一眼看懂，主操作只有一个按钮，失败能看懂原因。
3. **比脚本更安全**：事务化替换、崩溃可恢复、任意备份可恢复。
4. **机器可用**：完整 CLI，退出码稳定，可被计划任务/CI 调用。
5. **可诊断**：一键导出诊断包，不需要让用户手工找日志。

### 2.2 非目标

- 不做 Codex 的启动器、不做插件开发工具、不代理 Codex 的网络请求。
- 不修改 AppX 安装目录、不绕过 Store 更新机制、不做降级安装包。
- 不做账号 / 凭据 / Token 的任何读写。
- 不做跨平台。Windows 专用，明确不支持 macOS / Linux。
- 不自建插件市场、不下载第三方插件。只搬运 Desktop 自带的那份。

---

## 3. 现状与它的三个限制

当前实现：`sync-codex-bundled.cmd` 负责菜单/日志/暂停，逻辑在 `scripts/*.ps1`（check 138 行、sync 306 行、cleanup 140 行、rollback 115 行）。

| 限制 | 具体表现 |
| --- | --- |
| **必须整包下载才能用** | 逻辑分散在 `scripts/`，单拿 exe 或单拿 `.cmd` 都跑不起来；用户经常只下载了 `sync-codex-bundled.cmd` |
| **纯 CLI，无进度** | SHA-256 校验几百个文件时界面静止不动，用户以为卡死；错误需要自己从 PowerShell 堆栈里读 |
| **无崩溃恢复** | 复制中途被强杀/断电，会留下 `openai-bundled.stage-*` 和半交换状态；下次运行只打印"发现上次未完成的 stage"，不修复 |

此外还有两个隐性缺口：只能恢复"最新一个"备份；Edge 的 native messaging 完全没处理。

---

## 4. 技术方案

### 4.1 技术决策

| 项 | 选择 | 理由 |
| --- | --- | --- |
| UI 框架 | WinForms（非 WPF） | 界面复杂度不需要 XAML；启动更快、体积更小、单文件无额外依赖 |
| 目标框架 | `.NET Framework 4.8` | Win10/11 in-box，exe 零运行时依赖 |
| 构建 | in-box `MSBuild.exe`（`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\`） | 本地与 CI 均无需安装 SDK |
| 目标产物 | `CodexPluginSync.exe` ≈ 200–300 KB | 可放 U 盘、可直接发给别人 |
| 提权 | `asInvoker`（不强制管理员） | 现有流程不需要提权；需要时给出明确提示而非直接弹 UAC |
| 进程模型 | 单进程 + 后台工作线程 | 不在运行时依赖 PowerShell 脚本；PS 脚本保留为 legacy 入口 |
| 第三方依赖 | 零（不用 NuGet 包） | 降低供应链与杀软误报风险 |

明确不采用：Electron / Tauri（体积与复杂度不匹配）、Python + PyInstaller（AppX / 注册表 / EFS 场景贴合度差）、PowerShell + `ps2exe` 套壳（仍依赖脚本文件、误报率高）。

### 4.2 关键设计约束

**Core 层只接收显式路径参数，不自己调用 AppX API。**

```csharp
var engine = new SyncEngine(new SyncOptions {
    Root = @"D:\CodexHome",
    SourceBundle = @"C:\Program Files\WindowsApps\...\openai-bundled",
    BackupRoot = ...,
    Progress = ...,
    Log = ...
});
```

原因：现有脚本把 `Get-AppxPackage` 写死在函数内部，导致**无法在没有安装 Codex Desktop 的机器上测试**。把"定位"和"操作"拆开后，CI 可以用 fixture 目录跑完整的 check → sync → rollback → cleanup 链路。

---

## 5. 项目结构

```text
src/CodexPluginSync/
├── Program.cs                  # 入口：参数分派 GUI / CLI，单实例互斥
├── App.manifest                # asInvoker + PerMonitorV2 DPI + Win10 supportedOS
├── Cli/
│   └── CliRunner.cs            # --check --sync --rollback --cleanup-only --export-diag --selftest
├── Core/
│   ├── Paths.cs                # CodexHome 探测、所有子路径集中定义
│   ├── AppxLocator.cs          # WinRT PackageManager 查询，失败回落子进程 Get-AppxPackage
│   ├── CheckEngine.cs          # 只读，产出 StateSnapshot
│   ├── SyncEngine.cs           # 同步事务：stage → verify → swap，进度与取消
│   ├── RollbackEngine.cs
│   ├── CleanupEngine.cs
│   ├── BackupStore.cs          # 备份枚举 / 元数据 / 恢复指定备份
│   ├── NativeHost.cs           # Chrome（Edge 待调研）native messaging 注册
│   ├── Installer.cs            # robocopy / cipher 封装与 EFS 处理（原样移植）
│   ├── Hasher.cs               # SHA-256 批量校验 + 进度
│   ├── ProcessGuard.cs         # 占用进程检测 / 优雅关闭
│   ├── OperationJournal.cs     # 中断恢复
│   ├── Logger.cs               # 日志写入 + 推送给 UI
│   ├── Settings.cs             # %LOCALAPPDATA%\CodexPluginSync\settings.json
│   └── DiagnosticsExporter.cs  # 诊断 zip
├── UI/
│   ├── MainForm.cs             # 三页签：同步 / 备份 / 设置
│   ├── TrayService.cs          # NotifyIcon 常驻与定时检查
│   └── Controls/               # StatusCard、ProgressPanel、LogViewer（自绘）
└── Resources/
    ├── app.ico
    ├── strings.zh-CN.resx
    └── strings.en.resx

build/CodexPluginSync.csproj     # net48，in-box MSBuild 可构建
.github/workflows/build.yml      # windows-latest 构建 + selftest + Release artifact
scripts/*.ps1                    # 保留，标注 legacy
docs/DESIGN.md                   # 本文档
```

---

## 6. 数据模型

### 6.1 `StateSnapshot`（检查结果，也用于 `--json`）

```jsonc
{
  "timestamp": "2026-06-12T14:32:05+08:00",
  "codexHome": "D:\\CodexHome",
  "desktop": { "version": "26.1.2", "installLocation": "...", "found": true },
  "marketplace": {
    "exists": true,
    "needsUpdate": false,
    "desktopPlugins":  [ { "name": "browser", "version": "1.4.0" }, ... ],
    "localPlugins":    [ { "name": "browser", "version": "1.4.0" }, ... ],
    "materialized":    [ { "name": "browser", "version": "1.4.0" }, ... ]
  },
  "caches": [
    { "name": "browser", "expected": "1.4.0", "actual": "1.4.0", "state": "ok" },
    { "name": "chrome",  "expected": "1.4.0", "actual": "1.3.2", "state": "outdated" },
    { "name": "computer-use", "expected": "1.0.1", "actual": null, "state": "missing" }
  ],
  "nativeHost": { "registered": true, "path": "...", "upToDate": false },
  "lockingProcesses": [ { "name": "chrome", "id": 1234 } ],
  "disk": [ { "drive": "D:", "freeBytes": 45210000000 } ],
  "backups": { "count": 1, "newest": "openai-bundled-sync-20260601-101500" },
  "pendingOperation": null,
  "verdict": "update-needed"   // ok | update-needed | blocked | error
}
```

`state` 取值：`ok` / `outdated` / `missing` / `unknown`。`verdict` 是给 UI 和自动化用的单一结论，避免调用方自己推导。

### 6.2 `settings.json`

存放位置：`%LOCALAPPDATA%\CodexPluginSync\settings.json`（**不放在 CodexHome**，否则换 Root 就丢配置）。

```jsonc
{
  "codexHome": "D:\\CodexHome",
  "language": "auto",
  "theme": "auto",
  "runAtStartup": false,
  "autoCheckHours": 6,
  "keepBackups": 1,
  "staleStageHours": 24,
  "keepLogDays": 30,
  "minimumFreeGB": 1,
  "extraLockingProcesses": [],
  "confirmBeforeSync": true
}
```

### 6.3 备份元数据 `backup.json`

写在每个备份目录内，让备份页不必推断：

```jsonc
{
  "kind": "sync",                    // sync | rollback-safety
  "createdUtc": "2026-06-12T06:31:02Z",
  "desktopVersion": "26.0.1",
  "items": [
    { "name": "marketplace", "path": "marketplace.previous", "pluginVersions": [...] },
    { "name": "cache-chrome", "path": "cache-chrome.previous", "pluginVersions": [...] }
  ],
  "nativeHostBackup": "com.openai.codexextension.json.previous",
  "complete": true,
  "sizeBytes": 18342117
}
```

`complete` 由"必需子目录是否齐全"决定；不完整的备份在 UI 里显示为不可恢复，并说明缺哪一项（而不是像现在这样直接抛"备份不完整"后退出）。

### 6.4 操作日志 `pending-operation.json`

崩溃恢复的核心。写入 `CodexHome\pending-operation.json`，同步成功或回滚完成后删除。

```jsonc
{
  "operationId": "20260612-143200",
  "stage": "copying",       // preflight|copying|verifying|swapping|native-host
  "backupRoot": "D:\\CodexHome\\backups\\openai-bundled-sync-20260612-143200",
  "targets": [
    { "kind": "marketplace", "stagePath": "...openai-bundled.stage-20260612-143200", "livePath": "...openai-bundled", "swapped": false },
    { "kind": "cache", "plugin": "chrome", "stagePath": "...", "livePath": "...", "swapped": false }
  ]
}
```

### 6.5 日志格式

沿用现有约定：`CodexHome\logs\sync-openai-bundled-<yyyyMMdd-HHmmss>.log`，逐行带 `[HH:mm:ss]` 前缀。新增结构化字段写在同一行末尾，便于 grep：

```
[14:32:06] phase=verify file=128/260 result=ok
[14:32:07] phase=native-host target=Chrome path=... result=updated
```

---

## 7. 核心流程

### 7.1 检查（只读）

```text
1 定位 AppX（PackageManager）→ 取最低可用版本 / 最高版本，记录 InstallLocation
2 校验必需路径存在（sourceBundle、marketParent、CodexHome）
3 读取 Desktop 侧插件清单（plugins\*\.codex-plugin\plugin.json）
4 读取本地 Marketplace 清单
5 读取 .materialization-key；不存在时回落到 Desktop 的 .agents\plugins\marketplace.json
6 计算 expected = key ∩ desktop（按 name 匹配），与 local 比对
7 逐个读取缓存版本，标记 ok / outdated / missing
8 读 native messaging 注册表，与期望 exe 路径比对
9 枚举占用进程、磁盘可用空间、备份数量、pending-operation
10 输出 verdict
```

**为什么必须用 `.materialization-key` 而不是"AppX 里有什么就同步什么"**：AppX 包里存在但不参与持久化的插件（内部/临时插件）会造成"每次检查都提示需要更新"的假阳性。这一点是现有实现最关键的正确性设计，必须保留。

### 7.2 同步事务（带崩溃恢复）

```text
1 预检    AppX 存在 / 路径可达 / 磁盘空间 ≥ 阈值 / 无占用进程 / EFS 可继承
2 规划    由 StateSnapshot 得出需要动的项（Marketplace + 哪些缓存）
3 记档    写 pending-operation.json（stage=preflight）
4 复制    robocopy → stage 目录；保留 cipher /e → copy /COPY:DAT → cipher /d 的 EFS 处理
5 校验    SHA-256 全量比对（进度按文件数），不一致立即失败且不交换
6 交换    live → 备份目录，stage → live（每个目标单独更新 swapped 标记）
7 收尾    native messaging 路径修正 → 写 backup.json（complete=true）
8 清除    删除 pending-operation.json
```

**关键不变量：**

- 任何一步失败，live 目录要么是"旧版本完整"，要么是"新版本已校验完整"，绝不出现混合状态。
- 交换期间崩溃 → 下次启动由 `pending-operation.json` 判定是回滚还是续做。
- 校验不通过时，stage 目录保留（不删），便于诊断；由清理流程按 `staleStageHours` 回收。

**启动时的恢复分支：**

| `stage` 字段 | 恢复动作 |
| --- | --- |
| `preflight` / `copying` | 提示"上次同步未完成"，一键删除 stage 残留 |
| `verifying` | 同上；stage 未交换，live 未受影响 |
| `swapping` | 逐个 `targets` 检查：有 stage 无 live → 说明已换出，用备份补回；两者都在 → 删除 stage |

### 7.3 回滚

```text
1 列出备份，默认选最新 complete 的
2 完整性校验：必需项（marketplace / 三个缓存中实际被替换过的）齐全
3 建 rollback-before-<stamp> 安全快照
4 交换：当前 live → 安全快照，备份内容 → live
5 native messaging 清单文件一并回滚
6 校验每个插件 manifest 可读
7 任一步失败 → 逆序恢复已移动的项（现有脚本已实现该逻辑，保留）
```

与现状的差异：**支持指定任意备份**（`--backup <name>` / UI 列表选行），而不是只能恢复最新一个。

### 7.4 清理

保持现有安全策略（`Assert-UnderRoot` 的路径越界防护、junction 不递归、只删自己创建的命名模式）：

| 目标 | 规则 |
| --- | --- |
| 空备份目录 | 无条件删除（失败运行的残留） |
| `openai-bundled-sync-*` | 按时间倒序，保留最新 `keepBackups` 个 |
| `rollback-before-*` | 保留最新 1 个 |
| `openai-bundled.stage-*` | 早于 `staleStageHours` 才删（给正在跑的任务留窗口） |
| `sync-openai-bundled-*.log` | 早于 `keepLogDays` 才删 |

日志文件名模式与删除范围必须保持"只匹配自己创建的"，绝不泛删 `CodexHome` 下其他内容。

### 7.5 诊断导出

生成 `CodexPluginSync-diag-<stamp>.zip`：

| 内容 | 说明 |
| --- | --- |
| `logs/` | 最近 5 个日志 |
| `state.json` | `StateSnapshot` 完整快照 |
| `settings.json` | 当前配置（不含凭据；本工具本就无凭据） |
| `registry-native-host.txt` | Chrome（及 Edge，如实现）注册表项与清单内容 |
| `environment.txt` | OS build、.NET 版本、是否管理员、屏幕缩放、区域设置 |
| `README.txt` | 每个文件是什么、如何提交 issue |

**明确不打包**：插件文件内容、AppX 安装目录文件、任何 token / cookie / 用户对话数据。

---

## 8. 界面规格

主窗口约 880×640，可缩放，最小 720×520。三个页签。

### 8.1 页签「同步」

```text
CodexHome  [D:\CodexHome                    ] [浏览] [自动探测 ▾]
────────────────────────────────────────────────────────────
  Codex Desktop       26.1.2 已安装          磁盘 D:  42.1 GB 可用
  Marketplace         3 个插件               需要更新：是
  browser 缓存        1.4.0                  ✓ 已同步
  chrome 缓存         1.3.2                  ✗ 版本落后（期望 1.4.0）
  computer-use 缓存   (缺失)                 ✗ 未安装
  占用进程            Chrome ×3, msedge ×1    ⚠ 需先关闭
────────────────────────────────────────────────────────────
[检查状态] [一键同步] [回滚] [清理] [导出诊断]
▓▓▓▓▓▓▓▓▓▓░░░░░░  验证 SHA-256  128/260 文件
▶ 14:32:05 检测到 Codex Desktop 26.1.2
▶ 14:32:06 Marketplace 需要同步（3 个插件）
   …（可滚动，支持复制全文 / 打开日志文件）
```

交互规则：

- 「需要更新 = 否」时「一键同步」置灰，悬停显示"已是最新（上次同步 2 小时前）"。
- 检测到占用进程时按钮变为「关闭占用程序并同步」，先 `CloseMainWindow` 优雅关闭，超时再提示强制结束。
- 运行中除「取消」外全部禁用；取消后回到 stage 之前的状态。
- 结束显示结果条：`✓ 同步成功，请重启 Codex Desktop` / `✗ 失败：磁盘空间不足（D: 剩 0.4 GB，需要 1 GB）`，附「查看日志」「导出诊断」。
- 日志区固定高度、可折叠，出错时自动展开并定位到第一行失败原因。

### 8.2 页签「备份」

表格列：`时间 / Desktop 版本 / 插件版本 / 大小 / 类型 / 状态`，行操作：`恢复此备份` `打开目录` `删除`。

- 与 `--rollback` 相比的核心增强：可恢复任意备份，不只是最新一个。
- 不完整备份显示为灰行 + 悬停说明缺哪一项（`marketplace.previous` 还是 `cache-chrome.previous`）。
- 恢复前弹出确认，明确写出"当前状态会保存到 `rollback-before-<时间>`"。

### 8.3 页签「设置」

| 设置 | 默认 | 说明 |
| --- | --- | --- |
| 语言 | 跟随系统 | 中文 / English |
| 主题 | 跟随系统 | 深色模式需 `DwmSetWindowAttribute(20)` 让标题栏一起变暗 |
| 开机启动托盘常驻 | 关 | opt-in，写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| 自动检查间隔 | 6 小时 | 0 = 关闭 |
| 备份保留数量 | 1 | 对应 `keepBackups` |
| 过期 stage 清理阈值 | 24 小时 | |
| 日志保留天数 | 30 | |
| 最小可用磁盘 | 1 GB | |
| 额外占用进程名 | 空 | 实测发现 Codex 本体进程名后由用户补充 |
| 同步前确认 | 开 | |

---

## 9. 托盘与自动检查

- `NotifyIcon` 常驻，右键菜单：`打开主窗口 / 立即检查 / 退出`。
- 定时器：启动后延迟 2 分钟首检，之后每 `autoCheckHours` 小时；主窗口打开时不弹通知，只更新状态。
- 发现 `verdict = update-needed` 时弹**气泡通知**（`ShowBalloonTip`）。net48 上的 WinRT Toast 需要额外组件，气泡通知更稳且免依赖。
- 气泡点击 → 打开主窗口并自动执行检查；不自动执行同步（写入操作必须由人确认）。
- 默认不随系统启动；开启后在设置页显示注册表位置，便于用户自查与关闭。

---

## 10. CLI

GUI 与 CLI 是同一个 exe，参数分派，互不干扰：

```text
CodexPluginSync.exe                                  # 无参数 → 打开 GUI
CodexPluginSync.exe --check [--json]                  # 只读
CodexPluginSync.exe --sync [--force] [--quiet]        # 同步
CodexPluginSync.exe --rollback [--backup <name>]      # 回滚（默认最新完整备份）
CodexPluginSync.exe --cleanup-only                    # 只清理
CodexPluginSync.exe --export-diag [--out <path>]      # 导出诊断包
CodexPluginSync.exe --selftest --fixture <dir>         # 无 Codex 环境下的端到端自检
CodexPluginSync.exe --version
```

退出码（沿用现有语义）：`0` 成功 / `1` 失败 / `2` 仅 `--check` 表示"需要更新"。

`--json` 输出 `StateSnapshot`，供自动化解析，替代现在靠 grep `Update needed: Yes` 的做法。

现有 `sync-codex-bundled.cmd` 与 `scripts/*.ps1` **保留**，标注为 legacy，内部改为调用 exe（找不到 exe 时回落到脚本），保证老用户的批处理与计划任务不中断。

---

## 11. 失败场景矩阵

| 场景 | 检测方式 | 用户可见行为 |
| --- | --- | --- |
| Codex Desktop 未安装 | `PackageManager` 查不到 `OpenAI.Codex` | "未检测到 Codex Desktop（Microsoft Store 版）"，同步按钮禁用，给 Store 链接 |
| 磁盘不足 | `DriveInfo.AvailableFreeSpace < 阈值` | 写明哪个盘、差多少，附"打开存储设置" |
| 文件被占用 | 进程名匹配 | 列出进程名 + PID，提供「关闭并继续」 |
| CodexHome 不存在 | 路径检验 | 提示创建，或让用户换目录 |
| CodexHome 在不可加密卷 | 交换后复查 EFS 属性失败 | "该卷不支持加密属性"，建议换盘（现有 E 盘问题即属此类） |
| 校验不一致 | 哈希不符 | 列出前 5 个不符文件 + 「回滚」按钮 |
| AppX 内部路径变更 | `sourceBundle` 不存在 | 显示实际探测到的路径，提示可能为版本变更，附 issue 链接 |
| 用户取消 | `CancellationToken` | "已取消，状态已回滚" |
| 崩溃残留 | 启动时发现 `pending-operation.json` | 按 7.2 的分支给出"清理 / 回滚"选项 |
| 权限不足 | 复制时 `UnauthorizedAccessException` | 说明需要读取 AppX 目录的权限，给出排查步骤（不直接提权） |

---

## 12. 构建与发布

本地（无需安装 SDK）：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" `
    .\build\CodexPluginSync.csproj /p:Configuration=Release
```

产出：`build\bin\Release\CodexPluginSync.exe`（单个文件）。

CI（`.github/workflows/build.yml`）：

```text
windows-latest → msbuild Release → --selftest --fixture ./test/fixture
             → 上传 artifact → tag 触发 Release 附带 exe
```

版本号写入 `AssemblyInfo`，GUI 标题栏与「关于」显示，`--version` 输出。不做代码签名（可后续补），README 中说明可能出现"未知发布者"的 SmartScreen 提示并给出校验方式（提供 SHA-256）。

---

## 13. 测试策略

| 层 | 方式 |
| --- | --- |
| Core 引擎 | 用 fixture 目录（伪造的 `CodexHome` + 伪造的 `openai-bundled`）跑 check/sync/rollback/cleanup，校验文件数与哈希 |
| 崩溃恢复 | fixture 中预置各种 `pending-operation.json` 的 `stage` 取值，断言恢复分支正确 |
| 路径安全 | 断言清理逻辑拒绝 `CodexHome` 之外的路径、不递归 junction |
| 失败注入 | 只读目标目录 / 磁盘配额不足 / 哈希篡改，断言 live 目录未被破坏 |
| UI | 手动清单：DPI 125%/150%、深色模式、中英文、窗口最小尺寸下的截断 |

`--selftest --fixture <dir>` 即把上面所有非 UI 断言串成一个命令，CI 与本地共用。

---

## 14. 里程碑

| 阶段 | 内容 | 可交付 |
| --- | --- | --- |
| M1 | Core 骨架（Paths / AppxLocator / CheckEngine / Logger）+ 「同步」页只读状态 + `--check --json` | 图形版替代 `--check` |
| M2 | SyncEngine（stage / verify / swap / journal、进度、取消）+ ProcessGuard + 失败矩阵 | 图形版替代 `--sync`，比脚本更安全 |
| M3 | RollbackEngine / CleanupEngine + 备份页 + 恢复任意备份 | 取代 `--rollback` / `--cleanup-only` |
| M4 | 设置页 + 托盘 + 自动检查 + 诊断导出 | 完整形态 |
| M5 | 构建 / CI / README 重写 / 截图 + `--selftest` fixture | 可发布 v1.0 |

---

## 15. 风险与未决问题

| # | 风险 | 应对 |
| --- | --- | --- |
| 1 | **EFS 处理是踩出来的路**：`cipher /e` 父目录 → `robocopy /COPY:DAT` → `cipher /d` → 复查无 Encrypted 属性。**不可用 `File.Copy` 简化**，否则 E: 这类盘会失败 | 原样移植该序列，保留"复制后复查 Encrypted 属性"的断言，并加注释说明原因 |
| 2 | 占用进程名是写死的假设（`chrome, msedge, ChatGPT, extension-host`），Codex Desktop 本体进程名需实测 | 设置页允许用户补充进程名；未命中时只警告不阻断 |
| 3 | 现有实现只处理 Chrome 的 native messaging 注册表，Edge 是否需要未验证 | 列为调研项，实测后再加；不臆造注册表路径 |
| 4 | AppX 内部路径（`app\resources\plugins\openai-bundled`）可能随版本变化 | 定位失败时输出实际探测路径 + 手动指定源目录的隐藏参数 |
| 5 | 杀软 / EDR 对"复制到 AppX 外 + 写 HKCU 注册表"敏感 | manifest 不请求提权、不加壳、不用 UPX；README 说明写入范围 |
| 6 | 与 OpenAI 行为变化冲突（未来可能自己重物化） | `--check` 长期返回"不需要更新"即为失效信号；README 注明项目寿命预期 |
| 7 | WinForms 高 DPI 与深色标题栏在 4.8 上需手工处理 | 用 `PerMonitorV2` manifest + `DwmSetWindowAttribute`，M4 前在三种缩放下验证 |

---

## 16. 词汇表

| 术语 | 含义 |
| --- | --- |
| AppX / MSIX | Windows 的打包应用格式，Codex Desktop 以它分发 |
| bundled plugin | Desktop 安装包自带的内置插件（`openai-bundled` 目录） |
| materialize | Codex 把内置插件复制到用户目录使其可被加载的过程 |
| `.materialization-key` | Codex 记录的"本次实际物化了哪些插件 / 哪个版本"，比 AppX 内容更权威 |
| stage 目录 | 复制过程中的临时目录（`*.stage-<时间戳>`），校验通过后才换入正式位置 |
| native messaging host | Chrome 扩展通过标准输入输出通信的本机程序（`extension-host.exe`） |
| EFS | Windows 加密文件系统；AppX 内的 `Application Protected` 文件带 EFS 属性，直接复制会失败或留下加密副本 |
