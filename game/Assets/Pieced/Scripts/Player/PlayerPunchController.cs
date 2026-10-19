using System.Collections;
using Pieced.Environment;
using Pieced.Input;
using UnityEngine;

namespace Pieced.Player
{
    public sealed class PlayerPunchController : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour punchEventSource;
        [SerializeField] private Transform glove;
        [SerializeField] private Transform restPose;
        [SerializeField] private Transform impactPose;
        [SerializeField] private PunchingBag bag;
        [SerializeField] private float extensionDuration = 0.12f;
        [SerializeField] private float impactHold = 0.03f;
        [SerializeField] private float retractionDuration = 0.18f;
        [SerializeField] private float cooldown = 0.10f;
        [SerializeField] private AnimationCurve extensionEase = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [SerializeField] private AnimationCurve retractionEase = AnimationCurve.EaseInOut(0, 0, 1, 1);

        private IPunchEventSource source;
        private Coroutine punchRoutine;
        private bool busy;

        private void OnEnable()
        {
            source = punchEventSource as IPunchEventSource;
            if (source == null) Debug.LogError("Punch event source must implement IPunchEventSource.", this);
            else source.PunchPerformed += HandlePunch;
            SnapTo(restPose);
        }

        private void OnDisable()
        {
            if (source != null) source.PunchPerformed -= HandlePunch;
        }

        private void HandlePunch(PunchEvent punch)
        {
            if (busy || punch.hand != PunchHand.Right) return;
            punchRoutine = StartCoroutine(AnimatePunch(Mathf.Clamp(punch.intensity, 0.1f, 2f)));
        }

        private IEnumerator AnimatePunch(float intensity)
        {
            busy = true;
            yield return Move(restPose, impactPose, extensionDuration, extensionEase);
            bag.Strike(intensity);
            if (impactHold > 0f) yield return new WaitForSeconds(impactHold);
            yield return Move(impactPose, restPose, retractionDuration, retractionEase);
            if (cooldown > 0f) yield return new WaitForSeconds(cooldown);
            busy = false;
            punchRoutine = null;
        }

        private IEnumerator Move(Transform from, Transform to, float duration, AnimationCurve curve)
        {
            for (float elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime)
            {
                float t = curve.Evaluate(Mathf.Clamp01(elapsed / duration));
                glove.localPosition = Vector3.LerpUnclamped(from.localPosition, to.localPosition, t);
                glove.localRotation = Quaternion.SlerpUnclamped(from.localRotation, to.localRotation, t);
                yield return null;
            }
            SnapTo(to);
        }

        private void SnapTo(Transform pose)
        {
            if (glove == null || pose == null) return;
            glove.localPosition = pose.localPosition;
            glove.localRotation = pose.localRotation;
        }

        public void ResetPunch()
        {
            if (punchRoutine != null) StopCoroutine(punchRoutine);
            punchRoutine = null;
            busy = false;
            SnapTo(restPose);
        }
    }
}
