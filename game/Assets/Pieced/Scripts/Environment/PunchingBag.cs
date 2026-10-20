using UnityEngine;

namespace Pieced.Environment
{
    /// <summary>
    /// Heavy bag hanging from a world-space ball joint at this transform's origin. Gravity restores swing;
    /// a weak torsion spring (like a twisted chain) restores spin so the bag turns and then settles facing forward.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PunchingBag : MonoBehaviour
    {
        [SerializeField] private float mass = 45f;
        [SerializeField] private float swingLimit = 45f;
        [Tooltip("Restoring torque per radian of twist (N·m/rad).")]
        [SerializeField] private float twistSpring = 6f;
        [Tooltip("Torque per rad/s of spin (N·m·s/rad).")]
        [SerializeField] private float twistDamper = 1.5f;
        [Tooltip("Per-second damping of swing only; spin is damped by the twist damper so hooks still visibly turn the bag.")]
        [SerializeField] private float swingDamping = 0.8f;
        [SerializeField] private float friction = 1f;
        [SerializeField] private float bounciness = 0.1f;

        private Rigidbody body;
        private Vector3 initialPosition;
        private Quaternion initialRotation;

        public Rigidbody Body => body;
        /// <summary>Degrees the bag's long axis is tilted away from vertical.</summary>
        public float SwingAngle => Vector3.Angle(transform.up, initialRotation * Vector3.up);
        /// <summary>Signed degrees the bag has spun around its long axis relative to its starting facing.</summary>
        public float TwistAngle
        {
            get
            {
                var swingFree = Quaternion.FromToRotation(transform.up, initialRotation * Vector3.up) * transform.rotation;
                var forward = Quaternion.Inverse(initialRotation) * (swingFree * Vector3.forward);
                return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            }
        }
        /// <summary>Spin rate around the bag's long axis in rad/s.</summary>
        public float TwistRate => Vector3.Dot(body.angularVelocity, transform.up);

        private void Awake()
        {
            initialPosition = transform.position;
            initialRotation = transform.rotation;

            body = GetComponent<Rigidbody>();
            body.mass = mass;
            body.angularDamping = 0f;
            body.linearDamping = 0.15f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            body.maxAngularVelocity = 30f;
            body.solverIterations = 16;

            var material = new PhysicsMaterial("Bag") { dynamicFriction = friction, staticFriction = friction, bounciness = bounciness };
            foreach (var bagCollider in GetComponentsInChildren<Collider>()) bagCollider.sharedMaterial = material;

            var joint = gameObject.AddComponent<ConfigurableJoint>();
            joint.anchor = Vector3.zero;
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = transform.position;
            joint.axis = Vector3.up;
            joint.secondaryAxis = Vector3.forward;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.angularYLimit = new SoftJointLimit { limit = swingLimit };
            joint.angularZLimit = new SoftJointLimit { limit = swingLimit };
        }

        private void FixedUpdate()
        {
            var up = transform.up;
            var spin = Vector3.Project(body.angularVelocity, up);
            var swing = body.angularVelocity - spin;
            body.angularVelocity = swing * Mathf.Max(0f, 1f - swingDamping * Time.fixedDeltaTime) + spin;

            float twistTorque = -twistSpring * TwistAngle * Mathf.Deg2Rad - twistDamper * Vector3.Dot(spin, up);
            body.AddTorque(up * twistTorque, ForceMode.Force);
        }

        public void ResetBag()
        {
            body.position = initialPosition;
            body.rotation = initialRotation;
            transform.SetPositionAndRotation(initialPosition, initialRotation);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }
}
