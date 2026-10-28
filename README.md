# Unity Auto Test MVP

本工作区包含一个可直接导入 Unity 项目的 Editor 插件：**[UnityAutoTestMVP](UnityAutoTestMVP/)**。

- **用途**：项目收尾阶段的基础自动化检测——静态扫描场景与缺失脚本、对当前场景执行基础 Play Mode 冒烟测试、生成 JSON/Markdown 报告。
- **环境**：Unity 2021.3 LTS 或更高，Editor-only，无运行时组件、无第三方依赖、无外部上传。
- **安装**：把 `UnityAutoTestMVP` 文件夹复制到目标项目的 `Assets/` 下（形成 `Assets/UnityAutoTestMVP/Editor/...`），或在 Package Manager 中通过 “Add package from disk...” 选择 `UnityAutoTestMVP/package.json`。
- **打开**：`Tools > Unity Auto Test MVP`。
- **完整文档**：[UnityAutoTestMVP/README.md](UnityAutoTestMVP/README.md)（安装、首次使用流程、报告模型、限制、FAQ、验证记录）。
- **已验证版本**：Unity 2021.3.45f2c1、Unity 6000.0.79f1（Windows）；详见文档“验证记录”。

> 基础冒烟测试只验证“场景能进入 Play Mode 且运行期间没有捕获目标错误日志”，**不等价于完整游戏功能测试**，不会点击 UI 或验证玩法。
