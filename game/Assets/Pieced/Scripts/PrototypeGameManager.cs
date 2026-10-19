using Pieced.Environment;
using Pieced.Input;
using Pieced.Player;
using UnityEngine;

namespace Pieced
{
    public sealed class PrototypeGameManager : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour resetEventSource;
        [SerializeField] private PlayerPunchController punchController;
        [SerializeField] private PunchingBag bag;
        private IResetEventSource source;

        private void OnEnable()
        {
            source = resetEventSource as IResetEventSource;
            if (source == null) Debug.LogError("Reset event source must implement IResetEventSource.", this);
            else source.ResetRequested += ResetPrototype;
        }
        private void OnDisable() { if (source != null) source.ResetRequested -= ResetPrototype; }
        public void ResetPrototype() { punchController.ResetPunch(); bag.ResetBag(); }
    }
}
