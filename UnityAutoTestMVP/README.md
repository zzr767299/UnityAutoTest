# Unity Auto Test MVP

面向 Unity 项目收尾阶段的基础自动化检测工具。第一版提供两件事：

1. **项目静态扫描**：扫描场景资源，报告无效场景、加载失败的场景，以及所有 GameObject（包括非激活对象）上丢失的脚本（Missing Script）。
2. **运行时基础冒烟测试**：自动进入 Play Mode，运行 5–120 秒，收集 Error / Exception / Assert（可选 Warning）日志，自动退出，并生成结构化报告。

工具完全运行在 Unity Editor 本地，不上传任何项目代码、场景或日志，不依赖 AI、云服务或 Unity Test Framework。

> **重要：** 冒烟测试只验证“场景能进入 Play Mode 且运行期间没有目标错误日志”，**不等价于完整游戏功能测试**。它不会点击 UI、模拟输入、探索关卡或验证玩法规则。

---

## 环境要求

- Unity **2021.3 LTS 或更高版本**（Editor 专用）。
- 已在 **Unity 2021.3.45f2c1** 与 **Unity 6000.0.79f1** 上完成编译与 98 项自动化检查（详见文末“验证记录”）。
- 不需要安装任何额外包，也不修改项目的 `Packages/manifest.json`。

工具全部代码位于 `Editor` 目录 / `Editor` 程序集，不会进入玩家构建。

---

## 安装

### 方式 A：作为项目内插件（推荐）

把整个 `UnityAutoTestMVP` 文件夹复制到 Unity 项目的 `Assets` 目录，形成：

```text
Assets/
  UnityAutoTestMVP/
    Editor/
      UnityAutoTestWindow.cs
      Core/...
      UnityAutoTestMVP.Editor.asmdef
```

Unity 会自动编译，菜单 `Tools/Unity Auto Test MVP` 随即出现。

### 方式 B：作为本地 UPM 包

在 Unity 的 **Window > Package Manager > + > Add package from disk...** 中选择本文件夹下的 `package.json`。

> 不要把同一个插件同时放在 `Assets/` 和 `Packages/` 中，否则会出现重复类型/重复程序集错误。

### 卸载

删除对应的插件文件夹（方式 A）或在 Package Manager 中移除包（方式 B）。工具不会在场景或游戏代码中留下任何组件。

---

## 打开工具

菜单：**Tools > Unity Auto Test MVP**

窗口包含：

- 项目信息：Unity 版本、项目名称、当前活动场景、是否正在编译/导入、最近一次监听到的编译状态、最近扫描/冒烟结果。
- 测试设置：冒烟测试时长（5–120 秒）、是否将 Warning 纳入报告、是否扫描 Assets 下全部场景。
- 操作按钮：扫描项目、运行冒烟测试、停止测试、导出测试报告。
- 测试统计：检查项总数、通过、失败、警告、未执行、已取消。
- 检查明细：按状态着色，可展开错误消息、堆栈、修复建议。
- 说明与限制：每份报告都附带捕获范围与局限说明。

不可执行的操作会被禁用并显示原因（编译中、Play Mode 中、场景未保存、存在未保存修改等）。

---

## 第一次使用的完整流程

1. **保存当前场景。** 工具不会替你保存场景；存在未保存修改时，冒烟测试会被阻止，避免触发 Unity 的保存弹窗。
2. 点击 **扫描项目**。
   - 默认扫描 **Build Settings 中启用的场景**。
   - 如果没有任何启用场景，自动回退为扫描 `Assets` 下所有 `.unity` 场景。
   - 也可以勾选“扫描 Assets 下全部场景”。
3. 查看统计与明细。每个丢失脚本会给出场景路径、完整层级路径（如 `EnemyRoot/InactiveChild`）和修复建议。
4. 打开要测试的场景，确认已保存，设置冒烟测试时长和 Warning 选项，点击 **运行冒烟测试**。
   - 工具会自动进入 Play Mode，到达设定时长后自动退出。
   - 运行期间可点击 **停止测试**；主动停止会被记录为 **已取消**，不会被当作通过。
5. 测试结束后查看报告，点击 **导出测试报告** 同时导出 JSON 与 Markdown。
6. 报告默认写入：`<项目根目录>/Library/UnityAutoTestMVP/Reports/`。
   - 每次运行结束会自动保存一份 JSON。
   - “导出测试报告”会额外生成带时间戳的 JSON + Markdown，不覆盖历史文件。

---

## 功能一：项目静态扫描

### 场景发现

- 默认使用 `EditorBuildSettings.scenes` 中 `enabled == true` 的场景。
- 没有启用场景时，使用 `AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })` 回退扫描全部场景。
- 场景路径去重、排序，保证结果稳定。

### 每个场景的检查项

| 检查 | 说明 |
| --- | --- |
| 场景资源是否有效 | 通过 `AssetDatabase.LoadAssetAtPath<SceneAsset>` 校验 |
| 场景是否能加载 | 未打开的场景以 `OpenSceneMode.Additive` 临时加载 |
| 是否存在丢失脚本 | 遍历场景内所有 GameObject（含根节点、子节点、非激活对象） |
| 是否完成扫描 | 只有真正完成检查才标记为通过 |

丢失脚本使用 Unity 官方 API `GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(GameObject)` 检测。

### 安全保证

- **不删除**缺失脚本组件，**不自动修复**，**不保存**任何场景。
- 已打开的场景**就地扫描**，不会被关闭或保存。
- 未打开的场景以 **Additive** 方式临时加载，并在 `finally` 中关闭；扫描结束后恢复原活动场景。
- 扫描前记录场景 dirty 状态、场景数量和活动场景，验证结束后保持不变（已通过自动化验证）。
- 单个场景失败会被记录为失败/未执行，**不会中断其他场景的扫描**。
- 场景加载失败时，相关检查标记为“未执行”，不会误报为通过。
- 所有异常都会写入报告和 Console，不会被空 `catch` 吞掉（仅临时文件清理这类非关键路径有明确注释）。

### 扫描范围之外

- 不单独打开 Prefab 资源文件；Prefab 实例上的缺失脚本会随场景实例被检测到，但 Prefab 资源本身未被修改或单独扫描。
- 不检查材质、ScriptableObject、项目设置等内容（第一版范围）。

---

## 功能二：运行时基础冒烟测试

### 启动前检查

点击“运行冒烟测试”时会依次检查，任何一项不满足都会阻止启动并记录一份 **未执行（NotRun）** 报告：

- Unity 正在编译脚本或导入资源；
- 已处于 Play Mode / 正在切换；
- 批处理模式（batchmode）；
- 活动场景无效、未保存（untitled）或文件不存在；
- 任意已加载场景有未保存修改（避免 Unity 的保存弹窗）；
- 最近一次工具监听到的编译结果是失败。

### 运行流程

1. 记录任务 ID、场景路径、开始时间和配置信息到 `SessionState`。
2. 请求进入 Play Mode。
3. 进入 Play Mode 后开始计时，并开始捕获日志。
4. 到达设定时长（5–120 秒）后自动请求退出 Play Mode。
5. 在 `ExitingPlayMode`（Domain Reload 之前）汇总日志、生成报告并自动保存 JSON。
6. 用户主动停止、手动退出 Play Mode、关闭编辑器，都会安全结束并标记为 **已取消**。
7. 如果请求退出 Play Mode 后 15 秒仍未退出，工具会先结束测试记录并提示手动退出，避免测试状态永久挂起。

### 日志捕获

- 使用 `Application.logMessageReceivedThreaded`，带锁缓冲，避免多线程日志丢失/损坏。
- 默认纳入：`LogType.Error`、`LogType.Exception`、`LogType.Assert`。
- `LogType.Warning` 可在面板中开关（默认纳入）。
- 普通 `Log` 不纳入报告。
- 每条记录包含：时间、类型、消息、堆栈、场景路径、所属任务 ID（报告级）。
- 最多缓冲 2000 条，超出会在报告中说明截断。
- **捕获窗口：从点击“运行冒烟测试”、请求进入 Play Mode 开始，到退出 Play Mode 结束。** 因此场景 `Awake` / 初始化阶段、`EnteredPlayMode` 之前产生的运行时错误也会被捕获。
- 只统计本次测试期间的日志，**不会把历史 Console 日志计入**。

已知日志局限：

- 进入 Play Mode 触发的 Domain Reload 之前的日志可能丢失；Domain Reload 之后的初始化日志会被捕获。
- 测试期间如果发生脚本编译/Domain Reload，重载之前的日志可能丢失。
- Unity 可能对同一问题输出多条日志，报告按条数统计，不自动去重。
- 工具无法确定每条日志的具体业务来源。

### 状态管理与 Domain Reload

实现位于 `Editor/Core/AutoTestSession.cs`：

- 所有跨 Play Mode 的状态写入 `UnityEditor.SessionState`，普通 `static` 字段不作为唯一状态来源。
- `[InitializeOnLoad]` 静态构造函数在每次 Domain Reload 后重新订阅事件。
- 静态 `_hooked` / `_logSubscribed` 标志防止关闭 Domain Reload 时重复订阅。
- 计时基于 `SessionState` 中保存的 UTC ticks，Domain Reload 后继续。
- 报告在 `ExitingPlayMode` 回调中生成（此时静态日志缓冲仍然存在，早于退出 Play Mode 触发的 Domain Reload）。
- `EnteredEditMode` 作为兜底，避免状态遗留。
- 用户手动退出 Play Mode：插件收到 `ExitingPlayMode` 并按 **已取消** 处理。
- 如果 20 秒内无法进入 Play Mode（例如编译错误、脚本异常），测试被标记为 **未执行** 并说明原因。
- 暂停（Pause）不会停止计时。

---

## 功能三：编译状态与编译信息

- 面板始终显示 `EditorApplication.isCompiling` / `isUpdating`。
- 工具使用 `CompilationPipeline.compilationStarted`、`assemblyCompilationFinished`、`compilationFinished` 记录 **工具启动之后** 的编译结果。
- 结果编码后写入 `SessionState`，因此编译触发的 Domain Reload 不会丢失。
- 记录内容：错误/警告、程序集路径、文件、行号、列号、消息。
- 编译错误使用 `CompilationError` 类别，运行时错误使用 `RuntimeError` 类别，两者不会混淆。
- 报告中的编译状态分为：通过 / 有警告 / 失败 / 未知（未监听到）。
- **无法读取 Unity Console 中的历史编译错误**；仅根据 `isCompiling == false` 也不能证明编译通过。工具在没有任何编译事件时明确标记为“未监听到编译结果”。

---

## 功能四：测试报告

### 数据模型

`Editor/Core/TestReportModels.cs`：

```text
TestReport
  TaskId / ToolVersion / UnityVersion / ProjectName / ProjectPath
  ScenePath / TestType / OverallStatus
  StartedAtUtc / FinishedAtUtc / DurationSeconds
  Summary { Total, Passed, Failed, Warning, NotRun, Cancelled }
  Issues: List<TestIssue>
  Notes: List<string>
```

```text
TestIssue
  Id / Category / Severity / Status
  Title / Description
  ScenePath / GameObjectPath
  Message / StackTrace
  Suggestion
```

- 状态：`Passed`、`Failed`、`Warning`、`NotRun`、`Cancelled`。
- 问题类别区分：场景资源、场景加载、丢失脚本、场景扫描、运行时错误、运行时警告、编译错误、编译警告、编译状态、测试环境。
- 不把所有结果拼成一个字符串；JSON 可被新的分析、AI 模块或测试用例体系复用。

### 文件

- 默认目录：`Library/UnityAutoTestMVP/Reports/`
- 文件名：`yyyyMMdd_HHmmss_fff_ProjectScan|SmokeTest_<taskId8>.json/.md`
- 同一报告重复导出会自动追加 `_2`、`_3` 序号，避免覆盖历史。
- 测试结束自动保存 JSON；点击“导出测试报告”同时导出 JSON 与 Markdown。
- 报告自动保存失败会在面板 HelpBox 与 Console 中显示明确错误；手动导出失败还会弹出对话框。
- `Library/` 目录可能被 Unity 清理；需要长期保存时请手动导出/复制。

---

## 状态语义

| 状态 | 含义 |
| --- | --- |
| Passed | 检查完成且没有发现问题 |
| Failed | 检查完成并发现问题（如缺失脚本、运行时 Error） |
| Warning | 检查完成但存在警告（如编译警告、纳入报告的 Warning 日志） |
| NotRun | 检查未执行或被阻断，**不能视为通过** |
| Cancelled | 用户主动停止、手动退出 Play Mode 或编辑器关闭，**不能视为通过** |

冒烟测试总体状态：

- 捕获 Error / Exception / Assert → **Failed**
- 仅捕获已纳入的 Warning → **Warning**
- 无目标错误且正常跑完 → **Passed**
- 用户取消 / 手动退出 / 编辑器关闭 → **Cancelled**
- 无法进入 Play Mode → **NotRun**

---

## 目录结构

```text
UnityAutoTestMVP/
├── package.json                       # 可选 UPM 元数据
├── README.md
└── Editor/
    ├── UnityAutoTestMVP.Editor.asmdef # 仅 Editor 平台
    ├── UnityAutoTestWindow.cs         # EditorWindow UI
    └── Core/
        ├── AutoTestConstants.cs       # 常量与 SessionState 键
        ├── AutoTestEnvironment.cs     # 项目信息、EditorPrefs 设置、报告历史
        ├── AutoTestJson.cs            # 无依赖 JSON 读写
        ├── AutoTestSession.cs         # 跨 Domain Reload 状态机、日志捕获
        ├── CompilationModels.cs       # 编译消息模型与 SessionState 编解码
        ├── CompilationMonitor.cs      # 编译事件监听
        ├── ProjectScanner.cs          # 项目/场景发现与报告汇总
        ├── ReportExporter.cs          # JSON / Markdown 导出
        ├── SceneScanner.cs            # 单场景加载、遍历、缺失脚本检测
        ├── SmokeTestRunner.cs         # 冒烟测试启动检查与报告构建
        └── TestReportModels.cs        # 报告数据模型
```

---

## 已知限制（请务必阅读）

1. **冒烟测试不是功能测试。** 没有 UI 自动化、输入模拟、关卡探索，也不验证业务规则。
2. **不读取历史日志。** 只统计本次测试期间捕获的日志；请求进入 Play Mode 之前的日志不计入；进入 Play Mode 触发的 Domain Reload 之前的日志、以及测试期间脚本重载之前的日志可能丢失。
3. **编译信息不完整。** 只能记录工具启动之后的编译事件；Console 历史编译错误不可读取；`isCompiling` 不能证明编译通过。
4. **场景扫描范围有限。** 只扫描场景；Prefab 资源本身不会被单独打开。
5. **大项目耗时。** 扫描会逐个 Additive 加载场景，场景很大时耗时明显，但可随时取消。
6. **批处理模式不支持冒烟测试。** `-batchmode` 下无法可靠进入交互式 Play Mode，工具会直接阻止并说明。
7. **编辑器崩溃/断电会丢失状态。** `SessionState` 不跨编辑器重启；正在运行的测试不会留下完整报告。
8. **多编辑器实例同时操作同一项目不在支持范围内。**
9. **报告位于 Library。** 清理 Library 会删除报告，请及时导出。
10. **未验证所有 Unity 版本与平台。** 见下方验证记录。

---

## 常见问题

**菜单没有出现？**

- 检查 Console 是否有编译错误。
- 确认插件位于 `Assets/.../Editor/`（或作为 UPM 包安装），且 `UnityAutoTestMVP.Editor.asmdef` 的 `includePlatforms` 为 `Editor`。
- 尝试 `Assets > Refresh`。

**“运行冒烟测试”按钮是灰的？**

窗口下方会显示具体原因：编译中、导入中、Play Mode 中、场景未保存、场景文件不存在、存在未保存修改、最近一次编译失败、已有测试运行等。

**扫描后没有发现我已知的缺失脚本？**

- 缺失脚本必须在场景实例上；单独损坏的 Prefab 资源不会被扫描。
- 报告会显示已扫描的场景和 GameObject 数量；确认目标场景在扫描范围内（Build Settings 或“全部场景”）。
- 如果脚本 GUID 仍能解析到某个程序集，Unity 可能不会将其视为 Missing Script。

**测试结束但没有捕获到错误？**

- 确认错误发生在进入 Play Mode 之后、测试结束之前。
- 检查是否只捕获了 Warning 且未勾选“将 Warning 纳入报告”。
- 报告中的“日志捕获范围”说明了限制。

**报告导出失败？**

- 检查 `Library/` 是否可写、磁盘是否已满、文件是否被其他程序占用。
- 错误会显示在面板 HelpBox、Console 和弹窗中。

---

## 验证记录

验证脚本仅存在于临时工程，不随插件交付。

### Unity 2021.3.45f2c1（Windows）

- 插件编译通过。
- **98 项自动化检查全部通过**，覆盖：
  - 场景扫描、Build Settings 启用/未启用场景、全场景回退；
  - 根节点、子节点、**非激活子节点**上的 Missing Script 检测；
  - 无效场景路径的失败隔离；
  - 扫描前后场景数量、活动场景、dirty 状态、场景文件 SHA256、Build Settings 文件 SHA256 均未变化；
  - JSON/Markdown 导出、JSON 往返、唯一文件名；
  - 编译消息经 SessionState 编解码（含中文、`|`、换行）；
  - 冒烟报告的 Passed / Failed / Warning / NotRun / Cancelled 判定；
  - 取消、重复停止、batchmode 阻止；
  - 日志截断、程序集重载提示。
- **真实 Play Mode 端到端验证**（GUI Editor）：
  - **自动运行**：配置 5 秒；场景脚本在进入 Play Mode 的初始化阶段输出 Error + Warning；工具捕获了两条日志（含 `EnteredPlayMode` 之前的日志和堆栈），在 5.0 秒自动退出 Play Mode；报告总体 **Failed**（失败 1 / 警告 1），编辑器回到 Edit Mode。
  - **用户主动停止**：运行约 1.5 秒后停止；报告 **Cancelled**，包含“用户主动停止测试”，已捕获的错误仍保留在明细中。
  - **用户手动退出 Play Mode**：报告 **Cancelled**，包含“用户手动退出 Play Mode”。
  - **菜单与窗口**：`EditorApplication.ExecuteMenuItem("Tools/Unity Auto Test MVP")` 成功打开窗口，标题与尺寸正确，OnGUI 未产生异常日志。

### Unity 6000.0.79f1（Windows）

- 插件编译通过，无编译器警告。
- 同一套 **98 项自动化检查全部通过**。

### 未验证

- 窗口的视觉风格与排版未做逐像素人工检查。
- 其他 Unity 版本、平台、玩家构建/真机、UI 自动化、玩法正确性、性能分析。

---

## 版本

1.0.0 · Editor-only · 无运行时依赖
