# ToDooo

[English](README.md) · [简体中文](README.zh-CN.md)

A lightweight Windows 11 desktop todo and calendar companion built with **WinUI 3**, **.NET 11**, and **SQLite**. All tasks, subtasks, and settings are stored locally.

## Features

- **Quick Capture & Natural Language**: Add tasks inline or via dedicated editor with automatic parsing for dates/times (e.g., `明天下午3点`, `周五 10:30`), recurrence (`每天` / `每周` / `每月`), and priority (`!重要` / `!!`). Natural date/time parsing also works when quick-adding subtasks.
- **Docked Calendar Companion**: Side-by-side borderless acrylic calendar window that snaps next to the main window (`6px` gap), follows dragging, and syncs height resizing.
  - **Month & Day Views**: Continuous scrolling across months (18-week rolling grid) and a vertical day-by-day agenda stream (`Day` view) with dynamic header range tracking.
  - **Drag & Drop Scheduling**: Drag tasks or subtasks between the main list, calendar dates, and the undated drawer; double-click any date to create a task.
  - **Drawer & Summary**: Collapsible right drawer showing selected-day details, undated items, a **Hide Completed** toggle, and a **Weekly Completed** summary.
- **Top-Edge Auto-Retract**: Drag the window to the top edge of the screen to retract both the todo window and attached calendar into a `6px` strip; hover either strip to expand.
- **Subtasks & Notes**: Expandable subtasks with individual notes, due times, drag reordering, and a prompt to complete the parent task when all subtasks are checked; right-click **Edit** opens the subtask editor.
- **Views, Tags & Batch Actions**: Filter tabs (`All` / `Active` / `Important` / `Done` with configurable default tab), customizable & reorderable Smart Views (`Today` / `Tomorrow` / `This Week` / `Overdue`), 8 customizable color tags, live search, and one-click **Postpone all overdue to today**.
- **Reminders & Hotkeys**: Windows toast notifications (0–60 min lead time) with direct **Mark Done** and **Snooze 10 min** buttons, configurable global **Show Window** and **Quick Add** hotkeys, system tray menu, launch at startup, and JSON import/export.
- **Acrylic Appearance**: Live System / Dark / Light theme sync, Windows Desktop Acrylic material, `0%–90%` transparency slider, and optional auto-fade on mouse leave.

## Requirements

- **Runtime**: Windows 11 22H2 (Build 22621) or later (`x64`)
- **Build from source**: [.NET 11 SDK](https://dotnet.microsoft.com/) with Windows App SDK workload

## Build, Test & Publish

```powershell
# Build (Debug)
dotnet build src/ToDooo.App/ToDooo.csproj -p:Platform=x64

# Run unit tests
dotnet test tests/ToDooo.Tests/ToDooo.Tests.csproj

# Publish self-contained Release build (no .NET runtime required for end users)
dotnet publish src/ToDooo.App/ToDooo.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64

# One-step release: bump version (csproj + manifest), test, publish, pack ZIP
pwsh scripts/release.ps1 -Version 1.4.0
```

Version numbers live only in `ToDooo.csproj`; the settings page reads the assembly version at runtime. Pushing a `v*` tag triggers `.github/workflows/release.yml`, which re-runs tests, packs `ToDooo-<version>-win-x64.zip` (+ SHA256) and publishes a GitHub Release.

## Project Structure

```text
src/ToDooo.App/
├── MainWindow.xaml(.cs)         # Core window lifecycle, hotkeys, WndProc, calendar/settings host
├── MainWindow.Tray.cs           # Tray icon & menus, undo bar, JSON import/export, toast callbacks
├── MainWindow.Dock.cs           # Edge resizing & synchronized top-edge retract/expand
├── MainWindow.Filters.cs        # Search bar, filter tabs, tag dots, smart views, accelerators
├── MainWindow.Todos.cs          # Quick add, task cards, context menus, subtask interactions
├── Models/                      # TodoItem, SubtaskItem, SmartView
├── Services/                    # SQLite repository, settings, reminder scheduler, notifier, palette
├── ViewModels/                  # MainViewModel filtering, sorting, and persistence orchestration
├── Views/                       # CalendarHostWindow, CalendarWindow, SettingsWindow, Editors
├── Helpers/                     # NaturalDateParser, Bind, DpiScale
├── Interop/                     # NativeMethods, WindowAcrylic, WindowCloak, WindowSnap, TrayIcon
└── Themes/                      # Shared resource brushes and control styles
tests/ToDooo.Tests/              # xUnit tests (models, ViewModel, SQLite repository, parser)
```

## Local Data

Stored under `%LocalAppData%\ToDooo\`:

- `todo.db` — SQLite database for tasks and subtasks
- `settings.json` — Theme, transparency, default tab, hotkeys, tag names, smart views, and window bounds

Replacing the executable directory preserves all local data.

## License

[MIT](LICENSE) © 2026 yyt
