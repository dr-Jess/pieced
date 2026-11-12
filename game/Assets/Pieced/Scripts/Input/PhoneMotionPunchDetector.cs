using System;
using Pieced.Networking;
using UnityEngine;

namespace Pieced.Input
{
    /// <summary>
    /// Placeholder bridge from the phone's IMU stream to punch events: a sharp spike in acceleration throws a
    /// straight with the configured hand. It exists to prove the phone → laptop → game loop end to end; real punch
    /// classification (and the CV hand tracking) should replace it.
    /// </summary>
    public sealed class PhoneMotionPunchDetector : MonoBehaviour, IPunchEventSource
    {
        [SerializeField] private MotionStreamServer server;
        [SerializeField] private PunchHand hand = PunchHand.Right;
        [SerializeField] private PunchType punchType = PunchType.Straight;
        [Tooltip("User acceleration (gravity removed), in g, that counts as a punch.")]
        [SerializeField] private float triggerAcceleration = 1.8f;
        [Tooltip("Acceleration must fall below this before another punch can trigger.")]
        [SerializeField] private float rearmAcceleration = 0.6f;
        [SerializeField] private float cooldown = 0.25f;

        public event Action<PunchEvent> PunchPerformed;

        private bool armed = true;
        private double lastPunchTime = double.NegativeInfinity;

        private void OnEnable() { if (server != null) server.SampleReceived += OnSample; }
        private void OnDisable() { if (server != null) server.SampleReceived -= OnSample; }

        private void OnSample(MotionSample sample)
        {
            if (sample.role != "phone") return;
            float magnitude = sample.userAcceleration.magnitude;
            if (!armed)
            {
                if (magnitude < rearmAcceleration) armed = true;
                return;
            }
            if (magnitude < triggerAcceleration || sample.receivedTime - lastPunchTime < cooldown) return;

            armed = false;
            lastPunchTime = sample.receivedTime;
            float intensity = Mathf.Clamp(magnitude / triggerAcceleration, 0.8f, 1.5f);
            PunchPerformed?.Invoke(new PunchEvent(hand, punchType, intensity, sample.receivedTime));
        }
    }
}
