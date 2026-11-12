# Motion stream

The game on the laptop runs a TCP server (`MotionStreamServer`, port **7777**).
The phone app connects to it as a client and streams IMU samples. The CV
pipeline can later connect the same way with its own `role`. The game's
top-right status box (toggle with `F2`) shows the laptop's IP, connected
clients, sample rate, and dropped samples.

## Protocol (version 1)

Newline-delimited JSON over TCP: one UTF-8 JSON object per line, each with a
`type`. The client sends `hello` first and then any number of `motion` messages.
Clients should disable Nagle (`TCP_NODELAY`) so samples aren't batched.

```json
{"type":"hello","role":"phone","name":"Darren's iPhone","protocol":1}
{"type":"motion","seq":1,"t":5123.41,"ax":0.01,"ay":-0.02,"az":0.00,"gx":0.1,"gy":0.0,"gz":-0.3,"qx":0,"qy":0,"qz":0,"qw":1,"grx":0,"gry":0,"grz":-1}
```

| field | meaning |
| --- | --- |
| `role` | `phone` for the iOS app; pick a new role (e.g. `cv`) for other producers |
| `seq` | increments by 1 per sample; gaps are counted as drops |
| `t` | sender clock, seconds (iOS: seconds since boot) |
| `ax ay az` | user acceleration with gravity removed, in g (`CMDeviceMotion.userAcceleration`) |
| `gx gy gz` | rotation rate, rad/s |
| `qx qy qz qw` | attitude quaternion, `xArbitraryZVertical` reference frame |
| `grx gry grz` | gravity direction in the device frame, in g (optional; used to sanity-check attitude) |

The CV client (`tools/cv/live_pose.py`, `role: "cv"`) sends one `pose` message per camera frame:

```json
{"type":"pose","seq":1,"t":1234.567,"model":"heavy_gpu","world":[18 floats],"image":[12 floats],"vis":[6 floats]}
```

The landmark order is left shoulder, right shoulder, left elbow, right elbow, left wrist, right wrist
(MediaPipe pose 11-16). `world` is MediaPipe's world landmarks in metres (origin between the hips,
+x = your left, +y = down, -z = toward the camera). `image` holds normalized image x, y. `vis` is
MediaPipe's visibility, 0-1. In Unity, subscribe to `MotionStreamServer.PoseReceived`.

All vectors are in the phone's device frame, not converted to Unity's axes. The
game currently sends nothing back.

In Unity, subscribe to `MotionStreamServer.SampleReceived`. Events are raised
on the main thread before other scripts' `Update`. `PhoneMotionPunchDetector`
is a placeholder consumer: an acceleration spike over 1.8 g throws a right
straight.

## Testing without a phone

```sh
python3 tools/stream/fake_phone.py            # game running on this laptop
```

The fake phone sends a punch spike every 2 seconds. The automated version is
`Assets/Pieced/Tests/PlayMode/MotionStreamTests.cs`.

## Networking gotchas

- The phone and the laptop must be on the same network. Campus Wi-Fi often
  blocks device-to-device traffic, so if the connection times out, connect the
  laptop to the phone's Personal Hotspot instead.
- The first time the game listens, macOS asks whether Unity may accept incoming
  connections. Click Allow, or check System Settings > Network > Firewall.
- The first time the app connects, iOS asks for Local Network access. If you
  denied it, go to Settings > Privacy & Security > Local Network > app.
