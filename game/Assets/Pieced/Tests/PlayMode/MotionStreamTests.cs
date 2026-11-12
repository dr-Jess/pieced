using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using Pieced.Input;
using Pieced.Networking;
using Pieced.Player;
using Pieced.Tracking;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Pieced.Tests
{
    /// <summary>Talks to the motion stream server over a real localhost socket, the same way the phone app does.</summary>
    public sealed class MotionStreamTests
    {
        private const float Timeout = 3f;

        private static string Motion(long seq, float ax, float ay = 0f, float az = 0f) => System.FormattableString.Invariant(
            $"{{\"type\":\"motion\",\"seq\":{seq},\"t\":{100 + seq * 0.01},\"ax\":{ax},\"ay\":{ay},\"az\":{az},\"gx\":0.5,\"gy\":0,\"gz\":0,\"qx\":0,\"qy\":0,\"qz\":0,\"qw\":1}}\n");

        private const string Hello = "{\"type\":\"hello\",\"role\":\"phone\",\"name\":\"Test Phone\",\"protocol\":1}\n";

        private static void Send(TcpClient client, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            client.GetStream().Write(bytes, 0, bytes.Length);
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, string failure)
        {
            float deadline = Time.realtimeSinceStartup + Timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail(failure);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Server_ParsesHelloAndMotion_AndCountsDrops()
        {
            var go = new GameObject("Test Stream Server");
            go.SetActive(false);
            var server = go.AddComponent<MotionStreamServer>();
            server.Port = 0;
            go.SetActive(true);
            Assert.That(server.IsRunning, server.LastError);

            var samples = new List<MotionSample>();
            server.SampleReceived += samples.Add;
            using var client = new TcpClient { NoDelay = true };
            client.Connect("127.0.0.1", server.BoundPort);
            // Split one message across writes to check line reassembly.
            Send(client, Hello + Motion(1, 0.1f) + Motion(2, 0.2f).Substring(0, 20));
            Send(client, Motion(2, 0.2f).Substring(20) + Motion(4, 0.4f, 0.5f, -0.6f));

            yield return WaitUntil(() => samples.Count == 3, $"Expected 3 samples, got {samples.Count}.");
            var info = server.Clients.Single();
            Assert.That(info.Role, Is.EqualTo("phone"));
            Assert.That(info.Name, Is.EqualTo("Test Phone"));
            Assert.That(info.SamplesDropped, Is.EqualTo(1), "seq 3 was skipped");
            Assert.That(samples[2].sequence, Is.EqualTo(4));
            Assert.That(samples[2].userAcceleration, Is.EqualTo(new Vector3(0.4f, 0.5f, -0.6f)));
            Assert.That(samples[2].rotationRate.x, Is.EqualTo(0.5f).Within(1e-6));

            client.Close();
            yield return WaitUntil(() => server.Clients.Count == 0, "Server did not notice the disconnect.");
            Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator BrowserRequest_GetsReachabilityPage_AndIsNotAClient()
        {
            var go = new GameObject("Test Stream Server");
            go.SetActive(false);
            var server = go.AddComponent<MotionStreamServer>();
            server.Port = 0;
            go.SetActive(true);

            using var client = new TcpClient();
            client.Connect("127.0.0.1", server.BoundPort);
            Send(client, "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
            var reader = new System.IO.StreamReader(client.GetStream());
            var readTask = reader.ReadToEndAsync();
            yield return WaitUntil(() => readTask.IsCompleted, "Server never answered the browser.");
            StringAssert.StartsWith("HTTP/1.1 200 OK", readTask.Result);
            StringAssert.Contains("reachable", readTask.Result);
            yield return WaitUntil(() => server.Clients.Count == 0, "Browser should not stay listed as a client.");
            Object.Destroy(go);
        }

        /// <summary>A pose frame in MediaPipe world coordinates with the right wrist moved by (dx, dy, dz).</summary>
        private static string Pose(long seq, float dx, float dy, float dz)
        {
            // shoulders, elbows, wrists (left, right); MediaPipe: +x = your left, +y = down, -z = toward camera
            float[] w = { 0.18f, -0.5f, 0f, -0.18f, -0.5f, 0f, 0.25f, -0.3f, -0.1f, -0.25f, -0.3f, -0.1f,
                          0.1f, -0.45f, -0.25f, -0.1f + dx, -0.45f + dy, -0.25f + dz };
            var joined = string.Join(",", System.Array.ConvertAll(w, v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return System.FormattableString.Invariant(
                $"{{\"type\":\"pose\",\"seq\":{seq},\"t\":{seq * 0.033},\"model\":\"test\",\"world\":[{joined}],\"vis\":[1,1,1,1,1,1]}}\n");
        }

        [UnityTest]
        public IEnumerator PoseFromCvClient_MovesTrackedHand_AndPausesScriptedPunches()
        {
            yield return SceneManager.LoadSceneAsync("PunchingBagPrototype", LoadSceneMode.Single);
            yield return null;
            var server = Object.FindAnyObjectByType<MotionStreamServer>();
            var driver = Object.FindAnyObjectByType<PoseHandDriver>();
            Assert.That(driver != null, "Scene has no PoseHandDriver; rebuild it with Pieced > Build Prototype Scene.");
            var right = Object.FindObjectsByType<PlayerPunchController>().Single(c => c.Hand == PunchHand.Right);
            var target = GameObject.Find("Right Tracked Hand").transform;
            var guard = GameObject.Find("Right Guard Pose").transform;

            using var client = new TcpClient { NoDelay = true };
            client.Connect("127.0.0.1", server.BoundPort);
            Send(client, "{\"type\":\"hello\",\"role\":\"cv\",\"name\":\"test\",\"protocol\":1}\n" + Pose(1, 0, 0, 0));
            yield return WaitUntil(() => driver.IsDriving, "Driver never took over the hands.");
            Assert.That(right.enabled, Is.False, "Scripted punches should pause while the camera drives the hands.");

            // Right wrist 20 cm toward the camera (MediaPipe -z) and 10 cm to the player's right (MediaPipe -x).
            Send(client, Pose(2, -0.10f, 0f, -0.20f));
            yield return WaitUntil(() => target.localPosition.z - guard.localPosition.z > 0.25f, "Hand didn't move forward.");
            yield return new WaitForSeconds(0.1f);
            var moved = target.localPosition - guard.localPosition;
            Assert.That(moved.z, Is.EqualTo(0.50f).Within(0.02f), "20 cm forward x depth gain 2.5");
            Assert.That(moved.x, Is.EqualTo(0.10f).Within(0.02f), "10 cm to the player's right");

            client.Close();
            yield return WaitUntil(() => !driver.IsDriving, "Driver should hand control back when poses stop.");
            Assert.That(right.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator PhoneAccelerationSpike_ThrowsRightPunch_InPrototypeScene()
        {
            yield return SceneManager.LoadSceneAsync("PunchingBagPrototype", LoadSceneMode.Single);
            yield return null;
            var server = Object.FindAnyObjectByType<MotionStreamServer>();
            Assert.That(server != null && server.IsRunning, "Scene has no running MotionStreamServer.");
            var right = Object.FindObjectsByType<PlayerPunchController>().Single(c => c.Hand == PunchHand.Right);

            using var client = new TcpClient { NoDelay = true };
            client.Connect("127.0.0.1", server.BoundPort);
            Send(client, Hello + Motion(1, 0.05f));
            yield return WaitUntil(() => server.Clients.Any(c => c.SamplesReceived == 1), "Server never got the idle sample.");
            Assert.That(right.IsPunching, Is.False, "Idle motion should not punch.");

            Send(client, Motion(2, 2.6f, 0.4f, 0.2f));
            yield return WaitUntil(() => right.IsPunching, "A phone acceleration spike did not throw a punch.");
        }
    }
}
