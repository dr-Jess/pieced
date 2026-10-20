# Pieced Punching Bag Prototype

Open this directory with Unity `6000.6.6f1`. The editor automatically opens
`Assets/Pieced/Scenes/PunchingBagPrototype.unity` when it would otherwise start
with an untitled or temporary scene. After pulling script changes, run
**Pieced > Build Prototype Scene** to regenerate the scene.

To play without the editor, run **Pieced > Build Windows Player** and launch
`Builds/Windows/PiecedPunchingBag.exe` (git-ignored). With the editor closed, the
same build runs from the command line:

```sh
Unity.exe -batchmode -projectPath <path>/game -executeMethod Pieced.Editor.PrototypeSceneBuilder.BuildWindowsDevelopment -logFile build.log
```

## Controls

| Key | Left hand | Right hand |
| --- | --- | --- |
| Straight | `F` | `J` / `Space` |
| Hook | `D` | `K` |
| Uppercut | `S` | `L` |

- Hold `Shift` while punching for a power punch
- `P`: toggle the demo combo loop
- `R`: reset the gloves and punching bag
- `F1`: toggle the physics debug overlay

## How the physics works

Each glove is a dynamic rigidbody that chases a **tracked hand** (`Left/Right
Tracked Hand` under the camera) every physics step. Right now
`PlayerPunchController` moves the tracked hands along scripted Bezier punch
paths (`PunchPaths`); later, a phone/CV hand tracker can drive the same
transforms. The paths intentionally end inside the bag, so the collision, not
the animation, decides where the glove stops.

The real hand and the glove can separate (the player punches air, the bag
blocks the glove), and the bag is driven by the **real hand**:

- **Impact:** during a contact the bag receives `handMass × peak hand speed into
  the bag`, minus what the glove's own collision already delivered. A glove
  already resting on the bag still lands a full punch when the hand snaps.
- **Push:** a capped spring on how far the hand is past the glove
  (`pushStiffness`, `maxPushForce`), like leaning into the bag.

With the overlay on (`F1`), a translucent ghost shows the tracked hand and a
line shows how far the bag is holding the glove back.

The heavy bag is a rigidbody on a ball joint at its suspension point. Gravity
restores swing in any direction, and a weak spring restores twist so hooks spin
the bag (watch the smiley face) and it settles back facing forward. Physics
runs at 100 Hz (`ProjectSettings/TimeManager.asset`).

Tunables live on the components: `PhysicsGlove` (effective punch mass, max
acceleration, follow-through push), `PunchingBag` (mass, swing limit, twist
spring), and `PlayerPunchController` (hold and retraction timing).

## Tests

`Assets/Pieced/Tests/PlayMode` throws scripted punches in the real scene and
checks that gloves are stopped by the bag, hooks spin it in mirrored
directions, and it settles back. Run them from **Window > General > Test
Runner > PlayMode**; each test logs contact speed, impulse, penetration,
swing, and spin for tuning comparisons.

## Project structure

- `Assets/Pieced/Scenes`: playable scenes
- `Assets/Pieced/Scripts`: input, player, and environment behavior (`Pieced.Runtime` assembly)
- `Assets/Pieced/Materials`, `Assets/Pieced/Textures`: URP materials and the generated bag face
- `Assets/Pieced/Settings`: URP renderer and pipeline assets
- `Assets/Pieced/Editor`: scene generation and editor startup utilities
- `Assets/Pieced/Tests/PlayMode`: physics play mode tests

Unity-generated caches, logs, IDE files, and build output are excluded by
`.gitignore`.
