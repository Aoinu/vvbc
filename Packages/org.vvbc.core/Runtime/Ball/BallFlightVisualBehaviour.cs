using Tsukimi;
using UnityEngine;

// Visual sampling is active only between release and reset.
public partial class BallFlightVisualBehaviour : TsukimiBehaviour
{
    public Rigidbody body;
    public TrailRenderer speedTrail;
    public LineRenderer flightPath;
    public float minimumTrailSpeedKmh = 80f;
    public float minimumPathPointDistance = 0.05f;
    public int maximumPathPoints = 1024;

    private int _pathCount;
    private Vector3 _lastPathPoint;
    private bool _trailVisible;

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

    private void FixedUpdate()
    {
        if (body == null || speedTrail == null || flightPath == null || body.isKinematic) return;
        Vector3 position = body.position;
        bool showTrail = ShouldShowTrail(body.velocity.magnitude);
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
        return speedMetersPerSecond >= minimumTrailSpeedKmh / 3.6f;
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
