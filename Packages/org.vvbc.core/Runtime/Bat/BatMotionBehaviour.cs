using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BatMotionBehaviour : TsukimiBehaviour
{
    private const float PoseEpsilon = 0.0001f;
    public const int Inactive = 0;
    public const int LocalFree = 1;
    public const int LocalHeld = 2;
    public const int RemoteHeld = 3;
    private const int RemoteRelease = 4;

    [SerializeField] private Transform lowerGrip;
    [SerializeField] private Transform upperGrip;
    [SerializeField] private Transform visualRoot;
    [SerializeField] private Rigidbody body;
    [SerializeField] private VRC_Pickup gripA;
    [SerializeField] private VRC_Pickup gripB;
    [Header("Grip Pose")]
    [Range(0.01f, 0.2f)]
    [SerializeField] private float minimumTwoHandSeparation = 0.04f;

    [Header("Remote Visual Correction")]
    [SerializeField] private float maxHandLinearSpeed = 0.05f;
    [SerializeField] private float maxHandAngularSpeed = 15f;
    [SerializeField] private float maxBodyLinearSpeed = 0.05f;
    [SerializeField] private float maxBodyAngularSpeed = 15f;
    [SerializeField] private float calibrationStableDuration = 0.4f;
    [Range(1, 64)] [SerializeField] private int velocitySmoothingSamples = 5;
    [SerializeField] private float maxCalibrationPositionDrift = 0.02f;
    [SerializeField] private float maxCalibrationRotationDrift = 5f;
    [SerializeField] private float visualTransitionDuration = 0.15f;
    [SerializeField] private float transitionCancelLinearSpeed = 0.15f;
    [SerializeField] private float transitionCancelAngularSpeed = 45f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugGripTransitions;


    private int _mode;
    private int gripState;
    private int lowerHand;
    private int upperHand;
    private int stateRevision;
    private int _lowerProxy = BatGripConstants.ProxyA;
    private int _upperProxy = BatGripConstants.ProxyB;
    private int _leftProxy;
    private int _rightProxy;
    private VRCPlayerApi _owner;
    private BatBehaviour _bat;
    private Transform _gripATransform;
    private Transform _gripBTransform;
    private Vector3 _lockPositionOffset;
    private Quaternion _lockRotationOffset = Quaternion.identity;
    private bool _hasLockOffset;
    private float _nextLockedPoseLogTime;
    private Vector3 _dropVelocity;
    private Vector3 _dropAngularVelocity;
    private Vector3 _previousDrivenPosition;
    private Quaternion _previousDrivenRotation = Quaternion.identity;
    private bool _hasPreviousDrivenPose;

    private readonly float[] _handLinearSamples = new float[64];
    private readonly float[] _handAngularSamples = new float[64];
    private readonly float[] _bodyLinearSamples = new float[64];
    private readonly float[] _bodyAngularSamples = new float[64];
    private int _sampleCount;
    private int _sampleNext;
    private int _sampleCapacity;
    private bool _hasSample;
    private Vector3 _lastUpperPosition;
    private bool _hasUpperSample;
    private Vector3 _lastHandPosition;
    private Quaternion _lastHandRotation = Quaternion.identity;
    private Vector3 _lastBodyPosition;
    private Quaternion _lastBodyRotation = Quaternion.identity;
    private Vector3 _stableHandPosition;
    private Quaternion _stableHandRotation = Quaternion.identity;
    private Vector3 _stableBodyPosition;
    private Quaternion _stableBodyRotation = Quaternion.identity;
    private float _stableTime;
    private bool _calibrated;
    private Vector3 _remotePositionOffset;
    private Quaternion _remoteRotationOffset = Quaternion.identity;
    private int _remoteRevision = -1;
    private int _remoteOwnerId = -1;
    private bool _visualCorrected;
    private bool _transitioning;
    private float _transitionTime;
    private Vector3 _transitionPosition;
    private Quaternion _transitionRotation = Quaternion.identity;


    // Called only on state/authority changes, after the object is enabled.
    public void Configure(int mode, int state, int lower, int upper, int lowerProxy,
        int upperProxy, int leftProxy, int rightProxy, int revision, VRCPlayerApi owner,
        bool resetVelocity, bool solveNow, bool recaptureLock, BatBehaviour controller)
    {
        bool resetVisual = _mode != mode || _remoteRevision != revision ||
            (_owner != null && owner != null && _owner.playerId != owner.playerId);
        if (gripState == BatGripConstants.GripLocked && state != BatGripConstants.GripLocked)
            _hasLockOffset = false;
        if (_mode == RemoteRelease && mode == RemoteHeld)
        {
            _transitioning = false;
            _visualCorrected = false;
        }
        _mode = mode;
        gripState = state;
        lowerHand = lower;
        upperHand = upper;
        _lowerProxy = lowerProxy;
        _upperProxy = upperProxy;
        _leftProxy = leftProxy;
        _rightProxy = rightProxy;
        stateRevision = revision;
        _owner = owner;
        _bat = controller;
        if (_gripATransform == null && gripA != null) _gripATransform = gripA.transform;
        if (_gripBTransform == null && gripB != null) _gripBTransform = gripB.transform;
        if (resetVisual) ResetVisualHistory();
        if (mode != RemoteHeld) ResetVisualRoot();
        if (recaptureLock && mode == LocalHeld && state == BatGripConstants.GripLocked)
            CaptureLockOffset();
        if (body != null && resetVelocity) ResetVelocityTracking();
        if (solveNow && mode == LocalHeld && SolveOwnerPose() && resetVelocity)
            ResetVelocityTracking();
    }

    public void StopMotion()
    {
        _mode = Inactive;
        _hasPreviousDrivenPose = false;
        _dropVelocity = Vector3.zero;
        _dropAngularVelocity = Vector3.zero;
        _hasLockOffset = false;
        ResetVisualHistory();
        ResetVisualRoot();
    }

    public void ResetRemoteCalibration() { ResetVisualHistory(); }

    // Keep the existing return-to-Body interpolation alive after remote release.
    public bool BeginRemoteRelease()
    {
        if (_mode == RemoteRelease) return true;
        if (body == null || visualRoot == null || !NeedsRemoteRelease(_mode, _visualCorrected,
            _transitioning, visualTransitionDuration)) return false;
        _mode = RemoteRelease;
        ResetVisualHistory();
        return true;
    }

    private bool NeedsRemoteRelease(int mode, bool corrected, bool transitioning, float duration)
    {
        return mode == RemoteHeld && duration > 0f && (corrected || transitioning);
    }

    public bool CaptureLock() { return CaptureLockOffset(); }

    public void ReleaseVelocity()
    {
        if (body == null) return;
        body.velocity = _dropVelocity;
        body.angularVelocity = _dropAngularVelocity;
        _hasPreviousDrivenPose = false;
    }

    public override void PostLateUpdate()
    {
        if (_mode == Inactive || body == null) return;
        if (_mode == RemoteRelease)
        {
            SetRemoteVisual(false, body.transform.position, body.transform.rotation, 0f, 0f);
            if (!_transitioning)
            {
                _mode = Inactive;
                if (_bat != null)
                    _bat.SendCustomEventDelayedFrames(nameof(BatBehaviour.MotionReleaseFinished), 1);
            }
            return;
        }
        if (_mode == RemoteHeld) { UpdateRemoteVisualCorrection(); return; }
        if (_mode == LocalHeld && SolveOwnerPose()) UpdateDropVelocity(Time.deltaTime);
        FollowAvailableProxy(_lowerProxy, lowerGrip);
        if (_mode == LocalHeld) FollowAvailableProxy(_upperProxy, upperGrip);
    }

    private void FollowAvailableProxy(int proxy, Transform anchor)
    {
        if (anchor == null || proxy == _leftProxy || proxy == _rightProxy) return;
        Transform target = GetProxyTransform(proxy);
        if (target != null) target.SetPositionAndRotation(anchor.position, anchor.rotation);
    }

    private string ProxyName(int proxy)
    {
        if (proxy == BatGripConstants.ProxyA) return "GripA";
        if (proxy == BatGripConstants.ProxyB) return "GripB";
        if (proxy == BatGripConstants.ProxyOther) return "Other";
        return "None";
    }

    private bool SolveOwnerPose()
    {
        if (lowerHand == BatGripConstants.HandNone)
        {
            if (debugGripTransitions && gripState == BatGripConstants.GripLocked && Time.time >= _nextLockedPoseLogTime)
            {
                Debug.Log("Bat Locked pose skipped: references, ownership or lower hand unavailable");
                _nextLockedPoseLogTime = Time.time + 1f;
            }
            return false;
        }
        Transform lowerProxyTransform = GetProxyTransform(_lowerProxy);
        if (lowerProxyTransform == null || (gripState == BatGripConstants.GripLocked && !_hasLockOffset))
        {
            if (debugGripTransitions && gripState == BatGripConstants.GripLocked && Time.time >= _nextLockedPoseLogTime)
            {
                Debug.Log("Bat Locked pose skipped: lower proxy or lock offset unavailable");
                _nextLockedPoseLogTime = Time.time + 1f;
            }
            return false;
        }
        Vector3 lowerPosition = lowerProxyTransform.position;
        Quaternion lowerRotation = lowerProxyTransform.rotation;

        Vector3 rootPosition;
        Quaternion rootRotation;
        if (gripState == BatGripConstants.GripLocked && _hasLockOffset)
        {
            ApplyLockOffset(lowerPosition, lowerRotation, _lockPositionOffset, _lockRotationOffset,
                out rootPosition, out rootRotation);
        }
        else if (gripState == BatGripConstants.GripTwoHandHeld && upperHand != BatGripConstants.HandNone)
        {
            Transform upperProxyTransform = GetProxyTransform(_upperProxy);
            Vector3 upperPosition = upperProxyTransform != null ? upperProxyTransform.position : lowerPosition;
            if (upperProxyTransform != null &&
                (upperPosition - lowerPosition).sqrMagnitude >= minimumTwoHandSeparation * minimumTwoHandSeparation)
            {
                Quaternion baseRotation = lowerRotation * BatPoseSolver.InvertRotation(lowerGrip.localRotation);
                rootRotation = BatPoseSolver.AlignLocalYToAxis(baseRotation, upperPosition - lowerPosition);
                rootPosition = lowerPosition - rootRotation * lowerGrip.localPosition;
            }
            else
                CalculateLowerPose(lowerPosition, lowerRotation, lowerGrip.localPosition,
                    lowerGrip.localRotation, out rootPosition, out rootRotation);
        }
        else
            CalculateLowerPose(lowerPosition, lowerRotation, lowerGrip.localPosition,
                lowerGrip.localRotation, out rootPosition, out rootRotation);

        body.transform.position = rootPosition;
        body.transform.rotation = rootRotation;
        if (debugGripTransitions && gripState == BatGripConstants.GripLocked && Time.time >= _nextLockedPoseLogTime)
        {
            Debug.Log("Bat Locked pose applied: lowerProxy=" + ProxyName(_lowerProxy) +
                " position=" + rootPosition + " rotation=" + rootRotation);
            _nextLockedPoseLogTime = Time.time + 1f;
        }
        return true;
    }

    private Transform GetProxyTransform(int proxy)
    {
        if (proxy == BatGripConstants.ProxyA) return _gripATransform;
        if (proxy == BatGripConstants.ProxyB) return _gripBTransform;
        return null;
    }

    private bool TryGetRemoteHandPose(VRCPlayerApi player, int hand,
        out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (player == null || hand == BatGripConstants.HandNone) return false;

        HumanBodyBones bone = hand == BatGripConstants.HandLeft
            ? HumanBodyBones.LeftHand
            : HumanBodyBones.RightHand;
        position = player.GetBonePosition(bone);
        rotation = player.GetBoneRotation(bone);
        if (position.sqrMagnitude < PoseEpsilon) return false;
        if (!IsFiniteValue(position.x) || !IsFiniteValue(position.y) || !IsFiniteValue(position.z)) return false;
        if (!IsFiniteValue(rotation.x) || !IsFiniteValue(rotation.y) ||
            !IsFiniteValue(rotation.z) || !IsFiniteValue(rotation.w)) return false;
        return Quaternion.Dot(rotation, rotation) > PoseEpsilon;
    }

    private void CalculateLowerPose(Vector3 handPosition, Quaternion handRotation,
        Vector3 anchorLocalPosition, Quaternion anchorLocalRotation,
        out Vector3 rootPosition, out Quaternion rootRotation)
    {
        BatPose pose = BatPoseSolver.Lower(handPosition, handRotation, anchorLocalPosition, anchorLocalRotation);
        rootPosition = pose.position;
        rootRotation = pose.rotation;
    }

    private bool CaptureLockOffset()
    {
        Transform lowerProxyTransform = GetProxyTransform(_lowerProxy);
        if (lowerProxyTransform == null || body == null)
        {
            _hasLockOffset = false;
            if (debugGripTransitions) Debug.Log("Bat CaptureLockOffset failed: lower proxy or Body missing");
            return false;
        }
        Vector3 handPosition = lowerProxyTransform.position;
        Quaternion handRotation = lowerProxyTransform.rotation;

        CaptureLockPose(handPosition, handRotation, body.transform.position, body.transform.rotation,
            out _lockPositionOffset, out _lockRotationOffset);
        _hasLockOffset = true;
        if (debugGripTransitions) Debug.Log("Bat CaptureLockOffset succeeded: lowerProxy=" + ProxyName(_lowerProxy));
        return true;
    }

    private void CaptureLockPose(Vector3 handPosition, Quaternion handRotation,
        Vector3 rootPosition, Quaternion rootRotation,
        out Vector3 positionOffset, out Quaternion rotationOffset)
    {
        BatPose pose = BatPoseSolver.CaptureLock(handPosition, handRotation, rootPosition, rootRotation);
        positionOffset = pose.position;
        rotationOffset = pose.rotation;
    }

    private void ApplyLockOffset(Vector3 handPosition, Quaternion handRotation,
        Vector3 positionOffset, Quaternion rotationOffset,
        out Vector3 rootPosition, out Quaternion rootRotation)
    {
        BatPose pose = BatPoseSolver.ApplyLock(handPosition, handRotation, positionOffset, rotationOffset);
        rootPosition = pose.position;
        rootRotation = pose.rotation;
    }

    private void ResetVelocityTracking()
    {
        _dropVelocity = Vector3.zero;
        _dropAngularVelocity = Vector3.zero;
        _previousDrivenPosition = body.transform.position;
        _previousDrivenRotation = body.transform.rotation;
        _hasPreviousDrivenPose = true;
    }

    private void UpdateDropVelocity(float deltaTime)
    {
        if (deltaTime <= PoseEpsilon) return;
        Vector3 currentPosition = body.transform.position;
        Quaternion currentRotation = body.transform.rotation;
        if (!_hasPreviousDrivenPose)
        {
            _previousDrivenPosition = currentPosition;
            _previousDrivenRotation = currentRotation;
            _hasPreviousDrivenPose = true;
            return;
        }

        _dropVelocity = BatKinematics.LinearVelocity(_previousDrivenPosition, currentPosition, deltaTime);
        _dropAngularVelocity = BatKinematics.AngularVelocity(_previousDrivenRotation, currentRotation, deltaTime);

        _previousDrivenPosition = currentPosition;
        _previousDrivenRotation = currentRotation;
    }

    private void UpdateRemoteVisualCorrection()
    {
        VRCPlayerApi owner = _owner;
        if (owner == null || owner.isLocal || gripState == BatGripConstants.GripIdle)
        {
            ResetVisualHistory();
            SetRemoteVisual(false, body.transform.position, body.transform.rotation, 0f, 0f);
            return;
        }
        if (_remoteRevision != stateRevision || _remoteOwnerId != owner.playerId)
        {
            ResetVisualHistory();
            _remoteRevision = stateRevision;
            _remoteOwnerId = owner.playerId;
        }
        Vector3 handPosition;
        Quaternion handRotation;
        if (!TryGetRemoteHandPose(owner, lowerHand, out handPosition, out handRotation))
        {
            ResetVisualHistory();
            SetRemoteVisual(false, body.transform.position, body.transform.rotation, 0f, 0f);
            return;
        }
        float linearSpeed = 0f;
        float angularSpeed = 0f;
        if (_hasSample && Time.deltaTime > PoseEpsilon)
        {
            linearSpeed = Vector3.Distance(handPosition, _lastHandPosition) / Time.deltaTime;
            angularSpeed = BatVisualCorrection.RotationAngleDegrees(handRotation, _lastHandRotation) / Time.deltaTime;
        }
        if (gripState == BatGripConstants.GripTwoHandHeld)
        {
            Vector3 upperPosition;
            Quaternion upperRotation;
            if (!TryGetRemoteHandPose(owner, upperHand, out upperPosition, out upperRotation) ||
                (upperPosition - handPosition).sqrMagnitude < minimumTwoHandSeparation * minimumTwoHandSeparation)
            {
                ResetVisualHistory();
                SetRemoteVisual(false, body.transform.position, body.transform.rotation, linearSpeed, angularSpeed);
                return;
            }
            if (_hasUpperSample && Time.deltaTime > PoseEpsilon)
                linearSpeed = Mathf.Max(linearSpeed, Vector3.Distance(upperPosition, _lastUpperPosition) / Time.deltaTime);
            _lastUpperPosition = upperPosition;
            _hasUpperSample = true;
            Quaternion basis = _visualCorrected ? visualRoot.rotation : body.transform.rotation;
            BatPose freePose = CalculateFreeVisualPose(handPosition, upperPosition, lowerGrip.localPosition, basis);
            _lastHandPosition = handPosition;
            _lastHandRotation = handRotation;
            _hasSample = true;
            SetRemoteVisual(true, freePose.position, freePose.rotation, linearSpeed, angularSpeed);
            return;
        }
        UpdateCalibration(handPosition, handRotation, body.transform.position, body.transform.rotation, Time.deltaTime);
        if (_calibrated)
        {
            BatPose pose = BatPoseSolver.ApplyLock(handPosition, handRotation, _remotePositionOffset, _remoteRotationOffset);
            SetRemoteVisual(true, pose.position, pose.rotation, linearSpeed, angularSpeed);
        }
        else SetRemoteVisual(false, body.transform.position, body.transform.rotation, linearSpeed, angularSpeed);
    }

    private BatPose CalculateFreeVisualPose(Vector3 lower, Vector3 upper, Vector3 anchor, Quaternion basis)
    {
        BatPose result = new BatPose();
        result.rotation = BatPoseSolver.AlignLocalYToAxis(basis, upper - lower);
        result.position = lower - result.rotation * anchor;
        return result;
    }

    private bool ShouldCancelTransition(float linearSpeed, float angularSpeed)
    {
        return linearSpeed > transitionCancelLinearSpeed || angularSpeed > transitionCancelAngularSpeed;
    }

    private void UpdateCalibration(Vector3 handPosition, Quaternion handRotation,
        Vector3 bodyPosition, Quaternion bodyRotation, float dt)
    {
        if (_calibrated) { _lastHandPosition = handPosition; _lastHandRotation = handRotation; return; }
        int capacity = Mathf.Clamp(velocitySmoothingSamples, 1, 64);
        if (!_hasSample || capacity != _sampleCapacity || dt <= PoseEpsilon)
        {
            _sampleCapacity = capacity;
            _sampleCount = 0;
            _sampleNext = 0;
            _stableTime = 0f;
            _stableHandPosition = handPosition;
            _stableHandRotation = handRotation;
            _stableBodyPosition = bodyPosition;
            _stableBodyRotation = bodyRotation;
            _lastHandPosition = handPosition;
            _lastHandRotation = handRotation;
            _lastBodyPosition = bodyPosition;
            _lastBodyRotation = bodyRotation;
            _hasSample = true;
            return;
        }
        _handLinearSamples[_sampleNext] = Vector3.Distance(handPosition, _lastHandPosition) / dt;
        _handAngularSamples[_sampleNext] = BatVisualCorrection.RotationAngleDegrees(handRotation, _lastHandRotation) / dt;
        _bodyLinearSamples[_sampleNext] = Vector3.Distance(bodyPosition, _lastBodyPosition) / dt;
        _bodyAngularSamples[_sampleNext] = BatVisualCorrection.RotationAngleDegrees(bodyRotation, _lastBodyRotation) / dt;
        _sampleNext = (_sampleNext + 1) % capacity;
        if (_sampleCount < capacity) _sampleCount++;
        _lastHandPosition = handPosition;
        _lastHandRotation = handRotation;
        _lastBodyPosition = bodyPosition;
        _lastBodyRotation = bodyRotation;
        float hl = 0f; float ha = 0f; float bl = 0f; float ba = 0f;
        for (int i = 0; i < _sampleCount; i++)
        {
            hl += _handLinearSamples[i]; ha += _handAngularSamples[i];
            bl += _bodyLinearSamples[i]; ba += _bodyAngularSamples[i];
        }
        bool stable = _sampleCount == capacity && hl / _sampleCount <= maxHandLinearSpeed &&
            ha / _sampleCount <= maxHandAngularSpeed && bl / _sampleCount <= maxBodyLinearSpeed &&
            ba / _sampleCount <= maxBodyAngularSpeed &&
            Vector3.Distance(handPosition, _stableHandPosition) <= maxCalibrationPositionDrift &&
            Vector3.Distance(bodyPosition, _stableBodyPosition) <= maxCalibrationPositionDrift &&
            BatVisualCorrection.RotationAngleDegrees(handRotation, _stableHandRotation) <= maxCalibrationRotationDrift &&
            BatVisualCorrection.RotationAngleDegrees(bodyRotation, _stableBodyRotation) <= maxCalibrationRotationDrift;
        if (!stable)
        {
            _stableTime = 0f;
            _stableHandPosition = handPosition; _stableHandRotation = handRotation;
            _stableBodyPosition = bodyPosition; _stableBodyRotation = bodyRotation;
            return;
        }
        _stableTime += dt;
        if (_stableTime < calibrationStableDuration) return;
        BatPose offset = BatPoseSolver.CaptureLock(handPosition, handRotation, bodyPosition, bodyRotation);
        _remotePositionOffset = offset.position;
        _remoteRotationOffset = offset.rotation;
        _calibrated = true;
    }

    private void SetRemoteVisual(bool corrected, Vector3 position, Quaternion rotation, float linearSpeed, float angularSpeed)
    {
        if (_visualCorrected != corrected)
        {
            _visualCorrected = corrected;
            _transitionPosition = visualRoot.position;
            _transitionRotation = visualRoot.rotation;
            _transitionTime = 0f;
            _transitioning = visualTransitionDuration > 0f;
        }
        if (_transitioning && ShouldCancelTransition(linearSpeed, angularSpeed))
            _transitioning = false;
        if (_transitioning)
        {
            _transitionTime += Time.deltaTime;
            float t = Mathf.Clamp01(_transitionTime / visualTransitionDuration);
            visualRoot.SetPositionAndRotation(Vector3.Lerp(_transitionPosition, position, t), Quaternion.Slerp(_transitionRotation, rotation, t));
            if (t >= 1f) _transitioning = false;
        }
        else if (corrected) visualRoot.SetPositionAndRotation(position, rotation);
        else ResetVisualRoot();
    }

    private bool IsFiniteValue(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }

    private void ResetVisualHistory()
    {
        _calibrated = false;
        _hasSample = false;
        _hasUpperSample = false;
        _sampleCount = 0;
        _stableTime = 0f;
        _remoteRevision = stateRevision;
        _remoteOwnerId = -1;
    }

    private void ResetVisualRoot()
    {
        if (visualRoot == null) return;
        visualRoot.localPosition = Vector3.zero;
        visualRoot.localRotation = Quaternion.identity;
        _visualCorrected = false;
        _transitioning = false;
    }
}
