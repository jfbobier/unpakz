#!/usr/bin/env bash
# Rebuilds the committed SPIR-V from the GLSL sources (needs glslc from the Vulkan SDK / shaderc).
set -euo pipefail
cd "$(dirname "$0")/Unpakz.Vulkan/Shaders"
for f in *.comp; do glslc -O --target-env=vulkan1.1 "$f" -o "${f%.comp}.spv"; echo "compiled $f"; done
