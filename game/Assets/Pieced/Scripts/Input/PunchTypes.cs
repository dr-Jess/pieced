using System;

namespace Pieced.Input
{
    public enum PunchHand { Left, Right }
    public enum PunchType { Straight, Hook, Uppercut }

    [Serializable]
    public struct PunchEvent
    {
        public PunchHand hand;
        public PunchType type;
        public float intensity;
        public double timestamp;

        public PunchEvent(PunchHand hand, PunchType type, float intensity, double timestamp)
        {
            this.hand = hand;
            this.type = type;
            this.intensity = intensity;
            this.timestamp = timestamp;
        }
    }

    public interface IPunchEventSource
    {
        event Action<PunchEvent> PunchPerformed;
    }

    public interface IResetEventSource
    {
        event Action ResetRequested;
    }
}
