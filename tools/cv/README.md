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

## Synchronized phone + camera session

```sh
cd tools/cv
.venv/bin/python record_session.py --camera 0 [--targets targets.json]
```

Stop Unity's Play mode first: the recorder listens on port 7777 itself. Connect the phone app to
the IP shown in the preview, then press SPACE. Hold the phone in your **right** fist. The prompts
walk you through sync jabs, holding guard, two calibration moves (slide your fist left-right
parallel to the camera, then push it straight at the lens), touching tape targets (if you pass
`--targets`), free punching, then sync jabs again. The start screen warns if AWDL (the AirDrop
radio) is on, since it causes ~100 ms Wi-Fi stalls twice a second; `sudo ifconfig awdl0 down`
turns it off until macOS re-enables it.

Afterwards it writes a report (camera rate, phone rate, dropped samples, network jitter) and runs
MediaPipe heavy (GPU) and full (CPU) on the clip. Everything is timestamped with one laptop clock.
Use `--reprocess recordings/session-<timestamp>` to redo the report and landmarks.

**Touch targets (ground truth):** copy `targets.example.json` to `targets.json`. Put 4-6 pieces of
tape within reach at different heights, sides and distances, and measure each one from the
camera lens with a tape measure. The coordinate convention is in the file.

## Live tracking into the game

```sh
cd tools/cv
.venv/bin/python live_pose.py --camera 0            # heavy model on GPU; --model full --delegate CPU also works
```

Start the game's Play mode first; the script keeps retrying until it can connect. Stand in guard
when tracking starts. A preview window shows the tracked arms, the frame rate and the inference
time (`--no-preview` hides it).
