# ToDooo

钉在桌面层的 Windows 待办小组件（WinUI 3 / .NET 11 / SQLite），数据全部保存在本机。

## 功能

- 桌面层钉住：窗口位于应用窗口之下、桌面之上，不挡其他软件；可在右上角菜单切换为普通窗口。
- 边缘停靠：拖到屏幕左/右/上边缘自动收进去，只留一条薄边；悬停薄边呼出并置顶。
- 自然语言时间：新建时直接写“明天下午三点打网球”“周五10点汇报”，自动识别截止时间。
- 子任务：卡片行内展开，可勾选、右键删除、拖动排序；编辑弹窗可增删改。
- 标签与重要：颜色圆点筛选、星标标记重要。
- 智能视图：今天 / 明天 / 本周 / 已逾期，可在设置中启用与排序。
- 提醒：到期通知（可提前 5–60 分钟），通知与右键菜单均支持“推迟 10 分钟”。
- 外观：跟随系统主题（实时切换）、透明度 10%–100%、自动隐藏、亚克力磨砂。
- 其他：搜索、拖动排序、撤销条、删除与清空二次确认、开机自启、托盘菜单、JSON 导出。

## 环境

- Windows 11 22H2（22621）及以上，x64
- .NET 11 SDK

## 构建与运行

```bash
dotnet build src/ToDooo.App/ToDooo.csproj -c Release
dotnet run --project src/ToDooo.App/ToDooo.csproj -c Release
```

## 测试

```bash
dotnet test tests/ToDooo.Tests/ToDooo.Tests.csproj
```

## 发布

自包含单目录发布（无需安装运行时）：

```bash
dotnet publish src/ToDooo.App/ToDooo.csproj -c Release -r win-x64 -o C:/apps/ToDooo
```

便携包：把发布目录下的 `ToDooo/` 连同 `使用说明.txt` 打成 ZIP 即可，中文文件名需 UTF-8（7-Zip 加 `-mcu=on`）。

## 目录

```
src/ToDooo.App/       应用（WinUI 3）
src/ToDooo.App/Models        数据模型
src/ToDooo.App/Services      仓储、设置、提醒、主题
src/ToDooo.App/ViewModels    列表与筛选逻辑
src/ToDooo.App/Views         编辑弹窗、设置
src/ToDooo.App/Interop       窗口层级、亚克力、托盘
tests/ToDooo.Tests/   单元测试（模型 / 视图模型 / 仓储 / 日期解析）
```

## 数据

`%LocalAppData%\ToDooo`：`todo.db`（待办、子任务）与 `settings.json`（主题、透明度、视图等）。程序目录可直接覆盖升级，不影响数据。