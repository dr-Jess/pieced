# Pieced Punching Bag Prototype

Open this directory with Unity `6000.6.6f1`. The editor automatically opens
`Assets/Pieced/Scenes/PunchingBagPrototype.unity` when it would otherwise start
with an untitled or temporary scene.

## Controls

- `Space`: right straight punch
- `R`: reset the glove and punching bag

Use the **Game** view and enter Play Mode to run the prototype. The glove's
`Right Glove Rest Pose` and `Right Glove Impact Pose` transforms can be adjusted
in the scene hierarchy without changing code.

## Project structure

- `Assets/Pieced/Scenes`: playable scenes
- `Assets/Pieced/Scripts`: input, player, and environment behavior
- `Assets/Pieced/Materials`: URP materials
- `Assets/Pieced/Settings`: URP renderer and pipeline assets
- `Assets/Pieced/Editor`: scene generation and editor startup utilities

Unity-generated caches, logs, IDE files, and build output are excluded by
`.gitignore`.
