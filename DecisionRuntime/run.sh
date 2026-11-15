#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=${1:-/tmp/magicprompt-decisions}
threads=${2:-8}
cd "$root"

# The ports are shared within the container. Keep one launcher through warmup and shutdown.
command -v flock >/dev/null || { echo 'Decision runtime requires flock (util-linux).' >&2; exit 1; }
exec 9>"${TMPDIR:-/tmp}/magicprompt-decision-runtime.lock"
if ! flock -n 9 || (exec 8<>/dev/tcp/127.0.0.1/7893) 2>/dev/null; then
    echo 'MagicPrompt: local decision runtime already running or starting.'
    exit 0
fi

# The adapter and its model files are local. No inference process needs a GPU or the Hub.
export CUDA_VISIBLE_DEVICES='' HIP_VISIBLE_DEVICES='' ROCR_VISIBLE_DEVICES=''
export HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 PYTHONDONTWRITEBYTECODE=1
export STARTLUX_LLAMACPP_BIDIR=request
export NO_PROXY=127.0.0.1,localhost no_proxy=127.0.0.1,localhost
pids=()
stop() {
    if (("${#pids[@]}")); then
        kill "${pids[@]}" 2>/dev/null || true
        wait "${pids[@]}" 2>/dev/null || true
    fi
}
trap stop EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

llama=llama.cpp-aa94f2086147cd4bfbcff818ce998dffce5f/build/bin/llama-server
if [[ ! -x "$llama" ]]; then
    echo "Run DecisionRuntime/setup.sh inside this container first." >&2
    exit 1
fi
test -s startlux/StartLux-Decision-v1.1-0.8B-Instant-Q8_0.gguf
test -s jev/Jev-Style-2B-Decision-v4.2-Q8_0.gguf
test -x jev/build/jev-score-v2
test -x venv/bin/startlux-decide

"./$llama" --host 127.0.0.1 --port 7895 --no-ui \
    --model startlux/StartLux-Decision-v1.1-0.8B-Instant-Q8_0.gguf -ngl 0 \
    --threads "$threads" --parallel 1 --ctx-checkpoints 0 -c 16384 -b 16384 -ub 16384 \
    > startlux-llama.log 2>&1 &
pids+=("$!")
wait_ready() {
    local pid=$1 url=$2
    for ((attempt=0; attempt<120; attempt++)); do
        kill -0 "$pid" 2>/dev/null || return 1
        if curl --noproxy '*' -fsS --max-time 1 "$url" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    return 1
}
wait_ready "${pids[0]}" http://127.0.0.1:7895/health || {
    echo 'StartLux llama-server failed to start. See startlux-llama.log.' >&2
    exit 1
}
./venv/bin/startlux-decide serve --model "$PWD/startlux" --host 127.0.0.1 --port 7894 \
    --backend llamacpp --llamacpp-url http://127.0.0.1:7895 --no-images --think-mode never \
    --no-none-check --max-model-len 16384 > startlux-adapter.log 2>&1 &
pids+=("$!")
wait_ready "${pids[1]}" http://127.0.0.1:7894/health || {
    echo 'StartLux adapter failed to start. See startlux-adapter.log.' >&2
    exit 1
}
./venv/bin/python "$here/service.py" --jev-dir "$PWD/jev" --threads "$threads" &
pids+=("$!")
wait -n "${pids[@]}"
