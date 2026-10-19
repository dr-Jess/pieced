using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pieced.Input
{
    public sealed class KeyboardInputProvider : MonoBehaviour, IPunchEventSource, IResetEventSource
    {
        public event Action<PunchEvent> PunchPerformed;
        public event Action ResetRequested;

        private InputAction punchAction;
        private InputAction resetAction;

        private void Awake()
        {
            punchAction = new InputAction("Right Straight", InputActionType.Button, "<Keyboard>/space");
            resetAction = new InputAction("Reset", InputActionType.Button, "<Keyboard>/r");
            punchAction.performed += OnPunch;
            resetAction.performed += OnReset;
        }

        private void OnEnable() { punchAction?.Enable(); resetAction?.Enable(); }
        private void OnDisable() { punchAction?.Disable(); resetAction?.Disable(); }

        private void OnDestroy()
        {
            if (punchAction != null) { punchAction.performed -= OnPunch; punchAction.Dispose(); }
            if (resetAction != null) { resetAction.performed -= OnReset; resetAction.Dispose(); }
        }

        private void OnPunch(InputAction.CallbackContext _) => PunchPerformed?.Invoke(
            new PunchEvent(PunchHand.Right, PunchType.Straight, 1f, Time.realtimeSinceStartupAsDouble));
        private void OnReset(InputAction.CallbackContext _) => ResetRequested?.Invoke();
    }
}
