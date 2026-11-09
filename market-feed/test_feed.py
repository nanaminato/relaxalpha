import asyncio
import json
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime, timedelta
from http.server import ThreadingHTTPServer
from types import SimpleNamespace
from urllib.error import HTTPError
from urllib.request import urlopen

from feed import BEIJING, QuoteBook, handler_for, run, snapshot


class FakeApi:
    def __init__(self):
        self.loop = asyncio.new_event_loop()
        self.requests = []
        self.closed = False

    async def get_quote(self, symbol):
        self.requests.append(symbol)
        if symbol.endswith("2702"):
            raise ValueError("unknown instrument")
        return SimpleNamespace(datetime=datetime.now(BEIJING).isoformat(),
                               last_price=3100, bid_price1=3099, ask_price1=3101, volume=100, open_interest=200)

    def create_task(self, task):
        return self.loop.create_task(task)

    def wait_update(self, deadline):
        self.loop.run_until_complete(asyncio.sleep(0.01))

    def close(self):
        self.closed = True
        self.loop.close()


class FeedTests(unittest.TestCase):
    def test_concurrent_requests_reuse_one_session_and_one_subscription_per_symbol(self):
        book = QuoteBook(3)
        stop = threading.Event()
        sessions = []

        def factory():
            api = FakeApi()
            sessions.append(api)
            return api

        owner = threading.Thread(target=run, args=(factory, book, stop))
        owner.start()
        with ThreadingHTTPServer(("127.0.0.1", 0), handler_for(book)) as server:
            http = threading.Thread(target=server.serve_forever)
            http.start()
            base = f"http://127.0.0.1:{server.server_port}"
            try:
                def get(path):
                    with urlopen(base + path, timeout=3) as response:
                        return json.load(response)

                quotes_path = "/quotes?symbols=SHFE.rb2701C3000,SHFE.rb2701"
                with ThreadPoolExecutor(4) as pool:
                    list(pool.map(get, [quotes_path] * 12))

                latest = {}
                for _ in range(100):
                    latest = get(quotes_path)
                    if all(q["state"] == "ready" for q in latest["quotes"]):
                        break
                    time.sleep(0.02)
                self.assertEqual([3100, 3100], [q["lastPrice"] for q in latest["quotes"]])
                self.assertEqual("ready", latest["state"])
                self.assertEqual(2, get("/status")["subscriptions"])
                self.assertEqual(1, get("/status")["sessionGeneration"])
                self.assertEqual(1, len(sessions))
                self.assertCountEqual(sessions[0].requests, ["SHFE.rb2701C3000", "SHFE.rb2701"])

                invalid = "/quotes?symbols=SHFE.rb2702"
                get(invalid)
                for _ in range(100):
                    status = get(invalid)["quotes"][0]["state"]
                    if status == "unavailable":
                        break
                    time.sleep(0.02)
                self.assertEqual("unavailable", status)
                with self.assertRaises(HTTPError) as capacity:
                    get("/quotes?symbols=SHFE.rb2703")
                self.assertEqual(429, capacity.exception.code)
                with self.assertRaises(HTTPError) as syntax:
                    get("/quotes?symbols=invalid")
                self.assertEqual(400, syntax.exception.code)
                self.assertEqual(1, len(sessions))
                self.assertEqual(1, sessions[0].requests.count("SHFE.rb2702"))
            finally:
                stop.set()
                owner.join(timeout=5)
                server.shutdown()
                http.join(timeout=5)

        self.assertFalse(owner.is_alive())
        self.assertTrue(sessions[0].closed)

    def test_stale_timestamp_and_missing_values_remain_distinct(self):
        book = QuoteBook(2)
        book.set_state("ready")
        stamp = datetime.now(BEIJING) - timedelta(hours=1)
        quote = SimpleNamespace(datetime=stamp.replace(tzinfo=None).isoformat(), last_price=float("nan"),
                                bid_price1=0, ask_price1=52, volume=0, open_interest=32)
        item = snapshot("SHFE.rb2701", quote)
        book.update("SHFE.rb2701", item)
        observed = book.request(["SHFE.rb2701"])["quotes"][0]
        self.assertTrue(observed["stale"])
        self.assertEqual(stamp.astimezone(UTC), datetime.fromisoformat(observed["quoteTime"]))
        self.assertIsNone(observed["lastPrice"])
        self.assertIsNone(observed["bid"])
        self.assertEqual(52, observed["ask"])
        self.assertEqual(0, observed["volume"])


if __name__ == "__main__":
    unittest.main()
