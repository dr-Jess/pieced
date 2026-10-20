using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pieced.Input
{
    /// <summary>Loops scripted combos to show off the glove/bag physics hands-free. Toggle with P.</summary>
    public sealed class DemoComboInputProvider : MonoBehaviour, IPunchEventSource
    {
        private readonly struct Step
        {
            public readonly PunchHand hand;
            public readonly PunchType type;
            public readonly float intensity;
            public readonly float delayAfter;

            public Step(PunchHand hand, PunchType type, float intensity, float delayAfter)
            {
                this.hand = hand; this.type = type; this.intensity = intensity; this.delayAfter = delayAfter;
            }
        }

        private const PunchHand L = PunchHand.Left;
        private const PunchHand R = PunchHand.Right;

        private static readonly Step[][] Combos =
        {
            new[] { new Step(L, PunchType.Straight, 1f, 0.3f), new Step(R, PunchType.Straight, 1.2f, 1.6f) },
            new[] { new Step(R, PunchType.Hook, 1.4f, 2.4f) },
            new[] { new Step(L, PunchType.Hook, 1.4f, 2.4f) },
            new[] { new Step(L, PunchType.Straight, 1f, 0.3f), new Step(R, PunchType.Straight, 1f, 0.35f), new Step(L, PunchType.Hook, 1.3f, 1.8f) },
            new[] { new Step(R, PunchType.Uppercut, 1.4f, 1.8f) },
            new[] { new Step(L, PunchType.Uppercut, 1f, 0.35f), new Step(R, PunchType.Hook, 1.3f, 0.4f), new Step(L, PunchType.Hook, 1.3f, 2.2f) },
            new[] { new Step(R, PunchType.Hook, 1.5f, 0.45f), new Step(R, PunchType.Hook, 1.5f, 0.45f), new Step(R, PunchType.Hook, 1.5f, 3f) },
        };

        [SerializeField] private bool playOnStart;

        public event Action<PunchEvent> PunchPerformed;
        public bool IsPlaying => routine != null;

        private InputAction toggleAction;
        private Coroutine routine;

        private void Awake()
        {
            toggleAction = new InputAction("Toggle Demo", InputActionType.Button, "<Keyboard>/p");
            toggleAction.performed += OnToggle;
        }

        private void Start() { if (playOnStart) StartDemo(); }
        private void OnEnable() => toggleAction?.Enable();
        private void OnDisable() { toggleAction?.Disable(); StopDemo(); }

        private void OnDestroy()
        {
            if (toggleAction == null) return;
            toggleAction.performed -= OnToggle;
            toggleAction.Dispose();
        }

        private void OnToggle(InputAction.CallbackContext _) { if (IsPlaying) StopDemo(); else StartDemo(); }

        public void StartDemo() { if (routine == null) routine = StartCoroutine(Run()); }

        public void StopDemo()
        {
            if (routine != null) StopCoroutine(routine);
            routine = null;
        }

        private IEnumerator Run()
        {
            while (true)
            {
                foreach (var combo in Combos)
                {
                    foreach (var step in combo)
                    {
                        PunchPerformed?.Invoke(new PunchEvent(step.hand, step.type, step.intensity, Time.realtimeSinceStartupAsDouble));
                        yield return new WaitForSeconds(step.delayAfter);
                    }
                }
            }
        }
    }
}
