using Pieced.Environment;
using Pieced.Input;
using Pieced.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pieced
{
    /// <summary>On-screen readout of glove tracking and bag physics, toggled with F1.</summary>
    public sealed class PhysicsDebugOverlay : MonoBehaviour
    {
        [SerializeField] private PhysicsGlove[] gloves;
        [SerializeField] private PunchingBag bag;
        [SerializeField] private DemoComboInputProvider demo;
        [SerializeField] private bool visible;

        private GUIStyle style;
        private GloveHit lastHit;
        private bool hasHit;
        private float peakSwing, peakTwistRate;
        private InputAction toggleAction;

        private void Awake()
        {
            toggleAction = new InputAction("Toggle Overlay", InputActionType.Button, "<Keyboard>/f1");
            toggleAction.performed += _ => SetVisible(!visible);
        }

        private void Start() => SetVisible(visible);

        private void SetVisible(bool value)
        {
            visible = value;
            foreach (var glove in gloves) glove.SetDebugVisualsVisible(value);
        }

        private void OnEnable()
        {
            toggleAction.Enable();
            foreach (var glove in gloves) glove.BagHit += OnHit;
        }

        private void OnDisable()
        {
            toggleAction.Disable();
            foreach (var glove in gloves) glove.BagHit -= OnHit;
        }

        private void OnDestroy() => toggleAction.Dispose();

        private void OnHit(GloveHit hit)
        {
            lastHit = hit;
            hasHit = true;
            peakSwing = 0f;
            peakTwistRate = 0f;
        }

        private void FixedUpdate()
        {
            peakSwing = Mathf.Max(peakSwing, bag.SwingAngle);
            peakTwistRate = Mathf.Max(peakTwistRate, Mathf.Abs(bag.TwistRate));
            foreach (var glove in gloves) Debug.DrawRay(glove.Body.position, glove.Velocity * 0.05f, Color.cyan);
        }

        private void OnGUI()
        {
            style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true, padding = new RectOffset(10, 10, 8, 8) };
            if (!visible)
            {
                GUI.Box(new Rect(12, 12, 210, 32), "F1  debug · P  demo · R  reset", style);
                return;
            }

            var text = new System.Text.StringBuilder();
            text.AppendLine("<b>Left</b> F jab · D hook · S uppercut   <b>Right</b> J/Space cross · K hook · L uppercut");
            text.AppendLine($"Shift = power · P demo combos <b>{(demo != null && demo.IsPlaying ? "ON" : "off")}</b> · R reset · F1 hide");
            text.AppendLine();
            foreach (var glove in gloves)
                text.AppendLine($"{glove.Hand,-5}  hand {glove.HandVelocity.magnitude,5:0.0} m/s  glove {glove.Velocity.magnitude,5:0.0} m/s  " +
                                $"hand ahead of glove {glove.TrackingError * 100f,4:0} cm  push {glove.PushForce,4:0} N");
            text.AppendLine($"Bag  swing {bag.SwingAngle,5:0.0}°   twist {bag.TwistAngle,6:0.0}°   spin {bag.TwistRate,5:0.0} rad/s");
            if (hasHit)
            {
                var hitGlove = System.Array.Find(gloves, g => g.Hand == lastHit.hand);
                text.AppendLine($"Last hit  {lastHit.hand}  contact {lastHit.relativeSpeed:0.0} m/s  total impulse {hitGlove.ContactImpulse:0.0} N·s  →  " +
                                $"peak swing {peakSwing:0.0}°, peak spin {peakTwistRate:0.0} rad/s");
            }
            text.Append("Bare fist = tracked hand · see-through glove = physics glove · line = how far the bag holds the glove back");

            GUI.Box(new Rect(12, 12, 700, hasHit ? 170 : 150), text.ToString(), style);
        }
    }
}
