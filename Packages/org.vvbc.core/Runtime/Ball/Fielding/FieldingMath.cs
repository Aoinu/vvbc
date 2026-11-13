using UnityEngine;

// Pure fielding math shared by the glove, the aim helper and the Ball catch check.
// No state, no allocation; every result is a value so callers stay testable.
public static class FieldingMath
{
    public const float Gravity = 9.81f;

    // A bone pose read from VRCPlayerApi is unusable when it is missing (zero),
    // non-finite, or its rotation is not close to unit length.
    public static bool ValidPose(Vector3 position, Quaternion rotation)
    {
        if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z)) return false;
        if (position.sqrMagnitude < 0.000001f) return false;
        return ValidRotation(rotation);
    }

    public static bool ValidRotation(Quaternion rotation)
    {
        float lengthSquared = rotation.x * rotation.x + rotation.y * rotation.y +
            rotation.z * rotation.z + rotation.w * rotation.w;
        return Finite(lengthSquared) && lengthSquared > 0.81f && lengthSquared < 1.21f;
    }

    public static bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }

    // Fitting limits: offset distance from the hand bone and uniform scale.
    public static bool FittingValid(Vector3 offset, float scale, float maxDistance, float minScale, float maxScale)
    {
        if (!Finite(offset.x) || !Finite(offset.y) || !Finite(offset.z) || !Finite(scale)) return false;
        return offset.magnitude <= maxDistance + 0.0001f && scale >= minScale - 0.0001f && scale <= maxScale + 0.0001f;
    }

    // Scale moves in whole steps and stays inside the limits.
    public static float StepScale(float scale, int direction, float step, float minScale, float maxScale)
    {
        if (step <= 0f) return Mathf.Clamp(scale, minScale, maxScale);
        float steps = Mathf.Round((scale - minScale) / step) + (direction > 0 ? 1f : direction < 0 ? -1f : 0f);
        return Mathf.Clamp(minScale + steps * step, minScale, maxScale);
    }

    // Broad phase of the swept catch test. relativeEnd is ball minus glove at the
    // current step; relativeMotion is how far that vector moved during the step.
    // The relative segment lies within |relativeMotion| of relativeEnd, so a
    // distance beyond radius + |relativeMotion| cannot reach the sphere.
    public static bool CatchBroadPhase(Vector3 relativeEnd, Vector3 relativeMotion, float radius)
    {
        float reach = radius + relativeMotion.magnitude;
        return relativeEnd.sqrMagnitude <= reach * reach;
    }

    // Narrow phase: fraction along the relative segment of the closest approach
    // when it is inside the sphere, otherwise -1.
    public static float CatchFraction(Vector3 relativeStart, Vector3 relativeMotion, float radius)
    {
        float lengthSquared = relativeMotion.sqrMagnitude;
        float t = lengthSquared > 0.0000001f
            ? Mathf.Clamp01(-Vector3.Dot(relativeStart, relativeMotion) / lengthSquared) : 0f;
        Vector3 closest = relativeStart + relativeMotion * t;
        return closest.sqrMagnitude <= radius * radius ? t : -1f;
    }

    // Game cue for the aim marker, not the launch angle: the no-drag elevation that
    // reaches the distance at the reference speed, scaled and clamped.
    public static float AimElevationDegrees(float horizontalDistance, float referenceSpeed, float gain, float maxDegrees)
    {
        if (!(referenceSpeed > 0.1f) || !(horizontalDistance > 0f)) return 0f;
        float s = Mathf.Clamp01(Gravity * horizontalDistance / (referenceSpeed * referenceSpeed));
        float ballistic = 0.5f * Mathf.Asin(s) * Mathf.Rad2Deg;
        return Mathf.Clamp(ballistic * Mathf.Max(0f, gain), 0f, Mathf.Max(0f, maxDegrees));
    }

    // Arm speed to throw speed (both m/s): minimum at or below slowArm, maximum at or
    // above fastArm, smoothstep in between.
    public static float ThrowSpeed(float armSpeed, float slowArm, float fastArm, float minSpeed, float maxSpeed)
    {
        if (!Finite(armSpeed)) return minSpeed;
        float t = fastArm > slowArm ? Mathf.Clamp01((armSpeed - slowArm) / (fastArm - slowArm)) : (armSpeed >= fastArm ? 1f : 0f);
        float smooth = t * t * (3f - 2f * t);
        return minSpeed + (maxSpeed - minSpeed) * smooth;
    }

    // Closed-form estimate of the low-trajectory launch elevation (degrees) that reaches a
    // point at horizontal distance d and relative height h, ignoring Magnus lift. Horizontal
    // flight time uses the closed form for quadratic drag on a flat path,
    // t = (e^{kd} - 1) / (k v); the vertical component then covers h against gravity. One
    // refinement uses v cos(theta). ThrowAim uses it only as the solver's first guess.
    public static float LaunchElevationDegrees(float speed, float distance, float height, float drag, float maxDegrees)
    {
        if (!(speed > 0.1f) || !(distance > 0.01f)) return 0f;
        float theta = 0f;
        for (int i = 0; i < 2; i++)
        {
            float horizontal = Mathf.Max(0.1f, speed * Mathf.Cos(theta));
            float time = drag > 0.000001f
                ? (Mathf.Exp(Mathf.Min(drag * distance, 10f)) - 1f) / (drag * horizontal)
                : distance / horizontal;
            float vertical = (height + 0.5f * Gravity * time * time) / time;
            theta = Mathf.Asin(Mathf.Clamp(vertical / speed, -1f, 1f));
        }
        float limit = Mathf.Max(0f, maxDegrees);
        return Mathf.Clamp(theta * Mathf.Rad2Deg, -limit, limit);
    }

    // Height above the release point at which a throw crosses the horizontal distance
    // `distance`, predicted with the Ball's own flight steps (BallFlightModel.Step with the
    // spin decayed after each step, as AdvanceDecayingSpin and the Rigidbody angularDrag do).
    // Backspin (FieldingMath.Backspin) has a horizontal axis perpendicular to the heading, so
    // drag and Magnus stay in the vertical plane of the heading and the flight reduces to two
    // dimensions: Magnus = magnus * spin * (-vy, vx) in (along, up). spinRate is in rad/s;
    // spinDecay is the factor applied to it after every step (1 = no decay). A throw that
    // stops moving forward, or falls below `floor` while descending, or runs out of steps,
    // never reaches the distance: it returns min(height, floor), so it always reads as short.
    public static float PredictThrowHeight(float speed, float elevationDegrees, float distance,
        float drag, float magnus, float spinRate, float spinDecay, float gravity, float step, float floor, int maxSteps)
    {
        float radians = elevationDegrees * Mathf.Deg2Rad;
        float vx = speed * Mathf.Cos(radians);
        float vy = speed * Mathf.Sin(radians);
        float x = 0f;
        float y = 0f;
        // Coefficients pre-multiplied by the step: v += (g + a) dt in fewer Udon operations.
        float dragStep = drag * step;
        float gravityStep = gravity * step;
        float lift = magnus * step * spinRate;
        for (int i = 0; i < maxSteps; i++)
        {
            float dragSpeed = dragStep * Mathf.Sqrt(vx * vx + vy * vy);
            float nextVx = vx - dragSpeed * vx - lift * vy;
            vy = vy - gravityStep - dragSpeed * vy + lift * vx;
            vx = nextVx;
            float previousX = x;
            float previousY = y;
            x += vx * step;
            y += vy * step;
            lift *= spinDecay;
            if (x >= distance)
            {
                float span = x - previousX;
                return span > 0.000001f ? previousY + (y - previousY) * ((distance - previousX) / span) : y;
            }
            if (vx <= 0f || (vy < 0f && y < floor)) return Mathf.Min(y, floor);
        }
        return Mathf.Min(y, floor);
    }

    // SolveLaunchElevation result w: how the elevation relates to the target.
    public const int ReachNoTarget = 0;
    public const int ReachExact = 1;
    // Even the largest elevation passes the target's distance below it, or never gets there
    // (it stops moving forward, or descends below the ground plane first).
    public const int ReachShortAtLimit = 2;
    // Even the lowest elevation passes the target's distance above it.
    public const int ReachHighAtLimit = 3;
    // Every evaluation was used without coming within SolverTolerance; the best one is returned.
    public const int ReachApproximate = 4;
    public const int MaxSolverEvaluations = 6;
    public const int MaxPredictionSteps = 512;
    public const float SolverTolerance = 0.005f;
    // Without a known ground plane, a predicted flight that falls this far below the target
    // while descending is short.
    private const float ShortFloor = 20f;

    // Launch elevation (degrees, within ±maxDegrees) whose predicted flight
    // (PredictThrowHeight) passes horizontal distance `distance` at relative height `height`.
    // The predicted height rises with the elevation over this range, so the search keeps a
    // bracket: the closed-form estimate, one step along the geometric slope d/cos²θ, then
    // secant steps (bisection when a secant leaves the bracket). A bound that still misses
    // on the same side means the target cannot be reached; that bound is returned and
    // reported. Bounded: MaxSolverEvaluations predictions of at most MaxPredictionSteps steps.
    // ground is the height of a horizontal ground plane relative to the release (negative), or
    // NaN when unknown. A flight that descends below it before the target distance counts as
    // short, so ReachExact means the predicted free flight passes the target's distance at its
    // height without first going below that plane. Terrain above the plane (a mound, a fence)
    // is not modelled. A target below the plane uses its own height as the floor instead.
    // Returns (elevation, predicted minus wanted height, reach, evaluations).
    public static Vector4 SolveLaunchElevation(float speed, float distance, float height, float drag, float magnus,
        float spinRpm, float spinHalfLife, float gravity, float step, float maxDegrees, float ground)
    {
        float limit = Finite(maxDegrees) ? Mathf.Max(0f, maxDegrees) : 0f;
        if (!(distance >= 0.05f) || !Finite(distance) || !(speed > 0.1f) || !Finite(speed) || !(step > 0f) || !Finite(height))
        {
            // Too close (or nothing to fly): straight at the point within the limits.
            float safeHeight = Finite(height) ? height : 0f;
            float safeDistance = Finite(distance) ? Mathf.Max(distance, 0f) : 0f;
            float direct = Mathf.Atan2(safeHeight, Mathf.Max(safeDistance, 0.0001f)) * Mathf.Rad2Deg;
            float clamped = Mathf.Clamp(direct, -limit, limit);
            int reach = direct > limit ? ReachShortAtLimit : direct < -limit ? ReachHighAtLimit : ReachExact;
            return new Vector4(clamped, Mathf.Tan(clamped * Mathf.Deg2Rad) * safeDistance - safeHeight, reach, 0f);
        }
        float spinRate = Mathf.Max(0f, spinRpm) * 2f * Mathf.PI / 60f;
        float decay = spinHalfLife > 0f ? Mathf.Pow(0.5f, step / spinHalfLife) : 1f;
        float floor = Finite(ground) ? Mathf.Min(ground, height) : height - ShortFloor;
        float lo = -limit;
        float hi = limit;
        bool loKnown = false;
        bool hiKnown = false;
        float bestAngle = 0f;
        float bestError = float.MaxValue;
        float previousAngle = 0f;
        float previousError = 0f;
        int evaluations = 0;
        float angle = Mathf.Clamp(LaunchElevationDegrees(speed, distance, height, drag, limit), -limit, limit);
        while (true)
        {
            float error = PredictThrowHeight(speed, angle, distance, drag, magnus, spinRate, decay,
                gravity, step, floor, MaxPredictionSteps) - height;
            evaluations++;
            if (Mathf.Abs(error) < Mathf.Abs(bestError))
            {
                bestAngle = angle;
                bestError = error;
            }
            if (Mathf.Abs(error) <= SolverTolerance) return new Vector4(angle, error, ReachExact, evaluations);
            if (error < 0f)
            {
                if (angle >= limit) return new Vector4(angle, error, ReachShortAtLimit, evaluations);
                lo = angle;
                loKnown = true;
            }
            else
            {
                if (angle <= -limit) return new Vector4(angle, error, ReachHighAtLimit, evaluations);
                hi = angle;
                hiKnown = true;
            }
            if (evaluations >= MaxSolverEvaluations) break;
            float next;
            if (evaluations == 1)
            {
                // Height gained per degree, from the geometry of a straight line.
                float cosine = Mathf.Max(0.2f, Mathf.Cos(angle * Mathf.Deg2Rad));
                next = angle - error / (distance * Mathf.Deg2Rad / (cosine * cosine));
            }
            else
            {
                float change = error - previousError;
                next = Mathf.Abs(change) > 0.000001f ? angle - error * (angle - previousAngle) / change : 0.5f * (lo + hi);
            }
            next = Mathf.Clamp(next, -limit, limit);
            bool inside = (loKnown ? next > lo : next >= lo) && (hiKnown ? next < hi : next <= hi);
            if (!inside || !Finite(next))
                next = loKnown && hiKnown ? 0.5f * (lo + hi) : (hiKnown ? -limit : limit);
            previousAngle = angle;
            previousError = error;
            angle = next;
        }
        return new Vector4(bestAngle, bestError, ReachApproximate, evaluations);
    }

    // Perpendicular distance from a point to a forward ray; behind the origin it is
    // the distance to the origin. direction must be normalized.
    public static float RayDistance(Vector3 origin, Vector3 direction, Vector3 point)
    {
        Vector3 offset = point - origin;
        float along = Vector3.Dot(offset, direction);
        if (along <= 0f) return offset.magnitude;
        return (offset - direction * along).magnitude;
    }

    // Distance along a ray (in units of direction) to where it leaves a sphere, or -1 when it
    // does not reach the sphere ahead of its origin. The exit is the crossing on the side the
    // ray travels toward: the only forward crossing from inside the sphere, and the far one
    // from outside. A tangent ray touches it once. Non-finite input gives -1.
    public static float RaySphereExit(Vector3 origin, Vector3 direction, Vector3 center, float radius)
    {
        if (!Finite(radius) || !(radius > 0f)) return -1f;
        float lengthSquared = direction.sqrMagnitude;
        if (!Finite(lengthSquared) || !(lengthSquared > 0.000001f)) return -1f;
        Vector3 offset = origin - center;
        float half = Vector3.Dot(offset, direction) / lengthSquared;
        float constant = (offset.sqrMagnitude - radius * radius) / lengthSquared;
        float discriminant = half * half - constant;
        if (!Finite(discriminant) || discriminant < 0f) return -1f;
        float distance = Mathf.Sqrt(discriminant) - half;
        return Finite(distance) && distance >= 0f ? distance : -1f;
    }

    // Control error cone (degrees) from how far the arm ray missed the marker.
    public static float ErrorDegrees(float miss, float perfectRadius, float wildRadius, float minError, float maxError)
    {
        float t = wildRadius > perfectRadius ? Mathf.Clamp01((miss - perfectRadius) / (wildRadius - perfectRadius)) : (miss > perfectRadius ? 1f : 0f);
        return Mathf.Lerp(minError, maxError, t);
    }

    // Unit direction from a horizontal heading, an elevation and a yaw offset (degrees).
    public static Vector3 LaunchDirection(Vector3 horizontal, float elevationDegrees, float yawDegrees)
    {
        Vector3 flat = new Vector3(horizontal.x, 0f, horizontal.z);
        if (flat.sqrMagnitude < 0.000001f) flat = Vector3.forward;
        flat = flat.normalized;
        // Yaw about world up, positive turning +Z toward +X (Unity's AngleAxis convention).
        float yaw = yawDegrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(yaw);
        float s = Mathf.Sin(yaw);
        flat = new Vector3(flat.x * c + flat.z * s, 0f, flat.z * c - flat.x * s);
        float radians = elevationDegrees * Mathf.Deg2Rad;
        return flat * Mathf.Cos(radians) + Vector3.up * Mathf.Sin(radians);
    }

    // Backspin for a ball moving along direction: the top surface moves backward.
    public static Vector3 Backspin(Vector3 direction, float rpm)
    {
        Vector3 axis = Vector3.Cross(direction, Vector3.up);
        if (axis.sqrMagnitude < 0.000001f) return Vector3.zero;
        return axis.normalized * (Mathf.Max(0f, rpm) * 2f * Mathf.PI / 60f);
    }
}
