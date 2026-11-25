#!/usr/bin/env python3
"""
Быстрая проверка внешних источников AITradingSystem — без сборки .NET.

Проверяет:
  * Binance Demo (demo-api.binance.com): ping / time / klines / exchangeInfo(фильтры);
  * Binance Demo WebSocket (demo-stream.binance.com): kline + depth20 (реальные кадры);
  * Binance Spot Testnet и Mainnet (для сравнения);
  * новости cryptocurrency.cv/api/news (бесплатный агрегатор, ключ не нужен).

Запуск:  python3 tools/verify_endpoints.py [--symbol BTCUSDT] [--json report.json]
Код возврата 0 — все обязательные проверки прошли.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import socket
import ssl
import struct
import sys
import time
import urllib.error
import urllib.request

UA = {"User-Agent": "AITradingSystem/1.0", "Accept": "application/json"}
RESULTS: list[dict] = []


def record(name: str, ok: bool, detail: str, required: bool = True) -> None:
    RESULTS.append({"check": name, "ok": ok, "required": required, "detail": detail})
    print(f"[{'OK ' if ok else 'FAIL'}] {name}: {detail}")


def http_json(url: str, timeout: int = 20):
    started = time.time()
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=timeout) as resp:
            body = resp.read().decode("utf-8", "replace")
            return resp.status, json.loads(body), (time.time() - started) * 1000, body
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "replace")[:300]
        return exc.code, None, (time.time() - started) * 1000, body
    except Exception as exc:  # noqa: BLE001
        return -1, None, (time.time() - started) * 1000, repr(exc)


def ws_frames(host: str, path: str, count: int = 3, port: int = 443, timeout: int = 20):
    """Минимальный WebSocket-клиент: рукопожатие + чтение текстовых кадров."""
    ctx = ssl.create_default_context()
    raw = socket.create_connection((host, port), timeout=timeout)
    sock = ctx.wrap_socket(raw, server_hostname=host)
    key = base64.b64encode(os.urandom(16)).decode()
    sock.send(
        (
            f"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"
        ).encode()
    )
    buf = b""
    while b"\r\n\r\n" not in buf:
        chunk = sock.recv(4096)
        if not chunk:
            break
        buf += chunk
    head, _, buf = buf.partition(b"\r\n\r\n")
    status = head.split(b"\r\n")[0].decode(errors="replace")
    if "101" not in status:
        sock.close()
        raise RuntimeError(f"рукопожатие не удалось: {status}")

    frames: list[str] = []
    deadline = time.time() + timeout
    while len(frames) < count and time.time() < deadline:
        while len(buf) < 2:
            buf += sock.recv(65536)
        opcode = buf[0] & 0x0F
        length = buf[1] & 0x7F
        offset = 2
        if length == 126:
            while len(buf) < 4:
                buf += sock.recv(65536)
            length = struct.unpack("!H", buf[2:4])[0]
            offset = 4
        elif length == 127:
            while len(buf) < 10:
                buf += sock.recv(65536)
            length = struct.unpack("!Q", buf[2:10])[0]
            offset = 10
        while len(buf) < offset + length:
            buf += sock.recv(65536)
        payload, buf = buf[offset : offset + length], buf[offset + length :]
        if opcode == 1:
            frames.append(payload.decode("utf-8", "replace"))
    sock.close()
    return frames


def check_rest(label: str, base: str, symbol: str, required: bool) -> None:
    status, data, ms, raw = http_json(f"{base}/api/v3/time")
    record(f"{label} REST /time", status == 200 and isinstance(data, dict) and "serverTime" in data,
           f"HTTP {status}, {ms:.0f} мс, {raw[:80]}", required)

    status, data, ms, raw = http_json(f"{base}/api/v3/klines?symbol={symbol}&interval=15m&limit=3")
    ok = status == 200 and isinstance(data, list) and len(data) > 0 and len(data[0]) >= 9
    record(f"{label} REST /klines", ok,
           f"HTTP {status}, свечей {len(data) if isinstance(data, list) else 0}, {ms:.0f} мс", required)

    url = f'{base}/api/v3/exchangeInfo?symbols=%5B%22{symbol}%22%5D'
    status, data, ms, raw = http_json(url)
    detail = f"HTTP {status}, {ms:.0f} мс"
    ok = False
    if status == 200 and isinstance(data, dict) and data.get("symbols"):
        filters = {f["filterType"]: f for f in data["symbols"][0].get("filters", [])}
        lot = filters.get("LOT_SIZE", {})
        price = filters.get("PRICE_FILTER", {})
        notional = filters.get("NOTIONAL", filters.get("MIN_NOTIONAL", {}))
        ok = bool(lot.get("stepSize") and price.get("tickSize"))
        detail += (f", step={lot.get('stepSize')} tick={price.get('tickSize')} "
                   f"minNotional={notional.get('minNotional')}")
    record(f"{label} REST /exchangeInfo (фильтры)", ok, detail, required)

    # Подписанный эндпоинт без ключа/подписи должен ответить ошибкой Binance
    # (-1102 нет signature, -2014/-2015 нет/неверный ключ), а не 404:
    # это подтверждает, что путь /api/v3/account в этом окружении существует.
    status, data, ms, raw = http_json(f"{base}/api/v3/account")
    code = data.get("code") if isinstance(data, dict) else None
    ok = status in (400, 401, 403) and code in (-1102, -2014, -2015, -1022)
    record(f"{label} REST /account существует (ждём ошибку подписи без ключа)", ok,
           f"HTTP {status}, {raw[:120]}", required=False)


def check_ws(label: str, host: str, symbol: str, required: bool) -> None:
    stream = symbol.lower()
    path = f"/stream?streams={stream}@kline_15m/{stream}@depth20@1000ms"
    try:
        frames = ws_frames(host, path, count=4)
    except Exception as exc:  # noqa: BLE001
        record(f"{label} WS kline+depth20", False, repr(exc)[:160], required)
        return

    have_kline = have_depth = False
    for frame in frames:
        try:
            msg = json.loads(frame)
        except json.JSONDecodeError:
            continue
        name = msg.get("stream", "")
        payload = msg.get("data", {})
        if "@kline" in name and isinstance(payload.get("k"), dict):
            k = payload["k"]
            have_kline = all(key in k for key in ("t", "T", "o", "h", "l", "c", "v", "x"))
        if "@depth" in name and isinstance(payload.get("bids"), list):
            have_depth = bool(payload["bids"]) and isinstance(payload["bids"][0], list)
    record(f"{label} WS kline+depth20", have_kline and have_depth,
           f"кадров {len(frames)}, kline={have_kline}, depth20={have_depth}", required)


def check_news(limit: int) -> None:
    url = f"https://cryptocurrency.cv/api/news?limit={limit}"
    status, data, ms, raw = http_json(url, timeout=30)
    if status != 200 or not isinstance(data, dict):
        record("cryptocurrency.cv /api/news", False, f"HTTP {status}, {raw[:160]}")
        return
    articles = data.get("articles", [])
    required_fields = ("title", "link", "pubDate", "source", "contentType")
    ok = bool(articles) and all(f in articles[0] for f in required_fields)
    sample = articles[0]["title"][:70] if articles else "-"
    record("cryptocurrency.cv /api/news", ok,
           f"HTTP {status}, статей {len(articles)}, totalCount={data.get('totalCount')}, "
           f"{ms:.0f} мс, пример: {sample}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--symbol", default="BTCUSDT")
    parser.add_argument("--json", help="сохранить отчёт в файл")
    args = parser.parse_args()

    print("=== Binance Demo (demo.binance.com) ===")
    check_rest("Demo", "https://demo-api.binance.com", args.symbol, required=True)
    check_ws("Demo", "demo-stream.binance.com", args.symbol, required=True)

    print("\n=== Binance Mainnet / Testnet ===")
    check_rest("Mainnet", "https://api.binance.com", args.symbol, required=False)
    check_ws("Mainnet", "stream.binance.com", args.symbol, required=False)
    check_rest("Testnet", "https://testnet.binance.vision", args.symbol, required=False)

    print("\n=== Новости ===")
    check_news(limit=5)

    failed = [r for r in RESULTS if r["required"] and not r["ok"]]
    print(f"\nИтог: {len(RESULTS) - len(failed)}/{len(RESULTS)} проверок_OK, обязательных провалов: {len(failed)}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"timestamp_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                       "results": RESULTS}, fh, ensure_ascii=False, indent=2)
        print(f"Отчёт: {args.json}")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
