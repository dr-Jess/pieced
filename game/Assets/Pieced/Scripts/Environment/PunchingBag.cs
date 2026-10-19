using UnityEngine;

namespace Pieced.Environment
{
    public sealed class PunchingBag : MonoBehaviour
    {
        [SerializeField] private float impulse = 48f;
        [SerializeField] private float springStrength = 24f;
        [SerializeField] private float damping = 7f;
        [SerializeField] private float maxSwingAngle = 32f;
        [SerializeField] private float sleepAngle = 0.025f;
        [SerializeField] private float sleepVelocity = 0.025f;

        private Quaternion initialLocalRotation;
        private float angle;
        private float angularVelocity;

        private void Awake() => initialLocalRotation = transform.localRotation;

        public void Strike(float intensity)
        {
            angularVelocity -= impulse * Mathf.Clamp(intensity, 0.1f, 2f);
        }

        private void Update()
        {
            if (Mathf.Abs(angle) < sleepAngle && Mathf.Abs(angularVelocity) < sleepVelocity)
            {
                angle = 0f;
                angularVelocity = 0f;
                transform.localRotation = initialLocalRotation;
                return;
            }

            var acceleration = -springStrength * angle - damping * angularVelocity;
            angularVelocity += acceleration * Time.deltaTime;
            angle = Mathf.Clamp(angle + angularVelocity * Time.deltaTime, -maxSwingAngle, maxSwingAngle);
            if (Mathf.Abs(angle) >= maxSwingAngle) angularVelocity *= -0.25f;
            transform.localRotation = initialLocalRotation * Quaternion.Euler(angle, 0f, 0f);
        }

        public void ResetBag()
        {
            angle = 0f;
            angularVelocity = 0f;
            transform.localRotation = initialLocalRotation;
        }
    }
}
