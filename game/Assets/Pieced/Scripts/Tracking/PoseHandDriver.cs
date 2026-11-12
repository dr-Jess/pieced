using Pieced.Networking;
using Pieced.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pieced.Tracking
{
    /// <summary>
    /// Drives both tracked hands from the CV client's MediaPipe pose (tools/cv/live_pose.py). Each wrist is
    /// placed relative to its own shoulder, so stepping around doesn't move the hands. The first pose received
    /// is taken as your guard (press C to recalibrate while holding guard): that wrist position maps onto the
    /// scene's guard pose and movement from there is scaled by <see cref="gain"/>. MediaPipe badly under-reads
    /// motion toward the camera, so depth is multiplied (2.5x by default; [ and ] adjust it live).
    ///
    /// While pose data is arriving, the scripted punch controllers are disabled so they don't fight over the
    /// targets; they come back if the CV client stops. The physics gloves keep chasing the targets as before.
    /// Show the tracked (bare) fists with F1.
    /// </summary>
    [DefaultExecutionOrder(-10)] // move the tracked hands before the gloves chase them
    public sealed class PoseHandDriver : MonoBehaviour
    {
        [SerializeField] private MotionStreamServer server;
        [SerializeField] private Transform leftTarget, rightTarget;
        [SerializeField] private Transform leftGuard, rightGuard;
        [SerializeField] private PlayerPunchController leftController, rightController;
        [Tooltip("Game metres per MediaPipe metre: x sideways, y up, z forward. MediaPipe under-reads depth, so z > 1.")]
        [SerializeField] private Vector3 gain = new Vector3(1f, 1f, 2.5f);
        [Tooltip("Ignore a wrist the model is less sure about than this, keeping its last position.")]
        [SerializeField] private float minVisibility = 0.5f;
        [Tooltip("Hand control goes back to the scripted punches if no pose arrives for this long.")]
        [SerializeField] private float staleAfter = 0.5f;
        [Tooltip("Pose frames arrive at ~30 Hz; the target glides to each new one over this time so the physics " +
                 "gloves see smooth motion instead of 30 Hz jumps (adds about half this in latency).")]
        [SerializeField] private float glideTime = 1f / 30f;
        [SerializeField] private bool showStatus = true;

        private struct Hand
        {
            public Transform target, guard;
            public PlayerPunchController controller;
            public int shoulder, wrist;
            public Vector3 neutral;   // wrist - shoulder (game axes) at guard calibration
            public Vector3 from, to;  // glide between pose frames, in the target's parent space
        }

        private Hand[] hands;
        private bool calibrated, driving, calibrateNext;
        private double lastPoseTime = double.NegativeInfinity;
        private float glideStart;
        private long poses;
        private InputAction calibrateAction, depthUpAction, depthDownAction;
        private GUIStyle style;

        public bool IsDriving => driving;

        private void Awake()
        {
            hands = new[]
            {
                new Hand { target = leftTarget, guard = leftGuard, controller = leftController,
                           shoulder = PoseSample.LeftShoulder, wrist = PoseSample.LeftWrist },
                new Hand { target = rightTarget, guard = rightGuard, controller = rightController,
                           shoulder = PoseSample.RightShoulder, wrist = PoseSample.RightWrist },
            };
            calibrateAction = new InputAction("Calibrate Guard", InputActionType.Button, "<Keyboard>/c");
            calibrateAction.performed += _ => calibrateNext = true;
            depthUpAction = new InputAction("Depth Gain Up", InputActionType.Button, "<Keyboard>/rightBracket");
            depthUpAction.performed += _ => gain.z = Mathf.Min(gain.z + 0.25f, 6f);
            depthDownAction = new InputAction("Depth Gain Down", InputActionType.Button, "<Keyboard>/leftBracket");
            depthDownAction.performed += _ => gain.z = Mathf.Max(gain.z - 0.25f, 0.25f);
        }

        private void OnEnable()
        {
            calibrateAction.Enable(); depthUpAction.Enable(); depthDownAction.Enable();
            if (server != null) server.PoseReceived += OnPose;
        }

        private void OnDisable()
        {
            calibrateAction.Disable(); depthUpAction.Disable(); depthDownAction.Disable();
            if (server != null) server.PoseReceived -= OnPose;
            SetDriving(false);
        }

        private void OnDestroy()
        {
            calibrateAction?.Dispose(); depthUpAction?.Dispose(); depthDownAction?.Dispose();
        }

        /// <summary>MediaPipe world axes (+x your left, +y down, -z toward camera) to game camera axes.</summary>
        private static Vector3 ToGame(Vector3 v) => new Vector3(-v.x, -v.y, -v.z);

        private void OnPose(PoseSample pose)
        {
            poses++;
            lastPoseTime = Time.realtimeSinceStartupAsDouble;
            if (!calibrated || calibrateNext)
            {
                for (int i = 0; i < hands.Length; i++)
                    hands[i].neutral = ToGame(pose.world[hands[i].wrist] - pose.world[hands[i].shoulder]);
                calibrated = true;
                calibrateNext = false;
            }
            SetDriving(true);
            for (int i = 0; i < hands.Length; i++)
            {
                ref var hand = ref hands[i];
                if (hand.target == null || hand.guard == null) continue;
                hand.from = hand.target.localPosition;
                if (pose.visibility[hand.wrist] < minVisibility)
                {
                    hand.to = hand.from; // keep the last trusted position
                    continue;
                }
                var offset = ToGame(pose.world[hand.wrist] - pose.world[hand.shoulder]) - hand.neutral;
                hand.to = hand.guard.localPosition + Vector3.Scale(gain, offset);
            }
            glideStart = Time.time;
        }

        private void FixedUpdate()
        {
            if (driving && Time.realtimeSinceStartupAsDouble - lastPoseTime > staleAfter) SetDriving(false);
            if (!driving) return;
            float t = glideTime > 0f ? Mathf.Clamp01((Time.time - glideStart) / glideTime) : 1f;
            foreach (var hand in hands)
            {
                if (hand.target == null) continue;
                hand.target.localPosition = Vector3.Lerp(hand.from, hand.to, t);
                hand.target.localRotation = hand.guard.localRotation;
            }
        }

        private void SetDriving(bool value)
        {
            if (driving == value || hands == null) return;
            driving = value;
            for (int i = 0; i < hands.Length; i++)
            {
                ref var hand = ref hands[i];
                if (hand.target != null) hand.from = hand.to = hand.target.localPosition;
                if (hand.controller == null) continue;
                hand.controller.enabled = !value;          // scripted punches would fight over the target
                if (!value) hand.controller.ResetPunch();  // back to guard when the CV client stops
            }
        }

        private void OnGUI()
        {
            if (!showStatus) return;
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = Color.white } };
            string text = driving
                ? $"Hands: camera pose ({poses} frames). Depth gain {gain.z:0.00}x ([ / ] to change). " +
                  "C = recalibrate guard. F1 = show tracked fists"
                : "Hands: scripted (keyboard). Start tools/cv/live_pose.py to track your arms.";
            var size = style.CalcSize(new GUIContent(text));
            var rect = new Rect(Screen.width - size.x - 12, Screen.height - size.y - 12, size.x + 4, size.y + 4);
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, size.x, size.y), text, style);
        }
    }
}
