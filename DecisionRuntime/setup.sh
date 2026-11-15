#!/usr/bin/env bash
set -euo pipefail
root=${1:-/tmp/magicprompt-decisions}
jobs=${2:-8}
mkdir -p "$root"
cd "$root"

startlux_rev=a99977d9332decc9fb27a2488b4a83b533b6cd5f
startlux_llama_rev=aa94f2086147cd4bfbcff818ce998dffce5f
jev_llama_rev=441df11f65ea0b6d0c72965aaf70c8241070ddcb
startlux_repo=startlux-models/StartLux-Decision-v1.1-0.8B-Instant
jev_repo=chaoliangUNSW/Jev-Style-2B-Decision-v4.2-GGUF
jev_release=7ceb7058f83472a05b049ad48098664642d91e71
cpu_flags=(-DGGML_CUDA=OFF -DGGML_HIP=OFF -DGGML_VULKAN=OFF -DLLAMA_OPENSSL=OFF)
startlux_source=StartLux-Decision-$startlux_rev
startlux_llama=llama.cpp-$startlux_llama_rev
jev_llama=llama.cpp-$jev_llama_rev

download() {
    local url=$1 destination=$2
    if [[ ! -s "$destination" ]]; then
        curl -fsSL --retry 3 "$url" -o "$destination.part"
        mv "$destination.part" "$destination"
    fi
}
source_archive() {
    local repo=$1 rev=$2 directory=$3
    if [[ ! -f "$directory/.extracted" ]]; then
        download "https://codeload.github.com/$repo/tar.gz/$rev" "$directory.tar.gz"
        mkdir -p "$directory"
        tar xzf "$directory.tar.gz" --strip-components=1 -C "$directory"
        touch "$directory/.extracted"
    fi
}
model() {
    local url=$1 destination=$2 expected=$3
    download "$url?download=true" "$destination"
    if ! printf '%s  %s\n' "$expected" "$destination" | sha256sum --check --status; then
        printf 'SHA-256 check failed for %s. Remove the file and retry.\n' "$destination" >&2
        exit 1
    fi
}

source_archive StartLuxLabs/StartLux-Decision "$startlux_rev" "$startlux_source"
source_archive ggml-org/llama.cpp "$startlux_llama_rev" "$startlux_llama"
source_archive ggml-org/llama.cpp "$jev_llama_rev" "$jev_llama"
patch="$PWD/$startlux_source/patches/llama.cpp/0001-server-bidirectional-prompts.patch"
if ! (cd "$startlux_llama" && git apply --reverse --check "$patch") 2>/dev/null; then
    (cd "$startlux_llama" && git apply "$patch")
fi
cmake -S "$startlux_llama" -B "$startlux_llama/build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    "${cpu_flags[@]}" -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF
cmake --build "$startlux_llama/build" --target llama-server --parallel "$jobs"

mkdir -p startlux jev/tokenizer
startlux_files=https://huggingface.co/$startlux_repo/resolve/6496ae641cc71b149ce9dad69bd97f2a44e7bf11
for file in config.json decision_config.json merges.txt tokenizer.json tokenizer_config.json vocab.json; do
    download "$startlux_files/$file" "startlux/$file"
done
jev_files=https://huggingface.co/$jev_repo/resolve/$jev_release
for file in build_jev_score.sh jev_score_v2.cpp jev_style_decision_gguf.py manifest.json \
    readout_config.json tokenizer/tokenizer.json tokenizer/tokenizer_config.json; do
    download "$jev_files/$file" "jev/$file"
done
startlux_gguf=StartLux-Decision-v1.1-0.8B-Instant-Q8_0.gguf
jev_gguf=Jev-Style-2B-Decision-v4.2-Q8_0.gguf
model "https://huggingface.co/$startlux_repo-GGUF/resolve/529edd1b9dae38fda3ba6a5ab0cbc785c91e8359/$startlux_gguf" \
    "startlux/$startlux_gguf" 40060f7f7abbe7a3a537921d892da4503f93b1d548e73eb83e5b67ce5ed91444
model "$jev_files/$jev_gguf" "jev/$jev_gguf" 328a532570fce3d8567362e66d61144041838cc925badc272f17ac3c55a99c83
cmake -S "$jev_llama" -B "$jev_llama/build" -DCMAKE_BUILD_TYPE=Release \
    "${cpu_flags[@]}" -DBUILD_SHARED_LIBS=ON -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_SERVER=OFF
cmake --build "$jev_llama/build" --target llama --parallel "$jobs"
sh jev/build_jev_score.sh "$jev_llama"

if [[ ! -x venv/bin/python ]]; then
    python3 -m venv venv
fi
venv/bin/python -m pip install "./$startlux_source" \
    'aiohttp==3.14.5' 'transformers==5.23.0' 'jinja2==3.1.6' 'tokenizers==0.23.2' 'numpy==2.5.3'
venv/bin/python -m pip check
