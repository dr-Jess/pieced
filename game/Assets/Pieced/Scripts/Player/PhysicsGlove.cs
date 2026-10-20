using System;
using Pieced.Environment;
using Pieced.Input;
using UnityEngine;

namespace Pieced.Player
{
    public struct GloveHit
    {
        public PunchHand hand;
        public Vector3 point;
        public Vector3 normal;
        public float relativeSpeed;
        public float impulse;
        public double time;
    }

    /// <summary>
    /// Dynamic rigidbody that chases a tracked hand (the target) every physics step. The glove is blocked by the bag,
    /// so it can lag behind the real hand. The bag is driven by the real hand, not just the glove:
    /// <list type="bullet">
    /// <item>Impact: during a contact the bag receives the hand's momentum (handMass x peak speed into the bag),
    /// minus whatever the glove's own collision already delivered. A glove resting on the bag still lands a full
    /// punch when the hand snaps forward.</item>
    /// <item>Push: a capped spring on how far the tracked hand is past the glove, into the bag.</item>
    /// </list>
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PhysicsGlove : MonoBehaviour
    {
        private const float ContactGraceSeconds = 0.1f;

        [SerializeField] private PunchHand hand;
        [SerializeField] private Transform target;
        [Tooltip("Mass of the glove proxy itself. The momentum behind a punch comes from Hand Mass.")]
        [SerializeField] private float mass = 1.5f;
        [SerializeField] private float maxSpeed = 14f;
        [Tooltip("Acceleration the glove can apply while chasing the hand.")]
        [SerializeField] private float maxAcceleration = 600f;
        [Tooltip("Acceleration the glove may still push into the bag with while touching it; the hand model does the rest.")]
        [SerializeField] private float contactAcceleration = 5f;
        [SerializeField] private float maxAngularSpeed = 40f;
        [SerializeField] private float friction = 1f;

        [Header("Tracked hand -> bag")]
        [Tooltip("Effective mass behind the real hand (fist, arm, body rotation).")]
        [SerializeField] private float handMass = 2.5f;
        [Tooltip("Push per metre the tracked hand is past the glove, into the bag (N/m).")]
        [SerializeField] private float pushStiffness = 300f;
        [SerializeField] private float maxPushForce = 120f;

        [Header("Debug visuals")]
        [Tooltip("Bare fist drawn at the tracked hand, shown only in debug mode.")]
        [SerializeField] private GameObject trackedHandModel;
        [SerializeField] private LineRenderer stretchLine;
        [Tooltip("See-through material the glove switches to in debug mode so the tracked hand is visible.")]
        [SerializeField] private Material debugGloveMaterial;

        private MeshRenderer[] gloveRenderers;
        private Material[] gloveMaterials;

        private Rigidbody body;
        private TrailRenderer trail;
        private int contacts;
        private float lastContactTime = float.NegativeInfinity;
        private Collider bagCollider;
        private Rigidbody bagBody;
        private Vector3 lastTargetPosition;
        private float peakSpeedIntoBag;
        private bool episodeOpen;

        public event Action<GloveHit> BagHit;

        public PunchHand Hand => hand;
        public Transform Target => target;
        public Rigidbody Body => body;
        public Vector3 Velocity => body.linearVelocity;
        /// <summary>Velocity of the tracked hand, which may differ from the glove's when the bag blocks it.</summary>
        public Vector3 HandVelocity { get; private set; }
        /// <summary>How far the glove lags behind the tracked hand, e.g. because the bag is in the way.</summary>
        public float TrackingError => Vector3.Distance(body.position, target.position);
        /// <summary>Total impulse delivered to the bag during the current (or last) contact, N·s.</summary>
        public float ContactImpulse { get; private set; }
        /// <summary>Part of ContactImpulse added from the tracked hand's momentum (beyond the glove's own collision), N·s.</summary>
        public float HandImpulse { get; private set; }
        /// <summary>Impulse from the push spring during the current (or last) contact, N·s.</summary>
        public float PushImpulse { get; private set; }
        /// <summary>Current push from the hand being past the glove, N.</summary>
        public float PushForce { get; private set; }
        public GloveHit LastHit { get; private set; }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            body.mass = mass;
            body.useGravity = false;
            body.linearDamping = 0f;
            body.angularDamping = 0f;
            body.maxAngularVelocity = maxAngularSpeed;
            body.solverIterations = 16;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            trail = GetComponentInChildren<TrailRenderer>();
            if (target != null) lastTargetPosition = target.position;

            var material = new PhysicsMaterial("Glove") { dynamicFriction = friction, staticFriction = friction, bounciness = 0.05f };
            foreach (var gloveCollider in GetComponentsInChildren<Collider>()) gloveCollider.sharedMaterial = material;
        }

        private void FixedUpdate()
        {
            if (target == null) return;
            float dt = Time.fixedDeltaTime;
            HandVelocity = (target.position - lastTargetPosition) / dt;
            lastTargetPosition = target.position;

            if (contacts == 0 && episodeOpen && Time.time - lastContactTime > ContactGraceSeconds) episodeOpen = false;

            var desiredVelocity = Vector3.ClampMagnitude((target.position - body.position) / dt, maxSpeed);
            var velocityChange = Vector3.ClampMagnitude(desiredVelocity - body.linearVelocity, maxAcceleration * dt);
            PushForce = 0f;
            if (contacts > 0 && bagCollider != null)
            {
                var surface = bagCollider.ClosestPoint(body.position);
                var outward = body.position - surface;
                if (outward.sqrMagnitude < 1e-8f) outward = -HandVelocity;
                outward.Normalize();

                // Let the glove slide and retract freely, but cap how hard it pushes itself into the bag.
                float into = Vector3.Dot(velocityChange, -outward);
                float intoLimit = contactAcceleration * dt;
                if (into > intoLimit) velocityChange += outward * (into - intoLimit);

                DriveBagFromHand(surface, outward);
            }
            body.AddForce(velocityChange, ForceMode.VelocityChange);

            (target.rotation * Quaternion.Inverse(body.rotation)).ToAngleAxis(out float angle, out var axis);
            if (angle > 180f) angle -= 360f;
            body.angularVelocity = float.IsFinite(axis.x)
                ? Vector3.ClampMagnitude(axis * (angle * Mathf.Deg2Rad / dt), maxAngularSpeed)
                : Vector3.zero;
        }

        private void DriveBagFromHand(Vector3 surface, Vector3 outward)
        {
            var relative = HandVelocity - bagBody.GetPointVelocity(surface);
            float speedInto = Vector3.Dot(relative, -outward);
            peakSpeedIntoBag = Mathf.Max(peakSpeedIntoBag, speedInto);

            float owed = handMass * peakSpeedIntoBag - ContactImpulse;
            if (owed > 0f)
            {
                // Sideways hand motion drags the bag surface (spin), limited by friction like a real contact.
                var tangential = Vector3.ProjectOnPlane(relative, outward);
                float tangentialShare = Mathf.Min(friction, tangential.magnitude / Mathf.Max(speedInto, 0.01f));
                var impulse = (-outward + tangential.normalized * tangentialShare) * owed;
                bagBody.AddForceAtPosition(impulse, surface, ForceMode.Impulse);
                ContactImpulse += owed;
                HandImpulse += owed;
            }

            float depth = Mathf.Max(0f, Vector3.Dot(target.position - body.position, -outward));
            PushForce = Mathf.Min(pushStiffness * depth, maxPushForce);
            if (PushForce > 0f) bagBody.AddForceAtPosition(-outward * PushForce, surface, ForceMode.Force);
            PushImpulse += PushForce * Time.fixedDeltaTime;
        }

        private void LateUpdate() => UpdateStretchLine();

        public void UpdateStretchLine()
        {
            if (stretchLine == null || !stretchLine.enabled || target == null) return;
            stretchLine.SetPosition(0, transform.position);
            stretchLine.SetPosition(1, target.position);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsBag(collision)) return;
            contacts++;
            lastContactTime = Time.time;
            bagCollider = collision.collider;
            bagBody = collision.rigidbody;

            bool newPunch = !episodeOpen;
            if (newPunch)
            {
                episodeOpen = true;
                peakSpeedIntoBag = 0f;
                ContactImpulse = 0f;
                HandImpulse = 0f;
                PushImpulse = 0f;
            }
            ContactImpulse += collision.impulse.magnitude;
            if (!newPunch) return;

            var contact = collision.GetContact(0);
            LastHit = new GloveHit
            {
                hand = hand,
                point = contact.point,
                normal = contact.normal,
                relativeSpeed = collision.relativeVelocity.magnitude,
                impulse = collision.impulse.magnitude,
                time = Time.timeAsDouble,
            };
            BagHit?.Invoke(LastHit);
        }

        private void OnCollisionStay(Collision collision)
        {
            if (!IsBag(collision)) return;
            lastContactTime = Time.time;
            ContactImpulse += collision.impulse.magnitude;
        }

        private void OnCollisionExit(Collision collision)
        {
            if (IsBag(collision)) contacts = Mathf.Max(0, contacts - 1);
        }

        private static bool IsBag(Collision collision) =>
            collision.rigidbody != null && collision.rigidbody.GetComponent<PunchingBag>() != null;

        /// <summary>Debug mode: shows the bare tracked fist and the glove-to-hand line, and makes the glove see-through.</summary>
        public void SetDebugVisualsVisible(bool visible)
        {
            if (trackedHandModel != null) trackedHandModel.SetActive(visible);
            if (stretchLine != null) stretchLine.enabled = visible;
            UpdateStretchLine();

            if (debugGloveMaterial == null) return;
            if (gloveRenderers == null)
            {
                gloveRenderers = GetComponentsInChildren<MeshRenderer>();
                gloveMaterials = System.Array.ConvertAll(gloveRenderers, r => r.sharedMaterial);
            }
            for (int i = 0; i < gloveRenderers.Length; i++)
                gloveRenderers[i].sharedMaterial = visible ? debugGloveMaterial : gloveMaterials[i];
        }

        public void SnapToTarget()
        {
            body.position = target.position;
            body.rotation = target.rotation;
            transform.SetPositionAndRotation(target.position, target.rotation);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            lastTargetPosition = target.position;
            HandVelocity = Vector3.zero;
            contacts = 0;
            episodeOpen = false;
            if (trail != null) trail.Clear();
        }
    }
}
