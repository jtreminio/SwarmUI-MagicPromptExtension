# Local decision runtime

MagicPrompt sends structured, text-only decisions to `127.0.0.1:7893` inside the SwarmUI container. The runtime uses the patched StartLux llama-server and the publisher's Jev scorer, each with its own pinned llama.cpp checkout. Both load the Q8_0 model on the CPU with zero GPU layers.

## Set up and run

Run these commands on the Docker host. They assume the container is named `swarmui` and the extension is in the usual location under the SwarmUI checkout.

```bash
runtime=/workspace/src/Extensions/SwarmUI-MagicPromptExtension/DecisionRuntime
data=/workspace/Models/decision-runtime

docker exec swarmui bash "$runtime/setup.sh" "$data"
```

Setup needs internet access for the pinned sources, model assets, and Python dependencies. On the verified `jtreminio/swarmui:26.08-py3` image, Ubuntu 24.04 already includes the required system toolchain. Setup uses CMake 3.14 or newer, a C11/C++17 compiler, Ninja, Git, curl, and Python 3.12 with venv. It creates an isolated Python environment with aiohttp 3.14.5, Transformers 5.23.0, Jinja2 3.1.6, tokenizers 0.23.2, and NumPy 2.5.3. It checks both model SHA-256 hashes. The Jev worker also verifies the publisher's manifest when it starts. Setup accepts an optional second argument for parallel build jobs. Run accepts an optional second argument for inference threads. Each defaults to eight.

After setup, restart SwarmUI. MagicPrompt starts the installed runtime automatically on Linux when it finds `decision-runtime` in a configured model root. The bind mount keeps the binaries, Python environment, and weights across container replacements. Keep the directory at the same path inside the container. Automatic startup reuses the existing install and requires `flock` from util-linux, which is present in the verified image.

To start the runtime while SwarmUI is already up after a new install, run it detached once.

```bash
docker exec -d swarmui bash -c 'exec bash "$1/run.sh" "$2" >> "$2/runtime.log" 2>&1' _ "$runtime" "$data"
```

The launch starts the StartLux llama-server on loopback port 7895, the official StartLux adapter on loopback port 7894, and the common decision endpoint on loopback port 7893. The Jev worker starts when the first Jev request arrives and retains the model in RAM for later requests. A Jev timeout terminates the worker so the next request can start a fresh one. All three ports are internal to SwarmUI's container. Inference uses local files and runs with Hugging Face offline mode enabled.

`run.sh` stays in the foreground and stops its children on SIGINT or SIGTERM. An existing launcher or listening decision service takes precedence over another start attempt.

## Decision API

Send a JSON object to `POST /v1/decisions` with string fields `evaluator` (`startlux` or `jev`), `evidence`, `question`, and `type`, and an `options` array of strings.

| Type | Options | Answer and probabilities |
| --- | --- | --- |
| `choice` | Distinct choices, 2–26 for StartLux and 2–255 for Jev | Winning option string and probabilities in input order |
| `noul` | Empty array or two outcome descriptions in false, true order | Boolean, true when P(true) exceeds 0.5, and probabilities in false, true order |
| `score` | Two to ten ordered rating levels | Probability-weighted index from zero and probabilities in input order |

Each success has `answer`, `probabilities`, `evaluator_model`, and `metrics`. StartLux supplies token counts and confidence when the official adapter returns it. Jev supplies top probability, entropy concentration, and token counts. Errors return a non-2xx HTTP status with a JSON `error` message. The service allows up to 90 seconds for a local evaluator call. StartLux accepts up to its 16,384-token context, and Jev up to its 32,768-token context, including each adapter's prompt overhead.

```bash
docker exec swarmui curl -fsS http://127.0.0.1:7893/v1/decisions \
  -H 'Content-Type: application/json' \
  --data '{"evaluator":"startlux","evidence":"The package arrived on Tuesday.","question":"Did the package arrive on Tuesday?","type":"noul","options":[]}'
```

## Pinned sources

| Asset | Revision |
| --- | --- |
| [StartLux-Decision adapter](https://github.com/StartLuxLabs/StartLux-Decision/tree/a99977d9332decc9fb27a2488b4a83b533b6cd5f) | `a99977d9332decc9fb27a2488b4a83b533b6cd5f` |
| [StartLux llama.cpp](https://github.com/ggml-org/llama.cpp/tree/aa94f2086147cd4bfbcff818ce998dffce5f) | `aa94f2086147cd4bfbcff818ce998dffce5f` with StartLux patch 0001 and request-mode bidirectional attention |
| [StartLux Q8_0 GGUF](https://huggingface.co/startlux-models/StartLux-Decision-v1.1-0.8B-Instant-GGUF/tree/529edd1b9dae38fda3ba6a5ab0cbc785c91e8359) | `529edd1b9dae38fda3ba6a5ab0cbc785c91e8359` |
| [StartLux tokenizer and readout config](https://huggingface.co/startlux-models/StartLux-Decision-v1.1-0.8B-Instant/tree/6496ae641cc71b149ce9dad69bd97f2a44e7bf11) | `6496ae641cc71b149ce9dad69bd97f2a44e7bf11` |
| [Jev Q8_0 GGUF and scorer](https://huggingface.co/chaoliangUNSW/Jev-Style-2B-Decision-v4.2-GGUF/tree/7ceb7058f83472a05b049ad48098664642d91e71) | `7ceb7058f83472a05b049ad48098664642d91e71` |
| [Jev llama.cpp](https://github.com/ggml-org/llama.cpp/tree/441df11f65ea0b6d0c72965aaf70c8241070ddcb) | `441df11f65ea0b6d0c72965aaf70c8241070ddcb` |
