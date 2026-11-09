import asyncio
import json
import logging
import math
import os
import re
import signal
import threading
import time
from datetime import UTC, datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

SYMBOL = re.compile(r"(?:SHFE|INE|DCE|CZCE|GFEX)\.[A-Za-z]{1,3}\d{3,4}(?:-?[CP]-?\d+(?:\.\d+)?)?", re.ASCII)
BEIJING = timezone(timedelta(hours=8))
STALE_AFTER = timedelta(minutes=2)


class SubscriptionLimit(Exception):
    pass


class QuoteBook:
    def __init__(self, limit):
        self.limit = limit
        self.lock = threading.Lock()
        self.requested = {}
        self.quotes = {}
        self.state = "connecting"
        self.session_generation = 0

    def request(self, symbols):
        with self.lock:
            new = set(symbols).difference(self.requested)
            if len(self.requested) + len(new) > self.limit:
                raise SubscriptionLimit
            for symbol in symbols:
                self.requested.setdefault(symbol, None)
            now = datetime.now(UTC)
            return {"source": "TqSdk", "state": self.state,
                    "quotes": [self._snapshot(symbol, now) for symbol in symbols]}

    def _snapshot(self, symbol, now):
        quote = self.quotes.get(symbol)
        if quote is None:
            return {"symbol": symbol, "state": "pending"}
        if quote["state"] != "ready":
            return {"symbol": symbol, "state": quote["state"]}
        observed = quote["quoteTime"]
        age = now - observed
        return dict(quote, quoteTime=observed.isoformat(),
                    stale=self.state != "ready" or age > STALE_AFTER or age < timedelta(seconds=-5))

    def symbols(self):
        with self.lock:
            return tuple(self.requested)

    def set_state(self, state):
        with self.lock:
            self.state = state
            if state == "ready":
                self.session_generation += 1

    def update(self, symbol, quote):
        with self.lock:
            self.quotes[symbol] = quote

    def status(self):
        with self.lock:
            return {"source": "TqSdk", "state": self.state,
                    "subscriptions": len(self.requested), "capacity": self.limit,
                    "sessionGeneration": self.session_generation}


def positive(value):
    if isinstance(value, (int, float)) and math.isfinite(value) and value > 0:
        return float(value)
    return None


def count(value):
    if isinstance(value, (int, float)) and math.isfinite(value) and value >= 0 and int(value) == value:
        return int(value)
    return None


def snapshot(symbol, quote):
    try:
        stamp = datetime.fromisoformat(quote.datetime)
    except (AttributeError, ValueError, TypeError):
        return None
    if stamp.tzinfo is None:
        stamp = stamp.replace(tzinfo=BEIJING)

    return {"symbol": symbol, "state": "ready", "quoteTime": stamp.astimezone(UTC),
            "lastPrice": positive(quote.last_price), "bid": positive(quote.bid_price1),
            "ask": positive(quote.ask_price1), "volume": count(quote.volume),
            "openInterest": count(quote.open_interest)}


def pump(api, book, stop):
    subscribed = set()
    references = {}

    async def subscribe(symbol):
        try:
            references[symbol] = await asyncio.wait_for(api.get_quote(symbol), timeout=20)
        except Exception:
            book.update(symbol, {"state": "unavailable"})
        else:
            # A reference may exist before its first market tick.
            book.update(symbol, {"state": "pending"})

    while not stop.is_set():
        for symbol in book.symbols():
            if symbol not in subscribed:
                subscribed.add(symbol)
                api.create_task(subscribe(symbol))
        api.wait_update(deadline=time.time() + 0.25)
        for symbol, quote in references.items():
            item = snapshot(symbol, quote)
            if item is not None:
                book.update(symbol, item)


def run(factory, book, stop):
    delay = 5
    while not stop.is_set():
        api = None
        started = time.monotonic()
        try:
            api = factory()
            book.set_state("ready")
            pump(api, book, stop)
        except Exception as error:
            book.set_state("reconnecting")
            logging.warning("TqSdk session interrupted (%s)", type(error).__name__)
        finally:
            if api is not None:
                try:
                    api.close()
                except Exception:
                    logging.warning("TqSdk session close failed")
        if time.monotonic() - started >= 120:
            delay = 5
        if stop.wait(delay):
            break
        delay = min(delay * 2, 120)


def handler_for(book):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            request = urlsplit(self.path)
            if request.path == "/status":
                self.respond(200, book.status())
                return
            if request.path != "/quotes":
                self.respond(404, {"error": "not_found"})
                return

            query = parse_qs(request.query, max_num_fields=4) if request.query.count("&") < 4 else {}
            values = query.get("symbols", [])
            symbols = values[0].split(",") if len(values) == 1 else []
            if not 1 <= len(symbols) <= 8 or any(not SYMBOL.fullmatch(s) for s in symbols):
                self.respond(400, {"error": "invalid_symbols"})
                return
            try:
                self.respond(200, book.request(list(dict.fromkeys(symbols))))
            except SubscriptionLimit:
                self.respond(429, {"error": "subscription_capacity"})

        def respond(self, status, body):
            payload = json.dumps(body, allow_nan=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            try:
                self.wfile.write(payload)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def log_message(self, format, *args):
            # Ignore client URLs and payloads in routine logs.
            pass

    return Handler


def secret(name):
    path = os.environ.get(name + "_FILE")
    value = Path(path).read_text(encoding="utf-8-sig").rstrip("\r\n") if path else os.environ.get(name, "")
    if not value:
        raise ValueError("Missing credential")
    return value


def main():
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    sdk_logger = logging.getLogger("TqApi")
    sdk_logger.addHandler(logging.NullHandler())
    sdk_logger.propagate = False

    try:
        username = secret("TQ_USERNAME")
        password = secret("TQ_PASSWORD")
        limit = int(os.environ.get("TQ_FEED_CAPACITY", "128"))
        port = int(os.environ.get("TQ_FEED_PORT", "8765"))
        if not 2 <= limit <= 512:
            raise ValueError("Invalid capacity")
    except (OSError, ValueError):
        logging.error("TqSdk credentials or service configuration are missing or invalid.")
        return 2

    from tqsdk import TqApi, TqAuth

    stop = threading.Event()
    book = QuoteBook(limit)
    for name in (signal.SIGINT, signal.SIGTERM):
        signal.signal(name, lambda *_: stop.set())
    with ThreadingHTTPServer((os.environ.get("TQ_FEED_HOST", "127.0.0.1"), port), handler_for(book)) as server:
        http_thread = threading.Thread(target=server.serve_forever, daemon=True)
        http_thread.start()
        try:
            run(lambda: TqApi(auth=TqAuth(username, password), disable_print=True), book, stop)
        finally:
            server.shutdown()
            http_thread.join()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
