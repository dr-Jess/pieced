using Pieced.Networking;
using UnityEngine;

namespace Pieced.Tracking
{
    /// <summary>
    /// Dead-reckons the phone's pose from the motion stream: orientation straight from CoreMotion's fused attitude,
    /// position by double-integrating gravity-free acceleration rotated into the world. Position drifts without bound
    /// (any acceleration bias grows quadratically); this exists to measure how fast.
    ///
    /// Output frame: Unity axes (x right, y up, z forward), origin at the last reset, with z = the direction the
    /// phone was pointing at reset (its top, or away from its screen if it was held upright).
    /// </summary>
    public sealed class PhonePoseEstimator
    {
        public const float StandardGravity = 9.80665f;
        /// <summary>Gaps longer than this (dropped samples, app paused) are skipped rather than integrated.</summary>
        public const double MaxGap = 0.1;

        /// <summary>Zero the velocity whenever the phone has been still briefly (a "ZUPT"), which bounds drift.</summary>
        public bool ZeroVelocityUpdates { get; set; }
        public float StillAcceleration { get; set; } = 0.04f; // g
        public float StillRotationRate { get; set; } = 0.15f; // rad/s
        public float StillDuration { get; set; } = 0.12f;     // s

        public Vector3 Position { get; private set; }
        public Vector3 Velocity { get; private set; }
        /// <summary>Gravity-free acceleration in the output frame, m/s².</summary>
        public Vector3 WorldAcceleration { get; private set; }
        /// <summary>Absolute orientation (true tilt) with heading relative to the reset.</summary>
        public Quaternion Orientation { get; private set; } = Quaternion.identity;
        /// <summary>Rotation since reset as axis × degrees, in the output frame.</summary>
        public Vector3 RotationSinceReset { get; private set; }
        public double TimeSinceReset { get; private set; }
        public bool IsStill { get; private set; }
        public int SkippedGaps { get; private set; }

        /// <summary>
        /// Angle between measured gravity and "down" as predicted by the attitude, in degrees. Near zero means the
        /// CoreMotion → Unity frame conversion is right. Only meaningful if the sender streams gravity.
        /// </summary>
        public float GravityCheckError { get; private set; }
        /// <summary>Which way the attitude quaternion maps vectors, chosen by which one explains measured gravity.</summary>
        public bool AttitudeIsDeviceToWorld => errorDeviceToWorld <= errorWorldToDevice;

        private bool hasReference;
        private Quaternion heading = Quaternion.identity;
        private Quaternion referenceAttitude = Quaternion.identity;
        private double lastTime;
        private float stillTime;
        private double errorDeviceToWorld, errorWorldToDevice; // kept across resets

        public void Reset()
        {
            Position = Velocity = WorldAcceleration = RotationSinceReset = Vector3.zero;
            TimeSinceReset = 0;
            SkippedGaps = 0;
            stillTime = 0;
            hasReference = false;
        }

        public void Add(MotionSample sample)
        {
            var q = ToUnity(sample.attitude);
            var gravity = ToUnity(sample.gravity);
            bool hasGravity = gravity.sqrMagnitude > 0.25f;
            if (hasGravity)
            {
                errorDeviceToWorld += Vector3.Angle(q * gravity, Vector3.down);
                errorWorldToDevice += Vector3.Angle(Quaternion.Inverse(q) * gravity, Vector3.down);
            }
            var attitude = AttitudeIsDeviceToWorld ? q : Quaternion.Inverse(q); // device → world from here on
            if (hasGravity) GravityCheckError = Vector3.Angle(attitude * gravity, Vector3.down);

            if (!hasReference)
            {
                hasReference = true;
                referenceAttitude = attitude;
                heading = HeadingCorrection(attitude);
                lastTime = sample.deviceTime;
            }

            Orientation = heading * attitude;
            var delta = heading * attitude * Quaternion.Inverse(referenceAttitude) * Quaternion.Inverse(heading);
            delta.ToAngleAxis(out float angle, out var axis);
            if (angle > 180f) angle -= 360f;
            RotationSinceReset = Mathf.Approximately(angle, 0f) ? Vector3.zero : axis * angle;

            double dt = sample.deviceTime - lastTime;
            lastTime = sample.deviceTime;
            if (dt <= 0) return;
            if (dt > MaxGap) { SkippedGaps++; return; }

            float step = (float)dt;
            // CoreMotion's userAcceleration points OPPOSITE to how the phone accelerates (its accelerometer reports
            // gravity's direction, -1 g when flat, rather than the upward force), so negate it. Verified against
            // the camera: with the negation, phone and camera vertical velocities correlate at +0.80.
            WorldAcceleration = Orientation * ToUnity(-sample.userAcceleration) * StandardGravity;
            Velocity += WorldAcceleration * step;
            IsStill = sample.userAcceleration.magnitude < StillAcceleration && sample.rotationRate.magnitude < StillRotationRate;
            stillTime = IsStill ? stillTime + step : 0f;
            if (ZeroVelocityUpdates && stillTime >= StillDuration) Velocity = Vector3.zero;
            Position += Velocity * step;
            TimeSinceReset += dt;
        }

        /// <summary>Yaw rotation that turns the phone's horizontal pointing direction at reset into +Z.</summary>
        private static Quaternion HeadingCorrection(Quaternion attitude)
        {
            var forward = attitude * Vector3.forward; // the phone's top edge
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.1f) // held upright: use the direction the screen faces away from
            {
                forward = -(attitude * Vector3.up);
                forward.y = 0f;
            }
            if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
            return Quaternion.Inverse(Quaternion.LookRotation(forward.normalized, Vector3.up));
        }

        // CoreMotion is right-handed with Z up (device: x right, y toward the top edge, z out of the screen).
        // Unity is left-handed with Y up, so swap y and z; swapping two axes flips handedness, which negates
        // quaternion angles. Device axes in Unity: x right, y out of the screen, z toward the top edge.
        public static Vector3 ToUnity(Vector3 v) => new Vector3(v.x, v.z, v.y);
        public static Quaternion ToUnity(Quaternion q) => new Quaternion(-q.x, -q.z, -q.y, q.w);
    }
}
