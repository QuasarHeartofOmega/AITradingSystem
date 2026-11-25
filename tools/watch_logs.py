#!/usr/bin/env python3
"""
Живой просмотр логов AITradingSystem (аналог tail -f по каналам Serilog).

Каналы:
  ai        — входящий промпт, сырой ответ модели, рассуждения, распарсенный сигнал
  exchange  — каждый REST-запрос/ответ Binance, ошибки API, ордера, WebSocket
  news      — запросы агрегаторов и новостной контекст для модели
  trades    — решения, открытие/закрытие, отказы с причинами, баланс
  trading   — общий лог приложения

Примеры:
  python3 tools/watch_logs.py --channel ai
  python3 tools/watch_logs.py --channel trades --dir AITradingSystem.UI/bin/Debug/net8.0-windows/logs
  python3 tools/watch_logs.py --channel ai --grep "AI PROMPT" --lines 200
"""
from __future__ import annotations

import argparse
import glob
import os
import time

COLORS = {"ai": "\033[36m", "exchange": "\033[35m", "news": "\033[33m",
          "trades": "\033[32m", "trading": "\033[37m"}
RESET = "\033[0m"


def newest_file(log_dir: str, channel: str) -> str | None:
    files = sorted(glob.glob(os.path.join(log_dir, f"{channel}-*.log")), key=os.path.getmtime)
    return files[-1] if files else None


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", default="logs", help="каталог логов (Logging:LogPath)")
    parser.add_argument("--channel", default="ai",
                        choices=["ai", "exchange", "news", "trades", "trading"])
    parser.add_argument("--lines", type=int, default=60, help="сколько строк показать при старте")
    parser.add_argument("--grep", help="показывать только строки с этой подстрокой")
    parser.add_argument("--once", action="store_true", help="показать хвост и выйти")
    args = parser.parse_args()

    path = newest_file(args.dir, args.channel)
    if not path:
        raise SystemExit(f"В каталоге '{args.dir}' нет файлов {args.channel}-*.log. "
                         "Запустите приложение или укажите --dir.")

    color = COLORS.get(args.channel, "")
    print(f"{color}==> {path}{RESET}")

    fh = open(path, "r", encoding="utf-8", errors="replace")
    try:
        tail = fh.readlines()[-args.lines:]
        for line in tail:
            if not args.grep or args.grep in line:
                print(color + line.rstrip("\n") + RESET)

        if args.once:
            return

        while True:
            line = fh.readline()
            if not line:
                time.sleep(0.4)
                rotated = newest_file(args.dir, args.channel)
                if rotated and rotated != path:  # началась новая суточная/размерная ротация
                    fh.close()
                    path = rotated
                    print(f"{color}==> {path}{RESET}")
                    fh = open(path, "r", encoding="utf-8", errors="replace")
                continue
            if not args.grep or args.grep in line:
                print(color + line.rstrip("\n") + RESET)
    except KeyboardInterrupt:
        pass
    finally:
        fh.close()


if __name__ == "__main__":
    main()
