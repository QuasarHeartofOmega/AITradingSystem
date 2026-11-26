#!/usr/bin/env python3
"""
Живой просмотр логов AITradingSystem по каналам Serilog.

Понимает единый формат UnifiedLogFormatter и многострочные блоки AI/HTTP:
--grep фильтрует целое событие вместе с продолжением (отступ 4 пробела),
а не только строку заголовка. --lines считает события, а не физические строки.

Каналы:
  ai        — промпт, рассуждение, ответ, счётчики генерации, сигнал модели
  ai-native — нативный вывод llama.cpp/ggml
  exchange  — REST/WS биржи, сводки тел, ошибки API
  news      — новостные провайдеры и контекст
  trades    — решения, ордера, позиции, риск
  trading   — общий лог приложения (system + недиагностические каналы)

Примеры:
  python3 tools/watch_logs.py --channel ai
  python3 tools/watch_logs.py --channel ai --grep AI-PROMPT --lines 20
  python3 tools/watch_logs.py --channel trades --grep OUTCOME
  python3 tools/watch_logs.py --channel exchange --grep HTTP-RESP
  python3 tools/watch_logs.py --channel trades --dir AITradingSystem.UI/bin/Debug/net8.0-windows/logs
"""
from __future__ import annotations

import argparse
import glob
import os
import re
import sys
import time
from collections import deque
from collections.abc import Iterator

COLORS = {"ai": "\033[36m", "ai-native": "\033[90m", "exchange": "\033[35m",
          "news": "\033[33m", "trades": "\033[32m", "trading": "\033[37m"}
RESET = "\033[0m"
# Заголовок события: дата, время с миллисекундами и зоной, уровень, канал.
EVENT_START = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[[A-Z]{3}\] \[[^\]]+\]")


def newest_file(log_dir: str, channel: str) -> str | None:
    files = glob.glob(os.path.join(log_dir, f"{channel}-*.log"))
    if not files:
        return None
    # При одинаковом mtime выбираем лексикографически старший суффикс размерной ротации.
    return max(files, key=lambda p: (os.path.getmtime(p), p))


def events(lines: Iterator[str]) -> Iterator[str]:
    """Склеивает заголовок события с его многострочным продолжением."""
    buf: list[str] = []
    for line in lines:
        line = line.rstrip("\r\n")
        if EVENT_START.match(line) and buf:
            yield "\n".join(buf)
            buf = []
        buf.append(line)
    if buf:
        yield "\n".join(buf)


def matches(event: str, grep: str | None) -> bool:
    return grep is None or grep in event


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dir", default="logs", help="каталог логов (Logging:LogPath)")
    parser.add_argument("--channel", default="ai",
                        choices=["ai", "ai-native", "exchange", "news", "trades", "trading"])
    parser.add_argument("--lines", type=int, default=60, help="сколько событий показать при старте")
    parser.add_argument("--grep", help="показывать только события, содержащие подстроку (например AI-PROMPT)")
    parser.add_argument("--once", action="store_true", help="показать хвост и выйти")
    parser.add_argument("--no-color", action="store_true", help="без ANSI-раскраски")
    args = parser.parse_args()
    if args.lines < 0:
        parser.error("--lines не может быть отрицательным")

    path = newest_file(args.dir, args.channel)
    if not path:
        raise SystemExit(f"В каталоге '{args.dir}' нет файлов {args.channel}-*.log. "
                         "Запустите приложение или укажите --dir.")

    use_color = not args.no_color and os.environ.get("NO_COLOR") is None and sys.stdout.isatty()
    color = COLORS.get(args.channel, "") if use_color else ""
    reset = RESET if use_color else ""

    def show(event: str) -> None:
        if matches(event, args.grep):
            print(color + event + reset)

    print(f"{color}==> {path}{reset}")
    fh = open(path, "r", encoding="utf-8", errors="replace")
    try:
        tail: deque[str] = deque(maxlen=args.lines)
        for event in events(fh):
            if matches(event, args.grep):
                tail.append(event)
        for event in tail:
            show(event)

        if args.once:
            return

        pending: list[str] = []
        while True:
            line = fh.readline()
            if not line:
                time.sleep(0.4)
                rotated = newest_file(args.dir, args.channel)
                try:
                    truncated = fh.tell() > os.path.getsize(path)
                except OSError:
                    truncated = True
                if (rotated and rotated != path) or truncated:
                    if pending:
                        show("\n".join(pending))
                        pending = []
                    fh.close()
                    path = rotated or path
                    print(f"{color}==> {path}{reset}")
                    fh = open(path, "r", encoding="utf-8", errors="replace")
                continue

            line = line.rstrip("\r\n")
            if EVENT_START.match(line) and pending:
                show("\n".join(pending))
                pending = []
            pending.append(line)
    except KeyboardInterrupt:
        pass
    finally:
        fh.close()


if __name__ == "__main__":
    main()
