using UnityEngine;

namespace VVBC.Locomotion
{
    // Horizontal motion model for the Station locomotion validation. Pure functions of the
    // given values so the controller can swap how the result is applied to the Station.
    public static class LocomotionModel
    {
        // Horizontal distance outside the asymmetric Station-local head envelope.
        public static Vector3 HeadExcess(Vector3 head, float left, float right, float back, float forward)
        {
            float x = Mathf.Clamp(head.x, -Mathf.Max(0f, left), Mathf.Max(0f, right));
            float z = Mathf.Clamp(head.z, -Mathf.Max(0f, back), Mathf.Max(0f, forward));
            return new Vector3(head.x - x, 0f, head.z - z);
        }

        public static Vector3 LimitDistance(Vector3 displacement, float budget)
        {
            float limit = Mathf.Max(0f, budget);
            float length = displacement.magnitude;
            return length > limit ? displacement * (limit / length) : displacement;
        }

        public static Vector3 BudgetedStick(Vector3 displacement, float budget, float correctionDistance)
        {
            return LimitDistance(displacement, Mathf.Max(0f, budget - correctionDistance));
        }

        public static float AutoTurn(float rootYaw, float headYaw, Vector3 velocity, float maxDegrees)
        {
            float target = Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg;
            float desired = Mathf.Clamp(Mathf.DeltaAngle(rootYaw, target), -Mathf.Max(0f, maxDegrees), Mathf.Max(0f, maxDegrees));
            return LimitAutoTurn(Mathf.DeltaAngle(rootYaw, headYaw), desired);
        }

        public static bool ShouldAutoTurn(bool inVR, float look, float deadzone, float speed, float minimumSpeed)
        {
            return (!inVR || Mathf.Abs(look) <= deadzone) && speed >= Mathf.Max(0.001f, minimumSpeed);
        }

        public static bool CorrectionFailed(float distance, float reliableDistance, float improvement)
        {
            return distance >= 1f && reliableDistance >= 0.5f && improvement < 0.05f;
        }

        // Do not create a new behind-the-Station head orientation. If the head is
        // already behind, allow only a turn that moves toward the nearest boundary.
        public static float LimitAutoTurn(float relativeHeadYaw, float turn)
        {
            float relative = Mathf.DeltaAngle(0f, relativeHeadYaw);
            if (relative > 90f) return Mathf.Clamp(turn, 0f, relative - 90f);
            if (relative < -90f) return Mathf.Clamp(turn, relative + 90f, 0f);
            return Mathf.Clamp(turn, relative - 90f, relative + 90f);
        }

        // The stick as a vector of length at most 1, so a diagonal does not exceed the
        // maximum speed. A non-finite axis counts as released.
        public static Vector2 ClampStick(float horizontal, float vertical)
        {
            if (!(horizontal >= -1e9f && horizontal <= 1e9f)) horizontal = 0f;
            if (!(vertical >= -1e9f && vertical <= 1e9f)) vertical = 0f;
            Vector2 stick = new Vector2(horizontal, vertical);
            float length = stick.magnitude;
            return length > 1f ? stick / length : stick;
        }

        // Stick to a horizontal world direction, relative to the head yaw only: the head's
        // pitch and roll never tilt or shorten the motion.
        public static Vector3 WorldInput(Vector2 stick, float headYawDegrees)
        {
            return Quaternion.Euler(0f, headYawDegrees, 0f) * new Vector3(stick.x, 0f, stick.y);
        }

        // Where a root at rootPosition ends up when it turns by degrees (yaw) about pivot,
        // so that whatever is carried with the root (the playspace) keeps the pivot in place.
        // Height is kept.
        public static Vector3 TurnAbout(Vector3 rootPosition, Vector3 pivot, float degrees)
        {
            Vector3 offset = rootPosition - pivot;
            offset.y = 0f;
            Vector3 turned = pivot + Quaternion.Euler(0f, degrees, 0f) * offset;
            turned.y = rootPosition.y;
            return turned;
        }

        // One step of the horizontal velocity toward target (m/s), over dt seconds.
        //   no input                    -> slow down at deceleration (inertia, then stop)
        //   input against the velocity  -> move toward the target at reverseAcceleration,
        //                                  which first removes the old velocity, then
        //                                  builds the opposite one
        //   input slower than the speed -> slow down at deceleration
        //   otherwise                   -> speed up / turn at acceleration
        public static Vector3 Step(Vector3 velocity, Vector3 target, float dt,
            float maximumSpeed, float acceleration, float deceleration, float reverseAcceleration)
        {
            velocity.y = 0f;
            if (!(dt > 0f && dt < 1f)) return velocity;
            target.y = 0f;
            float speedLimit = maximumSpeed > 0f ? maximumSpeed : 0f;
            if (target.sqrMagnitude > speedLimit * speedLimit) target = target.normalized * speedLimit;

            float rate;
            if (target.sqrMagnitude < 1e-8f) rate = deceleration;
            else if (Vector3.Dot(velocity, target) < 0f) rate = reverseAcceleration;
            else if (target.sqrMagnitude < velocity.sqrMagnitude) rate = deceleration;
            else rate = acceleration;
            if (!(rate > 0f)) rate = 0f;

            Vector3 next = Vector3.MoveTowards(velocity, target, rate * dt);
            return next.sqrMagnitude > speedLimit * speedLimit ? next.normalized * speedLimit : next;
        }
    }
}
