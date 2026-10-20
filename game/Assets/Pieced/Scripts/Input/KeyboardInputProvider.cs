using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pieced.Input
{
    public sealed class KeyboardInputProvider : MonoBehaviour, IPunchEventSource, IResetEventSource
    {
        private const float NormalIntensity = 1f;
        private const float PowerIntensity = 1.4f;

        // Left hand on the left side of the keyboard, right hand on the right.
        private static readonly (string binding, PunchHand hand, PunchType type)[] Bindings =
        {
            ("<Keyboard>/f", PunchHand.Left, PunchType.Straight),
            ("<Keyboard>/d", PunchHand.Left, PunchType.Hook),
            ("<Keyboard>/s", PunchHand.Left, PunchType.Uppercut),
            ("<Keyboard>/j", PunchHand.Right, PunchType.Straight),
            ("<Keyboard>/space", PunchHand.Right, PunchType.Straight),
            ("<Keyboard>/k", PunchHand.Right, PunchType.Hook),
            ("<Keyboard>/l", PunchHand.Right, PunchType.Uppercut),
        };

        public event Action<PunchEvent> PunchPerformed;
        public event Action ResetRequested;

        private readonly List<InputAction> punchActions = new List<InputAction>();
        private InputAction resetAction;

        private void Awake()
        {
            foreach (var (binding, hand, type) in Bindings)
            {
                var action = new InputAction($"{hand} {type}", InputActionType.Button, binding);
                action.performed += _ => Punch(hand, type);
                punchActions.Add(action);
            }
            resetAction = new InputAction("Reset", InputActionType.Button, "<Keyboard>/r");
            resetAction.performed += OnReset;
        }

        private void OnEnable() { foreach (var action in punchActions) action.Enable(); resetAction?.Enable(); }
        private void OnDisable() { foreach (var action in punchActions) action.Disable(); resetAction?.Disable(); }

        private void OnDestroy()
        {
            foreach (var action in punchActions) action.Dispose();
            if (resetAction != null) { resetAction.performed -= OnReset; resetAction.Dispose(); }
        }

        private void Punch(PunchHand hand, PunchType type)
        {
            bool power = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            PunchPerformed?.Invoke(new PunchEvent(hand, type, power ? PowerIntensity : NormalIntensity, Time.realtimeSinceStartupAsDouble));
        }

        private void OnReset(InputAction.CallbackContext _) => ResetRequested?.Invoke();
    }
}
