using System.Collections;
using System.Linq;
using NUnit.Framework;
using Pieced.Environment;
using Pieced.Input;
using Pieced.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Pieced.Tests
{
    /// <summary>
    /// Plays scripted punches in the real prototype scene and measures what the physics does with them.
    /// Results are logged so tuning changes can be compared run to run.
    /// </summary>
    public sealed class GloveBagCollisionTests
    {
        private const float MaxAllowedPenetration = 0.04f;

        private struct PunchResult
        {
            public bool hit;
            public GloveHit firstHit;
            public float maxPenetration;
            public float peakSwing;
            public float peakTwistRate;
            public float signedTwistAtPeak;
        }

        private PunchingBag bag;
        private Collider bagCollider;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            Time.timeScale = 1f;
            yield return SceneManager.LoadSceneAsync("PunchingBagPrototype", LoadSceneMode.Single);
            yield return new WaitForFixedUpdate();
            bag = Object.FindAnyObjectByType<PunchingBag>();
            bagCollider = bag.GetComponentInChildren<CapsuleCollider>();
        }

        [UnityTearDown]
        public IEnumerator RestoreTime()
        {
            Time.timeScale = 1f;
            yield return null;
        }

        [UnityTest]
        public IEnumerator Straight_IsStoppedByBag_AndSwingsIt()
        {
            PunchResult result = default;
            yield return Throw(PunchHand.Right, PunchType.Straight, 1.2f, 1.5f, r => result = r);

            Assert.That(result.hit, "Glove never touched the bag.");
            Assert.That(result.maxPenetration, Is.LessThan(MaxAllowedPenetration), "Glove sank into the bag.");
            Assert.That(result.peakSwing, Is.GreaterThan(3f), "Bag barely swung.");
        }

        [UnityTest]
        public IEnumerator Hooks_SpinBag_InOppositeDirections()
        {
            PunchResult right = default, left = default;
            yield return Throw(PunchHand.Right, PunchType.Hook, 1.4f, 1.2f, r => right = r);
            Object.FindAnyObjectByType<PrototypeGameManager>().ResetPrototype();
            yield return new WaitForFixedUpdate();
            yield return Throw(PunchHand.Left, PunchType.Hook, 1.4f, 1.2f, r => left = r);

            Assert.That(right.hit && left.hit, "A hook missed the bag.");
            Assert.That(right.maxPenetration, Is.LessThan(MaxAllowedPenetration));
            Assert.That(left.maxPenetration, Is.LessThan(MaxAllowedPenetration));
            Assert.That(right.peakTwistRate, Is.GreaterThan(1f), "Right hook did not spin the bag.");
            Assert.That(left.peakTwistRate, Is.GreaterThan(1f), "Left hook did not spin the bag.");
            Assert.That(Mathf.Sign(right.signedTwistAtPeak), Is.Not.EqualTo(Mathf.Sign(left.signedTwistAtPeak)),
                "Mirrored hooks should spin the bag in opposite directions.");
        }

        [UnityTest]
        public IEnumerator Uppercut_IsStoppedByBag()
        {
            PunchResult result = default;
            yield return Throw(PunchHand.Right, PunchType.Uppercut, 1.4f, 1.5f, r => result = r);

            Assert.That(result.hit, "Uppercut never touched the bag.");
            Assert.That(result.maxPenetration, Is.LessThan(MaxAllowedPenetration));
        }

        [UnityTest]
        public IEnumerator Bag_SettlesBackToFacingForward()
        {
            yield return Throw(PunchHand.Right, PunchType.Hook, 1.5f, 0.5f, _ => { });
            Time.timeScale = 4f;
            yield return new WaitForSeconds(12f); // scaled time: 12 simulated seconds
            Debug.Log($"[Settle] swing {bag.SwingAngle:0.00}°, twist {bag.TwistAngle:0.00}°, spin {bag.TwistRate:0.000} rad/s");
            Assert.That(bag.SwingAngle, Is.LessThan(2f));
            Assert.That(Mathf.Abs(bag.TwistAngle), Is.LessThan(5f));
        }

        [UnityTest]
        public IEnumerator PunchFromRestingContact_UsesTrackedHandSpeed()
        {
            float fastImpulse = 0f, fastSwing = 0f, slowImpulse = 0f, slowSwing = 0f;
            yield return PushFromContact(0.04f, (impulse, swing) => { fastImpulse = impulse; fastSwing = swing; });
            Object.FindAnyObjectByType<PrototypeGameManager>().ResetPrototype();
            yield return PushFromContact(1.0f, (impulse, swing) => { slowImpulse = impulse; slowSwing = swing; });

            Debug.Log($"[Resting contact] fast snap: {fastImpulse:0.0} N·s, swing {fastSwing:0.0}° | slow push: {slowImpulse:0.0} N·s, swing {slowSwing:0.0}°");
            Assert.That(fastImpulse, Is.GreaterThan(12f), "A fast punch from contact should deliver the hand's momentum.");
            Assert.That(fastSwing, Is.GreaterThan(3f));
            Assert.That(fastSwing, Is.GreaterThan(slowSwing * 1.5f), "A snap should hit harder than a slow push over the same distance.");
        }

        /// <summary>Rests the right glove on the bag, then drives the tracked hand 30 cm into it over the given time.</summary>
        private IEnumerator PushFromContact(float pushDuration, System.Action<float, float> report)
        {
            var controller = Object.FindObjectsByType<PlayerPunchController>(FindObjectsSortMode.None).Single(c => c.Hand == PunchHand.Right);
            var glove = Object.FindObjectsByType<PhysicsGlove>(FindObjectsSortMode.None).Single(g => g.Hand == PunchHand.Right);
            controller.enabled = false;

            var axis = bag.transform.position;
            var start = new Vector3(axis.x + 0.05f, 1.6f, axis.z - 0.31f - 0.14f + 0.02f);
            for (float t = 0f; t < 1.5f; t += Time.fixedDeltaTime)
            {
                glove.Target.position = start;
                yield return new WaitForFixedUpdate();
            }
            Assert.That(glove.ContactImpulse, Is.GreaterThanOrEqualTo(0f));
            float before = glove.ContactImpulse, handBefore = glove.HandImpulse, pushBefore = glove.PushImpulse;

            float peakSwing = 0f;
            for (float t = 0f; t < pushDuration + 1.2f; t += Time.fixedDeltaTime)
            {
                float progress = Mathf.Clamp01(t / pushDuration);
                glove.Target.position = start + Vector3.forward * (0.3f * progress);
                yield return new WaitForFixedUpdate();
                peakSwing = Mathf.Max(peakSwing, bag.SwingAngle);
            }
            controller.enabled = true;
            float hand = glove.HandImpulse - handBefore, push = glove.PushImpulse - pushBefore, total = glove.ContactImpulse - before;
            Debug.Log($"[Contact push over {pushDuration:0.00}s] hand momentum {hand:0.0} N·s, push spring {push:0.0} N·s, " +
                      $"glove collisions {total - hand:0.0} N·s, peak swing {peakSwing:0.0}°");
            report(total, peakSwing);
        }

        private IEnumerator Throw(PunchHand hand, PunchType type, float intensity, float duration, System.Action<PunchResult> report)
        {
            var controller = Object.FindObjectsByType<PlayerPunchController>(FindObjectsSortMode.None).Single(c => c.Hand == hand);
            var glove = Object.FindObjectsByType<PhysicsGlove>(FindObjectsSortMode.None).Single(g => g.Hand == hand);
            var gloveCollider = glove.GetComponent<Collider>();

            var result = new PunchResult();
            void OnHit(GloveHit hit) { if (!result.hit) result.firstHit = hit; result.hit = true; }
            glove.BagHit += OnHit;

            controller.Play(new PunchEvent(hand, type, intensity, Time.timeAsDouble));
            for (float t = 0f; t < duration; t += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                if (Physics.ComputePenetration(gloveCollider, glove.Body.position, glove.Body.rotation,
                        bagCollider, bagCollider.transform.position, bagCollider.transform.rotation, out _, out float depth))
                    result.maxPenetration = Mathf.Max(result.maxPenetration, depth);
                result.peakSwing = Mathf.Max(result.peakSwing, bag.SwingAngle);
                if (Mathf.Abs(bag.TwistRate) > result.peakTwistRate)
                {
                    result.peakTwistRate = Mathf.Abs(bag.TwistRate);
                    result.signedTwistAtPeak = bag.TwistRate;
                }
            }
            glove.BagHit -= OnHit;

            Debug.Log($"[{hand} {type} x{intensity}] hit={result.hit} contactSpeed={result.firstHit.relativeSpeed:0.00} m/s " +
                      $"impulse={result.firstHit.impulse:0.00} N·s maxPenetration={result.maxPenetration * 100f:0.0} cm " +
                      $"peakSwing={result.peakSwing:0.0}° peakSpin={result.signedTwistAtPeak:0.00} rad/s");
            report(result);
        }
    }
}
