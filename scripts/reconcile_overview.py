"""对账脚本：把总览页的图表口径与原始库直接比对（只读）。"""

import os
import sqlite3

path = os.path.expandvars(r"%APPDATA%\com.tokenview.app\tokenview-usage.sqlite3")
uri = "file:" + path.replace("\\", "/") + "?mode=ro"
con = sqlite3.connect(uri, uri=True)

rows = con.execute(
    """
    SELECT date(started_at_ms / 1000, 'unixepoch', 'localtime') AS d,
           COUNT(*),
           SUM(COALESCE(total_tokens, 0))
    FROM sessions
    WHERE started_at_ms IS NOT NULL
    GROUP BY d
    ORDER BY d
    """
).fetchall()

print("有会话的天数:", len(rows))
print("最早 3 天:", rows[:3])
print("最晚 3 天:", rows[-3:])
print("会话累计 Token 合计:", f"{sum(r[2] for r in rows):,}")

top = sorted(rows, key=lambda r: -r[2])[:5]
print("单日 Token 最大的 5 天:")
for day, count, tokens in top:
    print(f"  {day}  会话 {count}  Token {tokens:,}")

# 最近 400 天里，有多少天 Token 大于全期峰值的 1%
if rows:
    peak = max(r[2] for r in rows)
    notable = [r for r in rows if r[2] > peak * 0.01]
    print(f"超过峰值 1% 的天数: {len(notable)} / {len(rows)}")
