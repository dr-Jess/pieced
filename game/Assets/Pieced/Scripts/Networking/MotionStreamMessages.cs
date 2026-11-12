using System;
using UnityEngine;

namespace Pieced.Networking
{
    /// <summary>
    /// Wire format: newline-delimited JSON over TCP, one object per line, every object has a "type".
    /// See tools/stream/README.md for the full protocol.
    /// </summary>
    [Serializable]
    internal sealed class WireMessage
    {
        public string type;

        // hello
        public string role;
        public string name;
        public int protocol;

        // motion
        public long seq;
        public double t;
        public float ax, ay, az;     // user acceleration (gravity removed), g
        public float gx, gy, gz;     // rotation rate, rad/s
        public float qx, qy, qz, qw; // attitude quaternion
        public float grx, gry, grz;  // gravity, g, device frame (optional; zero if the sender omits it)

        // pose (CV client): arm landmarks in order left shoulder, right shoulder, left elbow, right elbow,
        // left wrist, right wrist (MediaPipe pose landmarks 11-16)
        public string model;
        public float[] world;  // 18 floats: x, y, z per landmark; MediaPipe world metres (hips origin, x = your left, y down, z toward camera = negative)
        public float[] image;  // 12 floats: normalized image x, y per landmark
        public float[] vis;    // 6 floats: MediaPipe visibility 0-1
    }

    /// <summary>Arm landmarks for one camera frame from the CV client (MediaPipe's coordinates, not Unity's).</summary>
    public readonly struct PoseSample
    {
        public const int LeftShoulder = 0, RightShoulder = 1, LeftElbow = 2, RightElbow = 3, LeftWrist = 4, RightWrist = 5;

        public readonly int clientId;
        public readonly long sequence;
        /// <summary>Sender clock, seconds (the CV client uses the laptop's perf_counter at frame capture).</summary>
        public readonly double captureTime;
        public readonly double receivedTime;
        /// <summary>MediaPipe world landmarks, metres: hips origin, +x = your left, +y = down, -z = toward the camera.</summary>
        public readonly Vector3[] world;
        public readonly float[] visibility;

        public PoseSample(int clientId, long sequence, double captureTime, double receivedTime, Vector3[] world, float[] visibility)
        {
            this.clientId = clientId; this.sequence = sequence; this.captureTime = captureTime;
            this.receivedTime = receivedTime; this.world = world; this.visibility = visibility;
        }
    }

    /// <summary>One motion reading from a connected device (currently the phone's IMU).</summary>
    public readonly struct MotionSample
    {
        public readonly int clientId;
        public readonly string role;
        public readonly long sequence;
        /// <summary>Sender clock, seconds (the phone uses seconds since boot).</summary>
        public readonly double deviceTime;
        /// <summary>Time.realtimeSinceStartupAsDouble when the game processed it.</summary>
        public readonly double receivedTime;
        /// <summary>Acceleration with gravity removed, in g, in the sender's device frame.</summary>
        public readonly Vector3 userAcceleration;
        /// <summary>Rotation rate in rad/s, in the sender's device frame.</summary>
        public readonly Vector3 rotationRate;
        /// <summary>Sender's attitude, in the sender's reference frame (not converted to Unity's).</summary>
        public readonly Quaternion attitude;
        /// <summary>Gravity in g, in the sender's device frame; zero if the sender doesn't send it.</summary>
        public readonly Vector3 gravity;

        public MotionSample(int clientId, string role, long sequence, double deviceTime, double receivedTime,
            Vector3 userAcceleration, Vector3 rotationRate, Quaternion attitude, Vector3 gravity = default)
        {
            this.gravity = gravity;
            this.clientId = clientId; this.role = role; this.sequence = sequence;
            this.deviceTime = deviceTime; this.receivedTime = receivedTime;
            this.userAcceleration = userAcceleration; this.rotationRate = rotationRate; this.attitude = attitude;
        }
    }

    /// <summary>A connected device, as seen by the game. Values are updated on the main thread.</summary>
    public sealed class StreamClientInfo
    {
        public int Id { get; internal set; }
        public string Endpoint { get; internal set; }
        public string Role { get; internal set; } = "unknown";
        public string Name { get; internal set; } = "";
        public long SamplesReceived { get; internal set; }
        public long SamplesDropped { get; internal set; }
        public float SampleRate { get; internal set; }
        public long LastSequence { get; internal set; } = -1;
    }
}
