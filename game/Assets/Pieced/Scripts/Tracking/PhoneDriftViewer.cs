using Pieced.Networking;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pieced.Tracking
{
    /// <summary>
    /// Moves a phone-sized prism by the dead-reckoned pose of the streaming phone and graphs position and rotation
    /// since the last reset, to see how fast IMU-only tracking drifts. R resets, Z toggles zero-velocity updates,
    /// F toggles the camera following the phone, C clears the trail.
    /// </summary>
    public sealed class PhoneDriftViewer : MonoBehaviour
    {
        [SerializeField] private MotionStreamServer server;
        [SerializeField] private Transform phone;
        [SerializeField] private LineRenderer trail;
        [SerializeField] private Transform cameraRig;
        [SerializeField] private float graphWindow = 15f;
        [SerializeField] private bool zeroVelocityUpdates;
        [SerializeField] private bool followPhone;

        private struct Point { public float time; public Vector3 position, rotation; }

        private const int HistorySize = 4096; // > 15 s at 100 Hz
        private const int MaxTrailPoints = 3000;

        public PhonePoseEstimator Estimator { get; } = new PhonePoseEstimator();

        private readonly Point[] history = new Point[HistorySize];
        private int historyStart, historyCount;
        private readonly Vector3[] trailPoints = new Vector3[MaxTrailPoints];
        private int trailCount;
        private int trackedClient = -1;
        private long samples;
        private Vector3 cameraOffset;
        private Material lineMaterial;
        private GUIStyle textStyle, smallStyle;
        private InputAction resetAction, zuptAction, followAction, clearAction;

        private void Awake()
        {
            resetAction = Key("<Keyboard>/r", ResetPose);
            zuptAction = Key("<Keyboard>/z", () => zeroVelocityUpdates = !zeroVelocityUpdates);
            followAction = Key("<Keyboard>/f", () => followPhone = !followPhone);
            clearAction = Key("<Keyboard>/c", ClearTrail);
            if (cameraRig != null) cameraOffset = cameraRig.position;
            lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            ResetPose();
        }

        private InputAction Key(string binding, System.Action onPress)
        {
            var action = new InputAction(binding, InputActionType.Button, binding);
            action.performed += _ => onPress();
            return action;
        }

        private void OnEnable()
        {
            foreach (var a in new[] { resetAction, zuptAction, followAction, clearAction }) a.Enable();
            server.SampleReceived += OnSample;
            server.ClientDisconnected += OnClientDisconnected;
        }

        private void OnDisable()
        {
            foreach (var a in new[] { resetAction, zuptAction, followAction, clearAction }) a.Disable();
            server.SampleReceived -= OnSample;
            server.ClientDisconnected -= OnClientDisconnected;
        }

        private void OnDestroy()
        {
            foreach (var a in new[] { resetAction, zuptAction, followAction, clearAction }) a.Dispose();
            if (lineMaterial != null) Destroy(lineMaterial);
        }

        public void ResetPose()
        {
            Estimator.Reset();
            historyStart = historyCount = 0;
            ClearTrail();
            if (phone != null) phone.SetPositionAndRotation(Vector3.zero, Estimator.Orientation);
        }

        private void ClearTrail()
        {
            trailCount = 0;
            if (trail != null) trail.positionCount = 0;
        }

        private void OnClientDisconnected(StreamClientInfo client)
        {
            if (client.Id == trackedClient) trackedClient = -1;
        }

        private void OnSample(MotionSample sample)
        {
            if (sample.role != "phone") return;
            if (trackedClient != sample.clientId)
            {
                if (trackedClient != -1) return; // follow the first phone only
                trackedClient = sample.clientId;
                ResetPose();
            }
            samples++;
            Estimator.ZeroVelocityUpdates = zeroVelocityUpdates;
            Estimator.Add(sample);

            var point = new Point { time = (float)Estimator.TimeSinceReset, position = Estimator.Position, rotation = Estimator.RotationSinceReset };
            history[(historyStart + historyCount) % HistorySize] = point;
            if (historyCount < HistorySize) historyCount++;
            else historyStart = (historyStart + 1) % HistorySize;

            if (trailCount == 0 || (trailCount < MaxTrailPoints && Vector3.Distance(trailPoints[trailCount - 1], Estimator.Position) > 0.002f))
                trailPoints[trailCount++] = Estimator.Position;
        }

        private void LateUpdate()
        {
            if (phone != null) phone.SetPositionAndRotation(Estimator.Position, Estimator.Orientation);
            if (trail != null)
            {
                trail.positionCount = trailCount;
                trail.SetPositions(trailPoints);
            }
            if (cameraRig != null) cameraRig.position = followPhone ? Estimator.Position + cameraOffset : cameraOffset;
        }

        private ref Point At(int i) => ref history[(historyStart + i) % HistorySize];

        private void OnGUI()
        {
            textStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, normal = { textColor = Color.white } };
            smallStyle ??= new GUIStyle(textStyle) { fontSize = 12 };

            if (GUI.Button(new Rect(12, 12, 180, 40), "Reset to zero (R)")) ResetPose();
            zeroVelocityUpdates = GUI.Toggle(new Rect(204, 12, 260, 20), zeroVelocityUpdates, " Zero velocity when still (Z)");
            followPhone = GUI.Toggle(new Rect(204, 32, 260, 20), followPhone, " Camera follows phone (F)");

            var e = Estimator;
            string status = trackedClient == -1
                ? "<color=#ffb347>Waiting for the phone app to connect...</color>"
                : $"Time since reset {e.TimeSinceReset:0.0} s   samples {samples}   skipped gaps {e.SkippedGaps}\n" +
                  $"Position drift  <b>{e.Position.magnitude:0.000} m</b>   speed {e.Velocity.magnitude:0.000} m/s   {(e.IsStill ? "still" : "moving")}\n" +
                  $"Rotation since reset  <b>{e.RotationSinceReset.magnitude:0.0}°</b>\n" +
                  $"Frame check: gravity error {e.GravityCheckError:0.0}°  ({(e.AttitudeIsDeviceToWorld ? "device→world" : "world→device")})";
            GUI.Label(new Rect(12, 58, 700, 80), status, textStyle);

            float width = Mathf.Min(520f, Screen.width - 24f), height = 150f;
            var rotationRect = new Rect(12, Screen.height - height - 12, width, height);
            var positionRect = new Rect(12, rotationRect.y - height - 30, width, height);
            DrawGraph(positionRect, "Position since reset", p => p.position, "m", 0.05f, "0.000");
            DrawGraph(rotationRect, "Rotation since reset (axis × angle)", p => p.rotation, "°", 5f, "0.0");
        }

        private void DrawGraph(Rect rect, string title, System.Func<Point, Vector3> select, string unit, float minRange, string format)
        {
            float now = historyCount > 0 ? At(historyCount - 1).time : 0f;
            float start = now - graphWindow;
            float maxAbs = 0f;
            for (int i = 0; i < historyCount; i++)
            {
                if (At(i).time < start) continue;
                var v = select(At(i));
                maxAbs = Mathf.Max(maxAbs, Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
            }
            float range = Mathf.Max(minRange, maxAbs * 1.1f);

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            var latest = historyCount > 0 ? select(At(historyCount - 1)) : Vector3.zero;
            GUI.Label(new Rect(rect.x + 6, rect.y - 22, rect.width, 22),
                $"{title}   <color=#ff6b6b>x {latest.x.ToString(format)}</color>  <color=#7bed7b>y {latest.y.ToString(format)}</color>  " +
                $"<color=#6bb5ff>z {latest.z.ToString(format)}</color> {unit}", textStyle);
            GUI.Label(new Rect(rect.xMax - 110, rect.y + 2, 106, 18), $"±{range.ToString(format)} {unit}, {graphWindow:0} s", smallStyle);

            if (Event.current.type != EventType.Repaint) return;
            lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            float Y(float value) => Screen.height - (rect.center.y - value / range * rect.height * 0.5f);
            float X(float time) => rect.x + (time - start) / graphWindow * rect.width;
            GL.Color(new Color(1f, 1f, 1f, 0.25f));
            GL.Vertex3(rect.x, Y(0f), 0f); GL.Vertex3(rect.xMax, Y(0f), 0f);
            var colors = new[] { new Color(1f, 0.42f, 0.42f), new Color(0.48f, 0.93f, 0.48f), new Color(0.42f, 0.71f, 1f) };
            for (int axis = 0; axis < 3; axis++)
            {
                GL.Color(colors[axis]);
                bool hasPrevious = false;
                Vector2 previous = default;
                for (int i = 0; i < historyCount; i++)
                {
                    var p = At(i);
                    if (p.time < start) continue;
                    var current = new Vector2(X(p.time), Y(select(p)[axis]));
                    if (hasPrevious) { GL.Vertex3(previous.x, previous.y, 0f); GL.Vertex3(current.x, current.y, 0f); }
                    previous = current;
                    hasPrevious = true;
                }
            }
            GL.End();
            GL.PopMatrix();
        }
    }
}
