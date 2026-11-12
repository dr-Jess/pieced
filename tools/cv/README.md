# CV experiments

MediaPipe pose tracking from the laptop webcam. Setup (creates `.venv/`, downloads models; both git-ignored):

```sh
tools/cv/setup.sh
```

## Pose benchmark

```sh
cd tools/cv
.venv/bin/python benchmark_pose.py --list-cameras
.venv/bin/python benchmark_pose.py --camera <index>
```

It records one clip: 4 s to get in position, 6 s holding your guard still, then 12 s of punching.
Then it runs lite / full / heavy on both CPU and GPU (Metal) over the same frames. It prints
inference time, detection rate, wrist jitter at rest (pixels, % of shoulder width, world mm,
depth mm), tracking loss while punching, and each model's wrist offset from heavy's.

Each run goes in `recordings/<timestamp>/`: `clip.avi`, frame timestamps, `summary.txt`, and
per-frame shoulder, elbow and wrist landmarks for every model as CSV. Re-run the analysis with
`--analyze recordings/<timestamp>`.

The first run asks for camera permission for your terminal app.

## Live tracking into the game

```sh
cd tools/cv
.venv/bin/python live_pose.py --camera 0            # heavy model on GPU; --model full --delegate CPU also works
```

Start the game's Play mode first; the script keeps retrying until it can connect. Stand in guard
when tracking starts. A preview window shows the tracked arms, the frame rate and the inference
time (`--no-preview` hides it).
