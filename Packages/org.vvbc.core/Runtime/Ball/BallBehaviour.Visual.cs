using Tsukimi;
using UnityEngine;

// Visual sampling is active only between release and reset.
public partial class BallBehaviour
{
    [Header("Visual Effects")]
    [Tooltip("Speed at or below which the trail is hidden, in km/h. Default 40.")] public float minimumTrailSpeedKmh = 40f;
    [Tooltip("Speed at which the trail reaches its full length, in km/h. Between the two speeds the length scales linearly. Default 80.")] public float fullTrailSpeedKmh = 80f;
    [Tooltip("Minimum distance between world-space path points, in metres.")] public float minimumPathPointDistance = 0.05f;
    [Tooltip("Maximum number of points retained until reset.")] public int maximumPathPoints = 1024;

    private int _pathCount;
    private Vector3 _lastPathPoint;
    private bool _trailVisible;
    // Full trail duration read from the renderer; the speed fraction scales it.
    private float _trailFullTime;

    public void BeginFlight()
    {
        if (body == null || speedTrail == null || flightPath == null) return;
        _trailVisible = false;
        speedTrail.emitting = false;
        speedTrail.enabled = false;
        speedTrail.Clear();
        _pathCount = 1;
        _lastPathPoint = body.position;
        flightPath.positionCount = 1;
        flightPath.SetPosition(0, _lastPathPoint);
        flightPath.enabled = true;
    }

    public void EndFlight()
    {
        _trailVisible = false;
        if (speedTrail != null)
        {
            speedTrail.emitting = false;
            speedTrail.enabled = false;
            speedTrail.Clear();
        }
        if (flightPath != null)
        {
            flightPath.positionCount = 0;
            flightPath.enabled = false;
        }
        _pathCount = 0;
    }

    private void UpdateFlightVisual()
    {
        if (body == null || speedTrail == null || flightPath == null || body.isKinematic) return;
        Vector3 position = body.position;
        float speed = body.velocity.magnitude;
        bool showTrail = ShouldShowTrail(speed);
        if (showTrail)
        {
            if (_trailFullTime <= 0f) _trailFullTime = speedTrail.time;
            speedTrail.time = _trailFullTime * TrailLengthFraction(speed);
        }
        if (showTrail != _trailVisible)
        {
            _trailVisible = showTrail;
            speedTrail.emitting = showTrail;
            speedTrail.enabled = showTrail;
            if (!showTrail) speedTrail.Clear();
        }
        AppendPath(position);
    }

    private bool ShouldShowTrail(float speedMetersPerSecond)
    {
        return TrailLengthFraction(speedMetersPerSecond) > 0f;
    }

    // 0 at or below the minimum speed, 1 at or above the full-length speed.
    private float TrailLengthFraction(float speedMetersPerSecond)
    {
        float low = minimumTrailSpeedKmh / 3.6f;
        float high = Mathf.Max(fullTrailSpeedKmh / 3.6f, low + 0.0001f);
        return Mathf.Clamp01((speedMetersPerSecond - low) / (high - low));
    }

    private void AppendPath(Vector3 position)
    {
        if (_pathCount == 0 || _pathCount >= maximumPathPoints) return;
        float distance = Mathf.Max(0f, minimumPathPointDistance);
        if ((position - _lastPathPoint).sqrMagnitude < distance * distance) return;
        flightPath.positionCount = _pathCount + 1;
        flightPath.SetPosition(_pathCount, position);
        _lastPathPoint = position;
        _pathCount++;
    }
}
