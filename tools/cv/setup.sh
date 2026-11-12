#!/bin/sh
# Creates tools/cv/.venv and downloads the MediaPipe pose models. Needs uv (or swap in python3 -m venv + pip).
set -e
cd "$(dirname "$0")"
uv venv --python 3.12 .venv
uv pip install --python .venv/bin/python -r requirements.txt
mkdir -p models
for m in lite full heavy; do
  curl -sfL -o "models/pose_landmarker_$m.task" \
    "https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_$m/float16/latest/pose_landmarker_$m.task"
done
echo "Ready. See README.md for the tools (cd tools/cv first)."
