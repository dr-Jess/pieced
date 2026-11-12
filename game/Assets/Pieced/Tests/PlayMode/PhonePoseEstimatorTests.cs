using NUnit.Framework;
using Pieced.Networking;
using Pieced.Tracking;
using UnityEngine;

namespace Pieced.Tests
{
    /// <summary>
    /// Feeds synthetic CoreMotion samples (right-handed, Z up) to the estimator. Device frame: x right,
    /// y toward the top edge, z out of the screen. CoreMotion reports userAcceleration opposite to the
    /// actual acceleration, so a phone accelerating toward its top edge reports negative y.
    /// </summary>
    public sealed class PhonePoseEstimatorTests
    {
        private const double Dt = 0.01;
        private static readonly Quaternion Flat = Quaternion.identity; // lying flat, screen up, top pointing "north"
        private static readonly Vector3 FlatGravity = new Vector3(0, 0, -1);

        private static MotionSample Sample(int i, Vector3 accel, Quaternion attitude, Vector3 gravity, Vector3 gyro = default) =>
            new MotionSample(1, "phone", i, 1000 + i * Dt, 0, accel, gyro, attitude, gravity);

        /// <summary>CoreMotion-style quaternion for a right-handed rotation about an axis.</summary>
        private static Quaternion CmRotation(Vector3 axis, float degrees)
        {
            axis.Normalize();
            float half = degrees * Mathf.Deg2Rad / 2f, s = Mathf.Sin(half);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(half));
        }

        private static PhonePoseEstimator Run(int count, System.Func<int, MotionSample> sample, PhonePoseEstimator estimator = null)
        {
            estimator ??= new PhonePoseEstimator();
            for (int i = 0; i < count; i++) estimator.Add(sample(i));
            return estimator;
        }

        [Test]
        public void StillPhone_DoesNotMove()
        {
            var e = Run(500, i => Sample(i, Vector3.zero, Flat, FlatGravity));
            Assert.That(e.Position.magnitude, Is.LessThan(1e-6f));
            Assert.That(e.RotationSinceReset.magnitude, Is.LessThan(1e-3f));
            Assert.That(e.GravityCheckError, Is.LessThan(0.1f));
            Assert.That(e.TimeSinceReset, Is.EqualTo(4.99).Within(1e-6));
        }

        [Test]
        public void ConstantAccelerationTowardTopEdge_IntegratesForward()
        {
            // 0.1 g toward the top edge for 1 s: x = a t² / 2 ≈ 0.49 m forward (+Z).
            var e = Run(101, i => Sample(i, new Vector3(0, -0.1f, 0), Flat, FlatGravity));
            Assert.That(e.Position.z, Is.EqualTo(0.5f * 0.1f * PhonePoseEstimator.StandardGravity).Within(0.01f));
            Assert.That(Mathf.Abs(e.Position.x) + Mathf.Abs(e.Position.y), Is.LessThan(1e-4f));
            Assert.That(e.Velocity.z, Is.EqualTo(0.1f * PhonePoseEstimator.StandardGravity).Within(0.01f));
        }

        [Test]
        public void ForwardIsWhereverThePhonePointedAtReset()
        {
            // Phone turned 90° about vertical: its top points along CoreMotion's -x. Pushing toward the top edge
            // must still read as +Z (forward) because heading is zeroed at reset.
            var yawed = CmRotation(Vector3.forward, 90f); // CoreMotion Z is up
            var e = Run(101, i => Sample(i, new Vector3(0, -0.1f, 0), yawed, FlatGravity));
            Assert.That(e.Position.z, Is.GreaterThan(0.45f));
            Assert.That(Mathf.Abs(e.Position.x), Is.LessThan(1e-3f));
        }

        [Test]
        public void UprightPhone_GravityCheckPassesAndUpIsUp()
        {
            // Portrait, screen facing the player: rotate +90° about the device x axis so the top edge points up.
            var upright = CmRotation(Vector3.right, 90f);
            var gravity = new Vector3(0, -1, 0); // gravity pulls toward the bottom edge
            // Push toward the top edge, which is now world up.
            var e = Run(101, i => Sample(i, new Vector3(0, -0.1f, 0), upright, gravity));
            Assert.That(e.GravityCheckError, Is.LessThan(0.5f));
            Assert.That(e.Position.y, Is.GreaterThan(0.45f), "Top edge points up, so pushing toward it should rise.");
            Assert.That(Mathf.Abs(e.Position.x) + Mathf.Abs(e.Position.z), Is.LessThan(1e-3f));
        }

        [Test]
        public void AttitudeConvention_IsDetectedFromGravity()
        {
            // Same physical pose, but the quaternion in the opposite convention: the estimator should pick the
            // convention under which measured gravity points down.
            var inverted = Quaternion.Inverse(CmRotation(Vector3.right, 90f));
            var e = Run(20, i => Sample(i, Vector3.zero, inverted, new Vector3(0, -1, 0)));
            Assert.That(e.AttitudeIsDeviceToWorld, Is.False);
            Assert.That(e.GravityCheckError, Is.LessThan(0.5f));
        }

        [Test]
        public void Rotation_IsReportedRelativeToReset()
        {
            var e = new PhonePoseEstimator();
            e.Add(Sample(0, Vector3.zero, Flat, FlatGravity));
            e.Add(Sample(1, Vector3.zero, CmRotation(Vector3.forward, 30f), FlatGravity));
            Assert.That(e.RotationSinceReset.magnitude, Is.EqualTo(30f).Within(0.1f));
            Assert.That(Mathf.Abs(e.RotationSinceReset.y), Is.EqualTo(30f).Within(0.1f), "Yaw is about Unity's up axis.");
        }

        [Test]
        public void Reset_ZeroesPositionAndRotation()
        {
            var e = Run(101, i => Sample(i, new Vector3(0.05f, 0.1f, 0), CmRotation(Vector3.forward, 20f), FlatGravity));
            Assert.That(e.Position.magnitude, Is.GreaterThan(0.1f));
            e.Reset();
            e.Add(Sample(200, Vector3.zero, CmRotation(Vector3.forward, 50f), FlatGravity));
            Assert.That(e.Position, Is.EqualTo(Vector3.zero));
            Assert.That(e.Velocity, Is.EqualTo(Vector3.zero));
            Assert.That(e.RotationSinceReset.magnitude, Is.LessThan(1e-3f));
        }

        [Test]
        public void ZeroVelocityUpdates_StopDriftWhenStill()
        {
            // Push for 0.5 s, then hold still for 1 s.
            MotionSample Push(int i) => Sample(i, i < 50 ? new Vector3(0, -0.1f, 0) : Vector3.zero, Flat, FlatGravity);
            var raw = Run(150, Push);
            var zupt = Run(150, Push, new PhonePoseEstimator { ZeroVelocityUpdates = true });
            Assert.That(raw.Velocity.magnitude, Is.GreaterThan(0.4f), "Without ZUPT the phone keeps coasting.");
            Assert.That(zupt.Velocity.magnitude, Is.EqualTo(0f));
            Assert.That(zupt.Position.z, Is.LessThan(raw.Position.z));
        }

        [Test]
        public void LongGaps_AreSkipped()
        {
            var e = new PhonePoseEstimator();
            e.Add(new MotionSample(1, "phone", 0, 10.0, 0, new Vector3(0, 1, 0), default, Flat, FlatGravity));
            e.Add(new MotionSample(1, "phone", 1, 12.0, 0, new Vector3(0, 1, 0), default, Flat, FlatGravity));
            Assert.That(e.SkippedGaps, Is.EqualTo(1));
            Assert.That(e.Position, Is.EqualTo(Vector3.zero));
        }
    }
}
