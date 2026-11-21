# ToDooo

[English](README.md) · [简体中文](README.zh-CN.md)

轻量级 Windows 11 桌面待办与日历工具（WinUI 3 / .NET 11 / SQLite），所有任务与设置均保存在本机。

## 功能特性

- **快速输入与自然语言解析**：主添加栏支持自动识别日期时间（如 `明天下午3点`、`周五 10:30`）、重复周期（`每天` / `每周` / `每月`）及重要标记（`!重要` / `!!`）；子任务快速添加同样支持识别截止时间。
- **贴边日历工具窗**：独立无边框亚克力日历窗紧贴主窗口一侧（`6px` 间距）展开，支持靠近磁吸、双向拖动跟随与高度同步缩放。
  - **月 / 日连续视图**：月视图（18 周连续滚动网格）与日视图（纵向按天横排日程流）均支持连续平滑滚动，跨月跨周同屏浏览、顶栏标题自动联动。
  - **拖拽改期与直接新建**：支持主列表、日历格子与无截止日期区之间互拖改期；双击日历任意日期可直接新建待办。
  - **右侧抽屉与小结**：可收起右侧抽屉（同步缩放日历窗宽），提供「隐藏已完成」开关与「本周已完成」汇总条。
- **顶部自动收边**：将主窗口（或随主窗口一起）拖到屏幕顶部边缘松手即可收成 `6px` 顶部薄条并置顶，鼠标悬停即刻同步呼出。
- **子任务与备注**：主列表展开子任务并支持独立备注、截止时间、拖动排序与右键「编辑…」弹窗；全部子任务勾完时提示是否同步完成主任务。
- **筛选视图与批量操作**：支持「全部 / 进行中 / 重要 / 已完成」及启动默认标签设置、可自定义启用与排序的智能视图（今天 / 明天 / 本周 / 已逾期）、8 色自定义标签、实时搜索，以及在已逾期视图一键「全部顺延到今天」。
- **桌面提醒与全局热键**：支持提前 0–60 分钟的 Windows 系统通知，通知卡片直接提供「标记完成」与「推迟 10 分钟」按钮；支持自定义全局呼出与「快速记一条」热键、托盘菜单、开机自启与 JSON 导入导出。
- **亚克力外观**：跟随系统深浅色实时切换、`0%–90%` 透明度调节、桌面亚克力磨砂与离窗自动变淡。

## 运行与构建环境

- **运行环境**：Windows 11 22H2（22621）及以上，`x64`（自包含发布包无需额外安装运行时）
- **源码构建**：.NET 11 SDK

## 构建、测试与发布

```powershell
# 编译（Debug）
dotnet build src/ToDooo.App/ToDooo.csproj -p:Platform=x64

# 运行单元测试
dotnet test tests/ToDooo.Tests/ToDooo.Tests.csproj

# 发布自包含 Release 版本
dotnet publish src/ToDooo.App/ToDooo.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64

# 一键发版：同步版本号（csproj + manifest）→ 跑测试 → 发布 → 打包 ZIP
pwsh scripts/release.ps1 -Version 1.4.0
```

版本号只维护 `ToDooo.csproj` 一处，设置页「关于」在运行时读取程序集版本。推送 `v*` 标签会触发 `.github/workflows/release.yml`：自动跑测试、打包 `ToDooo-<版本>-win-x64.zip`（附 SHA256）并发布 GitHub Release。

## 目录结构

```text
src/ToDooo.App/
├── MainWindow.xaml(.cs)         # 主窗口生命周期、全局热键、消息循环与日历/设置宿主
├── MainWindow.Tray.cs           # 托盘图标与菜单、撤销条、JSON 导入导出、通知动作回调
├── MainWindow.Dock.cs           # 边缘缩放与顶部共同收边/呼出逻辑
├── MainWindow.Filters.cs        # 搜索栏、标签页筛选、颜色标签、智能视图与快捷键
├── MainWindow.Todos.cs          # 快速添加、任务卡片、右键菜单与子任务交互
├── Models/                      # TodoItem、SubtaskItem、SmartView 数据模型
├── Services/                    # SQLite 仓储、设置服务、提醒调度、系统通知与主题色板
├── ViewModels/                  # MainViewModel 列表筛选、排序与持久化调度
├── Views/                       # 日历宿主窗、日历控件、设置窗口、待办与子任务编辑窗口
├── Helpers/                     # 自然语言日期解析、x:Bind 转换、DPI 缩放辅助
├── Interop/                     # Win32 互操作、亚克力控制、DWM 防黑帧遮罩、窗口磁吸与托盘
└── Themes/                      # 全局主题画刷与控件样式
tests/ToDooo.Tests/              # xUnit 单元测试（模型、ViewModel、仓储、自然语言解析）
```

## 本地数据

数据保存在 `%LocalAppData%\ToDooo\`：

- `todo.db` — SQLite 数据库（待办、子任务及备注）
- `settings.json` — 外观主题、透明度、默认标签、热键、自定义标签名、智能视图与窗口尺寸位置

覆盖替换程序目录即可升级，不影响本地已有数据。

## 许可

[MIT](LICENSE) © 2026 yyt
