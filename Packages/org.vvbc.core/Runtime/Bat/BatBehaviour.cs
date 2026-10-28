using Tsukimi;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon.Common;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class BatBehaviour : TsukimiBehaviour
{
    private const int HistoryCapacity = 32;
    private const float PoseEpsilon = 0.0001f;

    [Header("Scene References")]
    public Transform lowerGrip;
    public Transform upperGrip;
    public Transform visualRoot;
    public Rigidbody body;
    public VRCObjectSync objectSync;
    public VRC_Pickup gripA;
    public VRC_Pickup gripB;

    [Header("Grip Pose")]
    [Range(0.01f, 0.2f)]
    public float minimumTwoHandSeparation = 0.04f;

    [Header("Remote Visual Correction")]
    [Range(0.1f, 2f)]
    public float maximumVisualPositionDelta = 1.25f;
    [Range(15f, 180f)]
    public float maximumVisualRotationDelta = 120f;
    [Range(0.05f, 0.5f)]
    public float maximumVisualTimeGap = 0.35f;
    [Range(0.05f, 1.5f)]
    public float maximumHandSampleStep = 0.65f;
    [Range(15f, 180f)]
    public float maximumHandSampleRotationStep = 100f;
    [Range(0.05f, 0.5f)]
    public float maximumHistorySampleGap = 0.25f;

    [Header("Diagnostics")]
    public bool debugGripTransitions;

    [Header("Synchronized Grip State")]
    [UdonSynced]
    public int gripState;
    [UdonSynced]
    public int lowerHand;
    [UdonSynced]
    public int upperHand;
    [UdonSynced]
    public int stateRevision;

    private Vector3 _lockPositionOffset;
    private Quaternion _lockRotationOffset = Quaternion.identity;
    private bool _hasLockOffset;
    private float _nextLockedPoseLogTime;
    private int _lowerProxy = BatGripConstants.ProxyA;
    private int _upperProxy = BatGripConstants.ProxyB;
    private int _lastLeftProxy = -1;
    private int _lastRightProxy = -1;
    private Renderer _gripARenderer;
    private Renderer _gripBRenderer;
    private Collider _gripACollider;
    private Collider _gripBCollider;

    private Vector3 _dropVelocity;
    private Vector3 _dropAngularVelocity;
    private Vector3 _previousDrivenPosition;
    private Quaternion _previousDrivenRotation = Quaternion.identity;
    private bool _hasPreviousDrivenPose;

    private readonly float[] _historyTimes = new float[HistoryCapacity];
    private readonly Vector3[] _historyPositions = new Vector3[HistoryCapacity];
    private readonly Quaternion[] _historyRotations = new Quaternion[HistoryCapacity];
    private int _historyNext;
    private int _historyCount;
    private int _historyRevision = -1;
    private int _historyOwnerId = -1;

    public void Start()
    {
        if (!ReferencesReady())
        {
            Debug.LogError("BatBehaviour: assign body, objectSync, lowerGrip, upperGrip, visualRoot, gripA and gripB in the Inspector.");
            return;
        }

        if (gripA != null)
        {
            _gripARenderer = gripA.GetComponent<Renderer>();
            _gripACollider = gripA.GetComponent<Collider>();
        }
        if (gripB != null)
        {
            _gripBRenderer = gripB.GetComponent<Renderer>();
            _gripBCollider = gripB.GetComponent<Collider>();
        }

        UpdateProxyPresentation(BatGripConstants.ProxyNone, BatGripConstants.ProxyNone);
        ResetVisualRoot();
        if (body != null) ResetVelocityTracking();

        if (IsLocalOwner())
            SetBodyHeld(gripState != BatGripConstants.GripIdle);
    }

    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        if (debugGripTransitions) Debug.Log("Bat InputUse received");
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        int leftProxy = GetHeldProxy(localPlayer, BatGripConstants.HandLeft);
        int rightProxy = GetHeldProxy(localPlayer, BatGripConstants.HandRight);
        if (debugGripTransitions)
        {
            bool batOwner = localPlayer != null && Networking.IsOwner(localPlayer, gameObject);
            bool bodyOwner = localPlayer != null && objectSync != null &&
                Networking.IsOwner(localPlayer, objectSync.gameObject);
            Debug.Log("Bat InputUse: pressed=" + value + " handType=" + (int)args.handType +
                " left=" + ProxyName(leftProxy) + " right=" + ProxyName(rightProxy));
            Debug.Log("Bat InputUse: batOwner=" + batOwner + " bodyOwner=" + bodyOwner +
                " state=" + gripState + " lowerHand=" + lowerHand + " upperHand=" + upperHand);
        }
        if (!value)
        {
            if (debugGripTransitions) Debug.Log("Bat InputUse rejected: release event");
            return;
        }
        if (localPlayer == null || !IsLocalOwner())
        {
            if (debugGripTransitions) Debug.Log("Bat InputUse rejected: no local player or Bat/Body ownership");
            return;
        }
        ReconcilePickups(leftProxy, rightProxy);

        int hand = ToGripHand(args.handType);
        int heldProxy = hand == BatGripConstants.HandLeft ? leftProxy :
            hand == BatGripConstants.HandRight ? rightProxy : BatGripConstants.ProxyNone;
        if (!IsLocalOwner() || (heldProxy != BatGripConstants.ProxyA && heldProxy != BatGripConstants.ProxyB))
        {
            if (debugGripTransitions) Debug.Log("Bat InputUse rejected: input hand does not hold GripA/B or ownership changed");
            return;
        }

        int nextState;
        int nextLower;
        int nextUpper;
        if (!ResolveUse(hand, out nextState, out nextLower, out nextUpper))
        {
            if (debugGripTransitions) Debug.Log("Bat InputUse rejected: hand is not a current grip holder or state cannot toggle");
            return;
        }

        if (nextState == BatGripConstants.GripLocked && !CaptureLockOffset())
        {
            if (debugGripTransitions) Debug.Log("Bat InputUse rejected: CaptureLockOffset failed");
            return;
        }

        if (debugGripTransitions) Debug.Log("Bat InputUse accepted: state " + gripState + " -> " + nextState);
        CommitGripState(nextState, nextLower, nextUpper, false);
    }

    public override void PostLateUpdate()
    {
        if (!ReferencesReady())
        {
            ResetVisualRoot();
            return;
        }

        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        int leftProxy = GetHeldProxy(localPlayer, BatGripConstants.HandLeft);
        int rightProxy = GetHeldProxy(localPlayer, BatGripConstants.HandRight);
        ReconcilePickups(leftProxy, rightProxy);

        if (IsLocalOwner())
        {
            if (gripState == BatGripConstants.GripIdle)
            {
                UpdateProxyPresentation(leftProxy, rightProxy);
                ResetVisualRoot();
                ResetVisualHistory();
                return;
            }

            if (SolveOwnerPose())
                UpdateDropVelocity(Time.deltaTime);
            UpdateProxyPresentation(leftProxy, rightProxy);
            ResetVisualRoot();
            return;
        }

        UpdateProxyPresentation(leftProxy, rightProxy);
        if (gripState == BatGripConstants.GripIdle)
        {
            ResetVisualRoot();
            ResetVisualHistory();
            return;
        }

        UpdateRemoteVisualCorrection();
    }

    public override void OnDeserialization()
    {
        ResetVisualHistory();
        if (gripState == BatGripConstants.GripIdle) ResetVisualRoot();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        ResetVisualHistory();
        if (body != null) ResetVelocityTracking();
        if (player != null && player.isLocal)
        {
            ResetVisualRoot();
            if (gripState != BatGripConstants.GripIdle) SetBodyHeld(true);
            if (gripState == BatGripConstants.GripLocked) _hasLockOffset = CaptureLockOffset();
            else _hasLockOffset = false;
        }
    }

    public override void OnAvatarChanged(VRCPlayerApi player)
    {
        if (player == null) return;
        VRCPlayerApi owner = objectSync != null ? Networking.GetOwner(objectSync.gameObject) : null;
        if (owner == null || owner.playerId != player.playerId) return;
        ResetVisualHistory();
        ResetVisualRoot();
    }

    private int GetHeldProxy(VRCPlayerApi player, int hand)
    {
        if (player == null || hand == BatGripConstants.HandNone) return BatGripConstants.ProxyNone;
        VRC_Pickup pickup = player.GetPickupInHand(hand == BatGripConstants.HandLeft
            ? VRC_Pickup.PickupHand.Left : VRC_Pickup.PickupHand.Right);
        if (pickup == null) return BatGripConstants.ProxyNone;
        if (pickup == gripA) return BatGripConstants.ProxyA;
        if (pickup == gripB) return BatGripConstants.ProxyB;
        return BatGripConstants.ProxyOther;
    }

    private string ProxyName(int proxy)
    {
        if (proxy == BatGripConstants.ProxyA) return "GripA";
        if (proxy == BatGripConstants.ProxyB) return "GripB";
        if (proxy == BatGripConstants.ProxyOther) return "Other";
        return "None";
    }

    private int HandHoldingProxy(int proxy, int leftProxy, int rightProxy)
    {
        return BatGripLogic.HandHoldingProxy(proxy, leftProxy, rightProxy);
    }

    private void ReconcilePickups(int leftProxy, int rightProxy)
    {
        if (debugGripTransitions && (leftProxy != _lastLeftProxy || rightProxy != _lastRightProxy))
            Debug.Log("Bat pickups: left=" + ProxyName(leftProxy) + " right=" + ProxyName(rightProxy) +
                " state=" + gripState + " lowerProxy=" + ProxyName(_lowerProxy) + " upperProxy=" + ProxyName(_upperProxy) +
                " localOwner=" + IsLocalOwner());
        _lastLeftProxy = leftProxy;
        _lastRightProxy = rightProxy;

        int lowerHeldHand = HandHoldingProxy(_lowerProxy, leftProxy, rightProxy);
        int upperHeldHand = HandHoldingProxy(_upperProxy, leftProxy, rightProxy);
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        if (!ResolvePickupState(lowerHeldHand, upperHeldHand,
                out nextState, out nextLower, out nextUpper, out promoted)) return;
        if (gripState == BatGripConstants.GripIdle)
        {
            if (!EnsureLocalOwnership()) return;
        }
        else if (!IsLocalOwner()) return;

        if (promoted) SwapProxyRoles();
        CommitGripState(nextState, nextLower, nextUpper, promoted);
    }

    private void SwapProxyRoles()
    {
        int previousLower = _lowerProxy;
        _lowerProxy = _upperProxy;
        _upperProxy = previousLower;
    }

    private bool EnsureLocalOwnership()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer == null || body == null || objectSync == null) return false;

        GameObject rootObject = objectSync.gameObject;
        if (!Networking.IsOwner(localPlayer, rootObject)) Networking.SetOwner(localPlayer, rootObject);
        if (!Networking.IsOwner(localPlayer, gameObject)) Networking.SetOwner(localPlayer, gameObject);
        return Networking.IsOwner(localPlayer, rootObject) &&
            Networking.IsOwner(localPlayer, gameObject);
    }

    private void CommitGripState(int nextState, int nextLower,
        int nextUpper, bool promoted)
    {
        bool wasHeld = gripState != BatGripConstants.GripIdle;
        bool willBeHeld = nextState != BatGripConstants.GripIdle;
        if (gripState == nextState && lowerHand == nextLower && upperHand == nextUpper) return;

        if (gripState == BatGripConstants.GripLocked && nextState != BatGripConstants.GripLocked)
            _hasLockOffset = false;
        gripState = nextState;
        lowerHand = nextLower;
        upperHand = nextUpper;
        stateRevision = stateRevision + 1;
        if (debugGripTransitions)
            Debug.Log("Bat state=" + gripState + " lowerHand=" + lowerHand + " upperHand=" + upperHand +
                " lowerProxy=" + ProxyName(_lowerProxy) + " upperProxy=" + ProxyName(_upperProxy) + " localOwner=" + IsLocalOwner());

        if (IsLocalOwner())
        {
            if (!wasHeld && willBeHeld)
            {
                SetBodyHeld(true);
                ResetVelocityTracking();
            }
            else if (wasHeld && !willBeHeld)
            {
                SetBodyHeld(false);
                if (body != null)
                {
                    body.velocity = _dropVelocity;
                    body.angularVelocity = _dropAngularVelocity;
                }
                _hasLockOffset = false;
            }
            else if (willBeHeld)
                SetBodyHeld(true);

            if (willBeHeld && (promoted || !wasHeld))
            {
                SolveOwnerPose();
                ResetVelocityTracking();
                if (promoted && objectSync != null) objectSync.FlagDiscontinuity();
            }
            else if (willBeHeld && gripState == BatGripConstants.GripLocked)
                SolveOwnerPose();

            RequestSerialization();
        }

        if (!willBeHeld) ResetVisualRoot();
        ResetVisualHistory();
    }

    private bool ResolvePickupState(int lowerHeldHand, int upperHeldHand,
        out int nextState, out int nextLower, out int nextUpper, out bool promoted)
    {
        BatGripTransition result = BatGripLogic.ResolvePickup(gripState, lowerHand, upperHand,
            lowerHeldHand, upperHeldHand);
        nextState = result.state;
        nextLower = result.lowerHand;
        nextUpper = result.upperHand;
        promoted = result.promoted;
        return result.changed;
    }

    private bool ResolveUse(int hand, out int nextState, out int nextLower, out int nextUpper)
    {
        BatGripTransition result = BatGripLogic.ResolveUse(gripState, lowerHand, upperHand, hand);
        nextState = result.state;
        nextLower = result.lowerHand;
        nextUpper = result.upperHand;
        return result.changed;
    }

    private int ToGripHand(HandType hand)
    {
        if (hand == HandType.LEFT) return BatGripConstants.HandLeft;
        if (hand == HandType.RIGHT) return BatGripConstants.HandRight;
        return BatGripConstants.HandNone;
    }

    private bool ReferencesReady()
    {
        return lowerGrip != null && upperGrip != null && visualRoot != null &&
            body != null && objectSync != null && gripA != null && gripB != null;
    }

    private bool IsLocalOwner()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        return localPlayer != null && body != null && objectSync != null &&
            Networking.IsOwner(localPlayer, objectSync.gameObject) &&
            Networking.IsOwner(localPlayer, gameObject);
    }

    private bool SolveOwnerPose()
    {
        if (!ReferencesReady() || !IsLocalOwner() || lowerHand == BatGripConstants.HandNone)
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
        if (proxy == BatGripConstants.ProxyA && gripA != null) return gripA.transform;
        if (proxy == BatGripConstants.ProxyB && gripB != null) return gripB.transform;
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

    private void SetBodyHeld(bool held)
    {
        if (body != null && held && !body.isKinematic)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        if (objectSync != null)
        {
            objectSync.SetKinematic(held);
            objectSync.SetGravity(!held);
        }
        if (body == null) return;

        body.isKinematic = held;
        body.useGravity = !held;
    }

    private void UpdateProxyPresentation(int leftProxy, int rightProxy)
    {
        bool remoteHeld = gripState != BatGripConstants.GripIdle && !IsLocalOwner();
        UpdateProxy(gripA, _gripARenderer, _gripACollider, BatGripConstants.ProxyA, leftProxy, rightProxy, remoteHeld);
        UpdateProxy(gripB, _gripBRenderer, _gripBCollider, BatGripConstants.ProxyB, leftProxy, rightProxy, remoteHeld);
    }

    private void UpdateProxy(VRC_Pickup pickup, Renderer guide, Collider collider, int proxy,
        int leftProxy, int rightProxy, bool remoteHeld)
    {
        if (pickup == null) return;
        bool available = !remoteHeld && (proxy == _lowerProxy || (gripState != BatGripConstants.GripIdle && proxy == _upperProxy));
        if (guide != null && guide.enabled != available) guide.enabled = available;
        if (collider != null && collider.enabled != available) collider.enabled = available;
        if (pickup.pickupable != available) pickup.pickupable = available;

        if (!available || proxy == leftProxy || proxy == rightProxy || pickup.IsHeld) return;
        Transform anchor = proxy == _lowerProxy ? lowerGrip : upperGrip;
        if (anchor != null) pickup.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
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
        VRCPlayerApi owner = Networking.GetOwner(objectSync.gameObject);
        if (owner == null || owner.isLocal)
        {
            ResetVisualRoot();
            ResetVisualHistory();
            return;
        }

        if (_historyRevision != stateRevision || _historyOwnerId != owner.playerId)
        {
            ResetVisualHistory();
            _historyRevision = stateRevision;
            _historyOwnerId = owner.playerId;
        }

        Vector3 currentFramePosition;
        Quaternion currentFrameRotation;
        if (!TryBuildRemoteGripFrame(owner, out currentFramePosition, out currentFrameRotation))
        {
            ResetVisualRoot();
            ResetVisualHistory();
            return;
        }

        float sampleTime = Networking.SimulationTime(owner);
        if (!IsFiniteTime(sampleTime))
        {
            ResetVisualRoot();
            ResetVisualHistory();
            return;
        }
        RecordRemoteGripFrame(sampleTime, currentFramePosition, currentFrameRotation);

        float objectTime = Networking.SimulationTime(objectSync.gameObject);
        Vector3 pastFramePosition;
        Quaternion pastFrameRotation;
        if (!TryGetGripFrameAt(objectTime, out pastFramePosition, out pastFrameRotation))
        {
            ResetVisualRoot();
            return;
        }

        Vector3 correctedPosition;
        Quaternion correctedRotation;
        bool valid = TryCalculateVisualPose(objectTime, sampleTime,
            pastFramePosition, pastFrameRotation,
            currentFramePosition, currentFrameRotation,
            body.transform.position, body.transform.rotation,
            out correctedPosition, out correctedRotation);
        if (!valid)
        {
            ResetVisualRoot();
            ResetVisualHistory();
            RecordRemoteGripFrame(sampleTime, currentFramePosition, currentFrameRotation);
            return;
        }

        visualRoot.position = correctedPosition;
        visualRoot.rotation = correctedRotation;
    }

    private bool TryBuildRemoteGripFrame(VRCPlayerApi owner,
        out Vector3 framePosition, out Quaternion frameRotation)
    {
        framePosition = Vector3.zero;
        frameRotation = Quaternion.identity;
        Vector3 lowerPosition;
        Quaternion lowerRotation;
        if (!TryGetRemoteHandPose(owner, lowerHand, out lowerPosition, out lowerRotation)) return false;

        framePosition = lowerPosition;
        frameRotation = lowerRotation;
        if (gripState != BatGripConstants.GripTwoHandHeld || upperHand == BatGripConstants.HandNone) return true;

        Vector3 upperPosition;
        Quaternion upperRotation;
        if (!TryGetRemoteHandPose(owner, upperHand, out upperPosition, out upperRotation)) return false;
        Vector3 axis = upperPosition - lowerPosition;
        if (axis.sqrMagnitude < minimumTwoHandSeparation * minimumTwoHandSeparation) return true;

        Quaternion baseRotation = lowerRotation * BatPoseSolver.InvertRotation(lowerGrip.localRotation);
        frameRotation = BatPoseSolver.AlignLocalYToAxis(baseRotation, axis);
        return true;
    }

    private void RecordRemoteGripFrame(float time, Vector3 position, Quaternion rotation)
    {
        if (_historyCount > 0)
        {
            int newest = _historyNext - 1;
            if (newest < 0) newest += HistoryCapacity;
            float timeStep = time - _historyTimes[newest];
            if (timeStep < -PoseEpsilon || timeStep > maximumHistorySampleGap)
                ResetVisualHistory();
            else if (Mathf.Abs(timeStep) <= PoseEpsilon)
            {
                _historyPositions[newest] = position;
                _historyRotations[newest] = rotation;
                return;
            }
            else if ((position - _historyPositions[newest]).sqrMagnitude >
                maximumHandSampleStep * maximumHandSampleStep ||
                BatVisualCorrection.RotationAngleDegrees(rotation, _historyRotations[newest]) > maximumHandSampleRotationStep)
            {
                ResetVisualHistory();
            }
        }

        _historyTimes[_historyNext] = time;
        _historyPositions[_historyNext] = position;
        _historyRotations[_historyNext] = rotation;
        _historyNext = (_historyNext + 1) % HistoryCapacity;
        if (_historyCount < HistoryCapacity) _historyCount++;
    }

    private bool TryGetGripFrameAt(float targetTime, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (_historyCount == 0 || !IsFiniteTime(targetTime)) return false;

        int oldest = _historyNext - _historyCount;
        while (oldest < 0) oldest += HistoryCapacity;
        int first = oldest;
        if (_historyCount == 1)
        {
            if (Mathf.Abs(targetTime - _historyTimes[first]) > PoseEpsilon) return false;
            position = _historyPositions[first];
            rotation = _historyRotations[first];
            return true;
        }

        for (int i = 0; i < _historyCount - 1; i++)
        {
            int index0 = (oldest + i) % HistoryCapacity;
            int index1 = (index0 + 1) % HistoryCapacity;
            float time0 = _historyTimes[index0];
            float time1 = _historyTimes[index1];
            if (targetTime < time0 || targetTime > time1) continue;
            float span = time1 - time0;
            float blend = span > PoseEpsilon ? (targetTime - time0) / span : 0f;
            position = Vector3.Lerp(_historyPositions[index0], _historyPositions[index1], blend);
            rotation = Quaternion.Slerp(_historyRotations[index0], _historyRotations[index1], blend);
            return true;
        }

        int newest = _historyNext - 1;
        if (newest < 0) newest += HistoryCapacity;
        if (Mathf.Abs(targetTime - _historyTimes[oldest]) <= PoseEpsilon)
        {
            position = _historyPositions[oldest];
            rotation = _historyRotations[oldest];
            return true;
        }
        if (Mathf.Abs(targetTime - _historyTimes[newest]) <= PoseEpsilon)
        {
            position = _historyPositions[newest];
            rotation = _historyRotations[newest];
            return true;
        }
        return false;
    }

    private bool TryCalculateVisualPose(float pastTime, float currentTime,
        Vector3 pastFramePosition, Quaternion pastFrameRotation,
        Vector3 currentFramePosition, Quaternion currentFrameRotation,
        Vector3 objectPosition, Quaternion objectRotation,
        out Vector3 correctedPosition, out Quaternion correctedRotation)
    {
        BatVisualResult result = BatVisualCorrection.Calculate(pastTime, currentTime,
            pastFramePosition, pastFrameRotation, currentFramePosition, currentFrameRotation,
            objectPosition, objectRotation, maximumVisualTimeGap,
            maximumVisualPositionDelta, maximumVisualRotationDelta);
        correctedPosition = result.position;
        correctedRotation = result.rotation;
        return result.valid;
    }

    private bool IsFiniteTime(float value)
    {
        return IsFiniteValue(value);
    }

    private bool IsFiniteValue(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }

    private void ResetVisualHistory()
    {
        _historyNext = 0;
        _historyCount = 0;
        _historyRevision = stateRevision;
        _historyOwnerId = -1;
    }

    private void ResetVisualRoot()
    {
        if (visualRoot == null) return;
        visualRoot.localPosition = Vector3.zero;
        visualRoot.localRotation = Quaternion.identity;
    }
}
