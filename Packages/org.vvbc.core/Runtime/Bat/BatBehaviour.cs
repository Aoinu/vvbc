using Tsukimi;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class BatBehaviour : TsukimiBehaviour
{
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
    public float maxHandLinearSpeed = 0.05f;
    public float maxHandAngularSpeed = 15f;
    public float maxBodyLinearSpeed = 0.05f;
    public float maxBodyAngularSpeed = 15f;
    public float calibrationStableDuration = 0.4f;
    [Range(1, 64)] public int velocitySmoothingSamples = 5;
    public float maxCalibrationPositionDrift = 0.02f;
    public float maxCalibrationRotationDrift = 5f;
    public float visualTransitionDuration = 0.15f;
    public float transitionCancelLinearSpeed = 0.15f;
    public float transitionCancelAngularSpeed = 45f;

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

    private Collider _bodyCollider;
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

        _bodyCollider = body.GetComponent<Collider>();
        ApplyBodyCollider();
        UpdateProxyPresentation(BatGripConstants.ProxyNone, BatGripConstants.ProxyNone);
        ResetVisualRoot();
        if (body != null) ResetVelocityTracking();

        if (IsLocalOwner() && gripState != BatGripConstants.GripIdle)
            SetBodyHeld(true);
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
        ApplyBodyCollider();
        UpdateRemoteVisualCorrection();
    }

    public override void OnDeserialization()
    {
        ResetVisualHistory();
        ApplyBodyCollider();
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

        ApplyBodyCollider();
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

    private void ApplyBodyCollider()
    {
        if (_bodyCollider == null && body != null) _bodyCollider = body.GetComponent<Collider>();
        if (_bodyCollider != null) _bodyCollider.enabled = gripState == BatGripConstants.GripIdle;
    }

    // Bat resolution belongs to the switch; all state and physics changes stay here.
    public bool Respawn(Vector3 position, Quaternion rotation)
    {
        if (!ReferencesReady() || gripState != BatGripConstants.GripIdle || gripA.IsHeld || gripB.IsHeld)
            return false;
        if (!IsLocalOwner())
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RespawnRequest), position, rotation);
            return false;
        }
        return RespawnOwned(position, rotation);
    }

    [NetworkCallable]
    public void RespawnRequest(Vector3 position, Quaternion rotation)
    {
        RespawnOwned(position, rotation);
    }

    private bool RespawnOwned(Vector3 position, Quaternion rotation)
    {
        if (!ReferencesReady() || !IsLocalOwner() || gripState != BatGripConstants.GripIdle || gripA.IsHeld || gripB.IsHeld)
            return false;
        if (!body.isKinematic)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        objectSync.SetKinematic(true);
        objectSync.SetGravity(false);
        body.isKinematic = true;
        body.useGravity = false;
        body.transform.SetPositionAndRotation(position, rotation);
        objectSync.FlagDiscontinuity();
        ResetVelocityTracking();
        ResetVisualHistory();
        ResetVisualRoot();
        ApplyBodyCollider();
        Transform lowerProxy = GetProxyTransform(_lowerProxy);
        Transform upperProxy = GetProxyTransform(_upperProxy);
        if (lowerProxy != null) lowerProxy.SetPositionAndRotation(lowerGrip.position, lowerGrip.rotation);
        if (upperProxy != null) upperProxy.SetPositionAndRotation(upperGrip.position, upperGrip.rotation);
        UpdateProxyPresentation(BatGripConstants.ProxyNone, BatGripConstants.ProxyNone);
        return true;
    }

    public Quaternion DefaultSpawnRotation()
    {
        // Body has identity local rotation in the Bat prefab; its parent stays fixed under ObjectSync.
        return transform.rotation;
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
