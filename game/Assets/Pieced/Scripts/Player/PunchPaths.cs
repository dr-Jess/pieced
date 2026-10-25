using Pieced.Input;
using UnityEngine;

namespace Pieced.Player
{
    /// <summary>A cubic Bezier glove path in camera-local space, authored for the right hand.</summary>
    public readonly struct PunchPath
    {
        public readonly Vector3 control1;
        public readonly Vector3 control2;
        public readonly Vector3 end;
        public readonly Quaternion endRotation;
        public readonly float extensionDuration;

        public PunchPath(Vector3 control1, Vector3 control2, Vector3 end, Vector3 endEuler, float extensionDuration)
        {
            this.control1 = control1;
            this.control2 = control2;
            this.end = end;
            endRotation = Quaternion.Euler(endEuler);
            this.extensionDuration = extensionDuration;
        }

        public Vector3 Evaluate(Vector3 start, float t)
        {
            float u = 1f - t;
            return u * u * u * start + 3f * u * u * t * control1 + 3f * u * t * t * control2 + t * t * t * end;
        }

        /// <summary>Mirrors the path across the camera's vertical plane for the left hand.</summary>
        public PunchPath Mirrored()
        {
            var euler = endRotation.eulerAngles;
            return new PunchPath(Mirror(control1), Mirror(control2), Mirror(end), new Vector3(euler.x, -euler.y, -euler.z), extensionDuration);
        }

        private static Vector3 Mirror(Vector3 v) => new Vector3(-v.x, v.y, v.z);
    }

    /// <summary>
    /// Scripted stand-ins for tracked hand motion. Paths deliberately end inside the bag so the physics glove,
    /// not the animation, decides where the punch stops.
    /// </summary>
    public static class PunchPaths
    {
        private static readonly PunchPath Straight = new PunchPath(
            new Vector3(0.16f, -0.20f, 0.55f), new Vector3(0.06f, -0.13f, 0.80f), new Vector3(0.04f, -0.08f, 0.92f),
            new Vector3(0f, 0f, 0f), 0.15f);

        private static readonly PunchPath Hook = new PunchPath(
            new Vector3(0.55f, -0.16f, 0.55f), new Vector3(0.55f, -0.12f, 0.85f), new Vector3(-0.30f, -0.12f, 0.85f),
            new Vector3(5f, -35f, -80f), 0.22f);

        private static readonly PunchPath Uppercut = new PunchPath(
            new Vector3(0.14f, -0.62f, 0.40f), new Vector3(0.08f, -0.55f, 0.85f), new Vector3(0.05f, 0.05f, 0.85f),
            new Vector3(-35f, -5f, -20f), 0.20f);

        public static PunchPath For(PunchHand hand, PunchType type)
        {
            var path = type switch
            {
                PunchType.Hook => Hook,
                PunchType.Uppercut => Uppercut,
                _ => Straight,
            };
            return hand == PunchHand.Left ? path.Mirrored() : path;
        }
    }
}
