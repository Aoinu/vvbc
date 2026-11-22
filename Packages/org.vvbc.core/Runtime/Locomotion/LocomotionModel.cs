using UnityEngine;

namespace VVBC.Locomotion
{
    // Horizontal motion model for the Station locomotion validation. Pure functions of the
    // given values so the controller can swap how the result is applied to the Station.
    public static class LocomotionModel
    {
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
