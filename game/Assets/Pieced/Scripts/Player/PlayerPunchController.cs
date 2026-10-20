using Pieced.Input;
using UnityEngine;

namespace Pieced.Player
{
    /// <summary>
    /// Drives one hand's tracking target along scripted punch paths in response to punch events. The physics
    /// glove follows the target, so this can later be swapped for a driver fed by phone/CV hand tracking.
    /// Runs in FixedUpdate so the target moves in lockstep with the physics glove chasing it.
    /// </summary>
    [DefaultExecutionOrder(-10)] // move the tracked hand before the gloves chase it each physics step
    public sealed class PlayerPunchController : MonoBehaviour
    {
        private enum Phase { Guard, Extend, Hold, Retract }

        [SerializeField] private PunchHand hand;
        [SerializeField] private MonoBehaviour[] punchEventSources;
        [SerializeField] private Transform target;
        [SerializeField] private Transform guardPose;
        [SerializeField] private float impactHold = 0.05f;
        [SerializeField] private float retractionDuration = 0.2f;
        [Tooltip("A punch requested mid-punch is queued; it starts as soon as the current one finishes extending.")]
        [SerializeField] private bool bufferInput = true;

        private IPunchEventSource[] sources;
        private Phase phase;
        private float phaseTime;
        private float speedScale = 1f;
        private PunchPath path;
        private Vector3 startPosition, retractFrom;
        private Quaternion startRotation, retractFromRotation;
        private PunchEvent? pending;

        public PunchHand Hand => hand;
        public bool IsPunching => phase != Phase.Guard;

        private void OnEnable()
        {
            sources = new IPunchEventSource[punchEventSources.Length];
            for (int i = 0; i < punchEventSources.Length; i++)
            {
                sources[i] = punchEventSources[i] as IPunchEventSource;
                if (sources[i] == null) Debug.LogError("Punch event sources must implement IPunchEventSource.", this);
                else sources[i].PunchPerformed += HandlePunch;
            }
            ResetPunch();
        }

        private void OnDisable()
        {
            foreach (var source in sources) if (source != null) source.PunchPerformed -= HandlePunch;
        }

        private void HandlePunch(PunchEvent punch)
        {
            if (punch.hand != hand) return;
            if (phase == Phase.Guard || phase == Phase.Retract) Play(punch);
            else if (bufferInput) pending = punch;
        }

        /// <summary>Starts a punch from wherever the target currently is (so combos chain without returning to guard).</summary>
        public void Play(PunchEvent punch)
        {
            path = PunchPaths.For(hand, punch.type);
            speedScale = Mathf.Clamp(punch.intensity, 0.5f, 1.5f);
            startPosition = target.localPosition;
            startRotation = target.localRotation;
            pending = null;
            Enter(Phase.Extend);
        }

        private void FixedUpdate()
        {
            phaseTime += Time.fixedDeltaTime;
            switch (phase)
            {
                case Phase.Extend:
                {
                    float t = Mathf.Clamp01(phaseTime * speedScale / path.extensionDuration);
                    float eased = t * t * (3f - 2f * t);
                    target.localPosition = path.Evaluate(startPosition, eased);
                    target.localRotation = Quaternion.Slerp(startRotation, path.endRotation, eased);
                    if (t >= 1f) Enter(Phase.Hold);
                    break;
                }
                case Phase.Hold:
                    if (phaseTime >= impactHold) BeginRetract();
                    break;
                case Phase.Retract:
                {
                    if (pending.HasValue) { Play(pending.Value); break; }
                    float t = Mathf.Clamp01(phaseTime / retractionDuration);
                    float eased = t * t * (3f - 2f * t);
                    target.localPosition = Vector3.Lerp(retractFrom, guardPose.localPosition, eased);
                    target.localRotation = Quaternion.Slerp(retractFromRotation, guardPose.localRotation, eased);
                    if (t >= 1f) Enter(Phase.Guard);
                    break;
                }
            }
        }

        private void BeginRetract()
        {
            retractFrom = target.localPosition;
            retractFromRotation = target.localRotation;
            Enter(Phase.Retract);
        }

        private void Enter(Phase next) { phase = next; phaseTime = 0f; }

        public void ResetPunch()
        {
            pending = null;
            Enter(Phase.Guard);
            if (target == null || guardPose == null) return;
            target.localPosition = guardPose.localPosition;
            target.localRotation = guardPose.localRotation;
        }
    }
}
