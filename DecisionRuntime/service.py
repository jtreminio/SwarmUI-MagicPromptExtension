#!/usr/bin/env python3
"""Text-only local adapter for the two pinned decision runtimes."""

import argparse
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import logging
import math
import os
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.request import ProxyHandler, Request, build_opener


MODELS = {
    "startlux": "startlux-models/StartLux-Decision-v1.1-0.8B-Instant-GGUF",
    "jev": "chaoliangUNSW/Jev-Style-2B-Decision-v4.2-GGUF",
}
PORT = 7893
STARTLUX_URL = "http://127.0.0.1:7894/v1/systemone"
MAX_BODY = 1048576
TIMEOUT = 90


@dataclass(frozen=True)
class Decision:
    evidence: str
    question: str
    kind: str
    options: tuple[str, ...]
    evaluator: str

    def keys(self):
        if self.kind == "noul":
            return ("false", "true")
        if self.kind == "score":
            return tuple(str(index) for index in range(len(self.options)))
        return self.options

    def criteria(self):
        if self.kind == "noul":
            return dict(zip(self.keys(), self.options)) if self.options else None
        if self.kind == "choice":
            return dict.fromkeys(self.options)
        return list(self.options)


def decode_request(payload):
    if not isinstance(payload, dict):
        raise ValueError("Expected a JSON object.")
    evidence, question, kind, evaluator, options = (
        payload.get(key) for key in ("evidence", "question", "type", "evaluator", "options")
    )
    if not isinstance(evidence, str) or not isinstance(question, str) or not question.strip():
        raise ValueError("Expected string evidence and a nonempty string question.")
    if evaluator not in ("startlux", "jev") or kind not in ("choice", "noul", "score"):
        raise ValueError("Unknown decision evaluator or type.")
    if not isinstance(options, list) or any(not isinstance(item, str) or not item.strip() for item in options):
        raise ValueError("Expected an options array of nonempty strings.")
    if kind == "choice":
        limit = 26 if evaluator == "startlux" else 255
        if not 2 <= len(options) <= limit or len(set(options)) != len(options):
            raise ValueError(f"{evaluator} choice requires 2 to {limit} distinct options.")
    if kind == "noul" and len(options) not in (0, 2):
        raise ValueError("Noul uses zero options or two descriptions in false, true order.")
    if kind == "score" and not 2 <= len(options) <= 10:
        raise ValueError("Score requires 2 to 10 ordered levels.")
    return Decision(evidence, question, kind, tuple(options), evaluator)


def normalize(decision, distribution, metrics):
    probabilities = [float(distribution[key]) for key in decision.keys()]
    if any(not math.isfinite(p) or p < 0 or p > 1 for p in probabilities):
        raise ValueError("The evaluator returned invalid probabilities.")
    if not math.isclose(sum(probabilities), 1, abs_tol=0.001):
        raise ValueError("The evaluator's probabilities do not sum to one.")
    if decision.kind == "choice":
        answer = decision.options[probabilities.index(max(probabilities))]
    elif decision.kind == "noul":
        answer = probabilities[1] > 0.5
    else:
        answer = sum(index * p for index, p in enumerate(probabilities))
    return {
        "answer": answer,
        "probabilities": probabilities,
        "evaluator_model": MODELS[decision.evaluator],
        "metrics": metrics,
    }


def startlux(decision):
    question = {"type": decision.kind, "instructions": decision.question}
    if decision.criteria() is not None:
        question["criteria"] = decision.criteria()
    payload = {
        "state": decision.evidence, "questions": {"decision": question},
        "think_mode": "never", "none_check": False, "allow_none": False,
    }
    request = Request(
        STARTLUX_URL, data=json.dumps(payload).encode(), headers={"Content-Type": "application/json"}
    )
    try:
        with build_opener(ProxyHandler({})).open(request, timeout=TIMEOUT) as response:
            result = json.load(response)
    except HTTPError as error:
        status = error.code
        error.close()
        raise EvaluatorError(status if status in (400, 422) else 502, f"StartLux returned HTTP {status}.") from error
    except URLError as error:
        raise EvaluatorError(502, "StartLux is unavailable.") from error
    answer = result["answers"]["decision"]
    if decision.kind == "noul":
        yes = answer["noul"]
        probabilities = {"false": 1 - yes, "true": yes}
    else:
        probabilities = answer["probabilities"]
    metrics = dict(result.get("usage", {}))
    if "confidence" in answer:
        metrics["confidence"] = answer["confidence"]
    return normalize(decision, probabilities, metrics)


class EvaluatorError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


class Jev:
    """Keep Jev's scorer hot and recreate the worker after a failed pipe or timeout."""

    def __init__(self, directory, threads):
        self.directory = directory
        self.threads = threads
        self.process = None
        self.lock = threading.Lock()

    def close(self):
        if self.process is None:
            return
        try:
            os.killpg(self.process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        self.process.wait()
        self.process.stdin.close()
        self.process.stdout.close()
        self.process = None

    def __call__(self, decision):
        deadline = time.monotonic() + TIMEOUT
        if not self.lock.acquire(timeout=TIMEOUT):
            raise TimeoutError("Jev queue timed out.")
        try:
            if self.process is None:
                self.process = subprocess.Popen(
                    [sys.executable, __file__, "--jev-worker", str(self.directory), "--threads", str(self.threads)],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, start_new_session=True,
                )
            payload = json.dumps({
                "state": decision.evidence, "question": decision.question,
                "options": decision.criteria(), "qtype": decision.kind,
            }).encode() + b"\n"
            # The worker may still be loading the model on the first request.
            # Bound both pipe operations so a wedged worker can be replaced.
            with selectors.DefaultSelector() as selector:
                os.set_blocking(self.process.stdin.fileno(), False)
                selector.register(self.process.stdin, selectors.EVENT_WRITE)
                pending = memoryview(payload)
                while pending:
                    if not selector.select(max(0, deadline - time.monotonic())):
                        raise TimeoutError("Jev worker write timed out.")
                    pending = pending[os.write(self.process.stdin.fileno(), pending):]
                selector.unregister(self.process.stdin)
                selector.register(self.process.stdout, selectors.EVENT_READ)
                response = bytearray()
                while not response.endswith(b"\n"):
                    if not selector.select(max(0, deadline - time.monotonic())):
                        raise TimeoutError("Jev worker response timed out.")
                    chunk = os.read(self.process.stdout.fileno(), 65536)
                    if not chunk:
                        raise BrokenPipeError("Jev worker exited.")
                    response.extend(chunk)
            result = json.loads(response)
        except (OSError, ValueError):
            self.close()
            raise
        finally:
            self.lock.release()
        if "error" in result:
            raise EvaluatorError(result["status"], result["error"])
        return normalize(decision, result["probabilities"], result["metrics"])


def jev_worker(directory, threads):
    sys.path.insert(0, str(directory))
    from jev_style_decision_gguf import InputBudgetError, JevStyleDecisionGGUF, QuestionError

    with JevStyleDecisionGGUF(model_dir=directory, quant="Q8_0", binary=directory / "build/jev-score-v2",
                               n_gpu_layers=0, threads=threads, verify=True) as scorer:
        for line in sys.stdin:
            try:
                result = scorer.decide(**json.loads(line))
            except (InputBudgetError, QuestionError):
                response = {"status": 422, "error": "Jev could not render the question within its supported input limits."}
            else:
                fields = ("top_probability", "entropy_concentration", "input_tokens", "state_tokens", "head_tokens")
                response = {
                    "probabilities": result["probabilities"],
                    "metrics": {key: result[key] for key in fields},
                }
            print(json.dumps(response, allow_nan=False), flush=True)


class Handler(BaseHTTPRequestHandler):
    def log_request(self, code="-", size="-"):
        if not isinstance(code, int) or not 200 <= code < 300:
            super().log_request(code, size)

    def send_json(self, status, payload):
        data = json.dumps(payload, allow_nan=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if self.path != "/v1/decisions":
            self.send_json(404, {"error": "Unknown route."})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= MAX_BODY:
                raise ValueError("Expected a request body of at most 1 MiB.")
            self.connection.settimeout(10)
            decision = decode_request(json.loads(self.rfile.read(length)))
        except (ValueError, UnicodeError) as error:
            self.send_json(400, {"error": str(error)})
            return
        except TimeoutError:
            self.send_json(408, {"error": "Request body timed out."})
            return
        try:
            result = startlux(decision) if decision.evaluator == "startlux" else self.server.jev(decision)
        except EvaluatorError as error:
            self.send_json(error.status, {"error": str(error)})
        except TimeoutError:
            self.send_json(504, {"error": f"{decision.evaluator} evaluation timed out."})
        except (OSError, ValueError, KeyError, TypeError) as error:
            logging.error("%s evaluation failed: %s", decision.evaluator, type(error).__name__)
            self.send_json(502, {"error": f"{decision.evaluator} evaluation failed."})
        else:
            self.send_json(200, result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jev-dir", type=Path)
    parser.add_argument("--jev-worker", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--threads", type=int, default=8)
    args = parser.parse_args()
    if args.threads < 1:
        parser.error("--threads must be positive")
    if args.jev_worker:
        jev_worker(args.jev_worker.resolve(), args.threads)
        return
    if not args.jev_dir:
        parser.error("--jev-dir is required")
    jev = Jev(args.jev_dir.resolve(), args.threads)
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    try:
        with ThreadingHTTPServer(("127.0.0.1", PORT), Handler) as server:
            server.jev = jev
            server.serve_forever()
    finally:
        jev.close()


if __name__ == "__main__":
    main()
