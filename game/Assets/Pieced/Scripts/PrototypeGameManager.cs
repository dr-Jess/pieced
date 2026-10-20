using Pieced.Environment;
using Pieced.Input;
using Pieced.Player;
using UnityEngine;

namespace Pieced
{
    public sealed class PrototypeGameManager : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour resetEventSource;
        [SerializeField] private PlayerPunchController[] punchControllers;
        [SerializeField] private PhysicsGlove[] gloves;
        [SerializeField] private PunchingBag bag;
        private IResetEventSource source;

        private void Awake()
        {
            // Gloves only interact with the bag and room, never with each other.
            for (int i = 0; i < gloves.Length; i++)
                for (int j = i + 1; j < gloves.Length; j++)
                    foreach (var a in gloves[i].GetComponentsInChildren<Collider>())
                        foreach (var b in gloves[j].GetComponentsInChildren<Collider>())
                            Physics.IgnoreCollision(a, b);
        }

        private void OnEnable()
        {
            source = resetEventSource as IResetEventSource;
            if (source == null) Debug.LogError("Reset event source must implement IResetEventSource.", this);
            else source.ResetRequested += ResetPrototype;
        }
        private void OnDisable() { if (source != null) source.ResetRequested -= ResetPrototype; }

        public void ResetPrototype()
        {
            foreach (var controller in punchControllers) controller.ResetPunch();
            foreach (var glove in gloves) glove.SnapToTarget();
            bag.ResetBag();
        }
    }
}
