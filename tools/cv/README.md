# CV experiments

MediaPipe pose tracking from the laptop webcam. Setup (creates `.venv/`, downloads models; both git-ignored):

```sh
tools/cv/setup.sh
```

## Live tracking into the game

```sh
cd tools/cv
.venv/bin/python live_pose.py --camera 0            # heavy model on GPU; --model full --delegate CPU also works
```

Start the game's Play mode first; the script keeps retrying until it can connect. Stand in guard
when tracking starts. A preview window shows the tracked arms, the frame rate and the inference
time (`--no-preview` hides it).
