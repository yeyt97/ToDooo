"""
为 README 截图生成一套虚构的演示数据。

只往 TODOOO_DATA_DIR 指向的临时目录里写，绝不碰真实数据目录。
用法：
    $env:TODOOO_DATA_DIR = 'C:\\temp\\todooo-demo'
    python scripts/make-demo-data.py
"""

import os
import sqlite3
import uuid
from datetime import datetime, timedelta, date

ROOT = os.environ.get("TODOOO_DATA_DIR")
if not ROOT:
    raise SystemExit("Set TODOOO_DATA_DIR first (point it at a throwaway folder).")

real = os.path.join(os.environ["LOCALAPPDATA"], "ToDooo")
if os.path.abspath(ROOT).lower() == os.path.abspath(real).lower():
    raise SystemExit("Refusing to write demo data into the real data directory.")

os.makedirs(ROOT, exist_ok=True)
db_path = os.path.join(ROOT, "todo.db")
if os.path.exists(db_path):
    os.remove(db_path)

FMT = "%Y-%m-%dT%H:%M:%S"
TODAY = date.today()
NOW = datetime.now()


def dt(days, hour, minute=0):
    return datetime(TODAY.year, TODAY.month, TODAY.day, hour, minute) + timedelta(days=days)


def stamp(value):
    return value.strftime(FMT)


con = sqlite3.connect(db_path)
cur = con.cursor()

# ---- schema：与应用的 TodoRepository / CalendarRepository 保持一致 ----
cur.executescript(
    """
    CREATE TABLE todos (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        title TEXT NOT NULL,
        is_done INTEGER NOT NULL DEFAULT 0,
        is_important INTEGER NOT NULL DEFAULT 0,
        due_at TEXT NULL,
        recurrence INTEGER NOT NULL DEFAULT 0,
        tag_index INTEGER NOT NULL DEFAULT -1,
        sort_order REAL NOT NULL DEFAULT 0,
        created_at TEXT NOT NULL,
        completed_at TEXT NULL,
        due_notified INTEGER NOT NULL DEFAULT 0,
        snooze_until TEXT NULL,
        note TEXT NOT NULL DEFAULT ''
    );
    CREATE INDEX idx_todos_order ON todos(sort_order);

    CREATE TABLE subtasks (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        todo_id INTEGER NOT NULL,
        title TEXT NOT NULL,
        is_done INTEGER NOT NULL DEFAULT 0,
        sort_order REAL NOT NULL DEFAULT 0,
        due_at TEXT NULL,
        due_notified INTEGER NOT NULL DEFAULT 0,
        note TEXT NOT NULL DEFAULT ''
    );
    CREATE INDEX idx_subtasks_todo ON subtasks(todo_id);

    CREATE TABLE calendar_sources (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        href TEXT NOT NULL UNIQUE,
        display_name TEXT NOT NULL DEFAULT '',
        color TEXT NOT NULL DEFAULT '',
        is_visible INTEGER NOT NULL DEFAULT 1,
        sort_order INTEGER NOT NULL DEFAULT 0,
        sync_token TEXT NULL
    );

    CREATE TABLE calendar_events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        calendar_href TEXT NOT NULL,
        resource_href TEXT NOT NULL,
        uid TEXT NOT NULL DEFAULT '',
        start_at TEXT NOT NULL,
        end_at TEXT NULL,
        is_all_day INTEGER NOT NULL DEFAULT 0,
        summary TEXT NOT NULL DEFAULT '',
        location TEXT NOT NULL DEFAULT '',
        recurrence_id TEXT NULL,
        rrule TEXT NOT NULL DEFAULT '',
        etag TEXT NOT NULL DEFAULT '',
        last_modified TEXT NULL,
        notes TEXT NOT NULL DEFAULT '',
        url TEXT NOT NULL DEFAULT '',
        categories TEXT NOT NULL DEFAULT '',
        is_busy INTEGER NOT NULL DEFAULT 1,
        status TEXT NOT NULL DEFAULT 'CONFIRMED',
        class TEXT NOT NULL DEFAULT 'PUBLIC',
        priority INTEGER NOT NULL DEFAULT 0
    );
    CREATE INDEX idx_events_range ON calendar_events(start_at);
    CREATE INDEX idx_events_calendar ON calendar_events(calendar_href);

    CREATE TABLE calendar_pending (
        resource_href TEXT PRIMARY KEY,
        kind TEXT NOT NULL,
        calendar_href TEXT NOT NULL,
        etag TEXT NOT NULL DEFAULT '',
        payload TEXT NOT NULL DEFAULT ''
    );

    CREATE TABLE calendar_event_masters (
        resource_href TEXT PRIMARY KEY,
        calendar_href TEXT NOT NULL,
        uid TEXT NOT NULL DEFAULT '',
        etag TEXT NOT NULL DEFAULT '',
        raw_ics TEXT NOT NULL DEFAULT ''
    );
    CREATE INDEX idx_masters_calendar ON calendar_event_masters(calendar_href);
    """
)

# ---- 待办：虚构的工作与生活事项，覆盖各种状态 ----
# (标题, 完成, 重要, 截止偏移天, 时, 分, 标签, 重复, 备注)
TODOS = [
    ("整理季度复盘材料", 0, 1, 0, 17, 0, 0, 0, "把三个项目的进展和风险整理成一页纸"),
    ("给新同事开通系统账号", 0, 0, 0, 11, 30, 1, 0, ""),
    ("预约体检", 0, 1, 1, 9, 0, 2, 0, "记得空腹"),
    ("续签办公场地合同", 0, 0, 2, 15, 0, 0, 0, ""),
    ("准备周会演示", 0, 1, 3, 10, 0, 1, 2, "讲清楚这周做完的和下周要做的"),
    ("缴纳水电费", 0, 0, 4, 20, 0, 3, 3, ""),
    ("读《重构》第 3 章", 0, 0, None, 0, 0, 4, 0, "做好笔记，下周分享"),
    ("整理书架", 0, 0, None, 0, 0, -1, 0, ""),
    ("提交差旅报销单", 1, 0, -1, 16, 0, 0, 0, ""),
    ("回复供应商邮件", 1, 0, -2, 14, 0, 1, 0, ""),
]

cur.execute("DELETE FROM todos")
cur.execute("DELETE FROM subtasks")
for i, (title, done, important, off, hh, mm, tag, recur, note) in enumerate(TODOS):
    due = dt(off, hh, mm) if off is not None else None
    cur.execute(
        """INSERT INTO todos
           (title, is_done, is_important, due_at, recurrence, tag_index, sort_order,
            created_at, completed_at, note)
           VALUES (?,?,?,?,?,?,?,?,?,?)""",
        (
            title, done, important, stamp(due) if due else None, recur, tag,
            float(i) * 10.0, stamp(NOW - timedelta(days=3)),
            stamp(NOW - timedelta(hours=5)) if done else None, note,
        ),
    )

# 子任务：给前几条挂上，展示进度和层级
SUBTASKS = {
    1: [("导出各项目工时", 1), ("写风险清单", 0), ("和产品确认数据", 0)],
    2: [("填申请表", 1), ("等主管审批", 0)],
    5: [("列演示大纲", 1), ("补两张图表", 0), ("提前试一遍投影", 0)],
    4: [("确认新租金", 0), ("准备纸质材料", 0)],
}
for todo_id, items in SUBTASKS.items():
    for j, (text, done) in enumerate(items):
        cur.execute(
            """INSERT INTO subtasks (todo_id, title, is_done, sort_order, due_at, note)
               VALUES (?,?,?,?,?,?)""",
            (todo_id, text, done, float(j) * 10.0, None, ""),
        )

# ---- 日历源：虚构的四个日历 ----
SOURCES = [
    ("demo/calendars/work/", "工作", "#4D8FE3"),
    ("demo/calendars/personal/", "个人", "#F08A3C"),
    ("demo/calendars/family/", "家庭", "#4CAF6D"),
    ("demo/calendars/study/", "学习", "#9B6BE8"),
]
cur.execute("DELETE FROM calendar_sources")
for i, (href, name, color) in enumerate(SOURCES):
    cur.execute(
        """INSERT INTO calendar_sources (href, display_name, color, is_visible, sort_order)
           VALUES (?,?,?,1,?)""",
        (href, name, color, i),
    )

cur.execute("DELETE FROM calendar_event_masters")
cur.execute("DELETE FROM calendar_events")


def add_event(calendar, summary, start, end, all_day=False, rrule="", location="", description=""):
    """写入一份母事件 ICS；列表展示由应用读取时按 RRULE 展开。"""
    uid = str(uuid.uuid4()).upper()
    resource = f"{calendar}{uid}.ics"

    def d(x):
        return x.strftime("%Y%m%d")

    def utc(x):
        return x.strftime("%Y%m%dT%H%M%SZ")

    lines = [
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "PRODID:-//ToDooo//Demo//EN",
        "BEGIN:VEVENT",
        f"UID:{uid}",
        f"DTSTAMP:{utc(NOW)}",
    ]
    if all_day:
        lines.append(f"DTSTART;VALUE=DATE:{d(start)}")
        lines.append(f"DTEND;VALUE=DATE:{d(end)}")
    else:
        lines.append(f"DTSTART;TZID=Asia/Shanghai:{start.strftime('%Y%m%dT%H%M%S')}")
        lines.append(f"DTEND;TZID=Asia/Shanghai:{end.strftime('%Y%m%dT%H%M%S')}")
    lines.append(f"SUMMARY:{summary}")
    if location:
        lines.append(f"LOCATION:{location}")
    if description:
        lines.append(f"DESCRIPTION:{description}")
    if rrule:
        lines.append(f"RRULE:{rrule}")
    lines += ["END:VEVENT", "END:VCALENDAR"]

    ics = "\r\n".join(lines) + "\r\n"
    cur.execute(
        "INSERT INTO calendar_event_masters (resource_href, calendar_href, uid, etag, raw_ics) VALUES (?,?,?,?,?)",
        (resource, calendar, uid, "", ics),
    )
    return resource


W, P, F, S = SOURCES[0][0], SOURCES[1][0], SOURCES[2][0], SOURCES[3][0]

# 本周的例会（每周重复，展示循环日程）
add_event(W, "每周例会", dt(-14, 10), dt(-14, 11), rrule="FREQ=WEEKLY;BYDAY=MO", location="3 号会议室")
# 跨日：出差
add_event(W, "出差·杭州", dt(3, 0), dt(6, 0), all_day=True, description="客户现场沟通")
# 多日项目周期
add_event(W, "版本发布窗口", dt(1, 0), dt(4, 0), all_day=True)
# 单日定时
add_event(W, "一对一沟通", dt(0, 15), dt(0, 16))
add_event(W, "需求评审", dt(2, 14), dt(2, 15, 30), location="线上")
add_event(W, "供应商对接", dt(1, 11), dt(1, 12))
add_event(P, "健身", dt(0, 19), dt(0, 20))
add_event(P, "朋友聚餐", dt(2, 18, 30), dt(2, 20, 30), location="万象城")
add_event(P, "牙医预约", dt(5, 9, 30), dt(5, 10))
add_event(F, "家人生日", dt(4, 0), dt(5, 0), all_day=True)
add_event(F, "周末露营", dt(9, 0), dt(11, 0), all_day=True, location="楠溪江")
add_event(S, "读书会", dt(6, 19), dt(6, 21))
add_event(S, "线上课程", dt(-3, 20), dt(-3, 21))

con.commit()
con.close()

# settings.json：保持默认（日历同步关闭 = 纯离线）。
# 用默认值就够了，但显式写一份，避免沿用上一次跑过的配置。
import json
settings_path = os.path.join(ROOT, "settings.json")
with open(settings_path, "w", encoding="utf-8") as f:
    json.dump({
        "CalendarSyncEnabled": False,
        "CalendarAppleId": "",
        "CalendarPasswordCipher": "",
        "CalendarRefreshMinutes": 0,
        "ThemeMode": 1,
        "WindowOpacity": 0.9,
        "WindowTintMode": 1,
    }, f, ensure_ascii=False, indent=2)

print("demo data written to", db_path)
print("todos:", len(TODOS))
