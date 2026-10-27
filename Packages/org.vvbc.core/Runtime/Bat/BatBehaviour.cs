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
    private const int GripIdle = 0;
    private const int GripLowerHeld = 1;
    private const int GripTwoHandHeld = 2;
    private const int GripLocked = 3;
    private const int HandNone = 0;
    private const int HandLeft = 1;
    private const int HandRight = 2;
    private const int ProxyNone = 0;
    private const int ProxyA = 1;
    private const int ProxyB = 2;
    private const int ProxyOther = 3;

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
    private int _lowerProxy = ProxyA;
    private int _upperProxy = ProxyB;
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
        Transform rootTransform = transform.parent;
        if (body == null && rootTransform != null) body = rootTransform.GetComponent<Rigidbody>();
        if (objectSync == null && rootTransform != null) objectSync = rootTransform.GetComponent<VRCObjectSync>();
        if (body != null && lowerGrip == null) lowerGrip = body.transform.Find("LowerGripAnchor");
        if (body != null && upperGrip == null) upperGrip = body.transform.Find("UpperGripAnchor");
        if (body != null && visualRoot == null) visualRoot = body.transform.Find("VisualRoot");

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

        UpdateProxyPresentation(ProxyNone, ProxyNone);
        ResetVisualRoot();
        if (body != null) ResetVelocityTracking();

        if (IsLocalOwner())
            SetBodyHeld(gripState != GripIdle);
    }

    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        if (!value || !IsLocalOwner()) return;

        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer == null) return;
        int leftProxy = GetHeldProxy(localPlayer, HandLeft);
        int rightProxy = GetHeldProxy(localPlayer, HandRight);
        ReconcilePickups(leftProxy, rightProxy);

        int hand = ToGripHand(args.handType);
        if (!IsLocalOwner() || hand != lowerHand ||
            HandHoldingProxy(_lowerProxy, leftProxy, rightProxy) != hand) return;

        int nextState;
        int nextLower;
        int nextUpper;
        if (!ResolveUse(hand, out nextState, out nextLower, out nextUpper)) return;

        if (gripState == GripTwoHandHeld && nextState == GripLocked &&
            !CaptureLockOffset()) return;
        else if (gripState == GripLocked && nextState != GripLocked)
            _hasLockOffset = false;

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
        int leftProxy = GetHeldProxy(localPlayer, HandLeft);
        int rightProxy = GetHeldProxy(localPlayer, HandRight);
        ReconcilePickups(leftProxy, rightProxy);

        if (IsLocalOwner())
        {
            if (gripState == GripIdle)
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
        if (gripState == GripIdle)
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
        if (gripState == GripIdle) ResetVisualRoot();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        ResetVisualHistory();
        if (body != null) ResetVelocityTracking();
        if (player != null && player.isLocal)
        {
            ResetVisualRoot();
            if (gripState != GripIdle) SetBodyHeld(true);
            if (gripState == GripLocked) _hasLockOffset = CaptureLockOffset();
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
        if (player == null || hand == HandNone) return ProxyNone;
        VRC_Pickup pickup = player.GetPickupInHand(hand == HandLeft
            ? VRC_Pickup.PickupHand.Left : VRC_Pickup.PickupHand.Right);
        if (pickup == null) return ProxyNone;
        if (pickup == gripA) return ProxyA;
        if (pickup == gripB) return ProxyB;
        return ProxyOther;
    }

    private string ProxyName(int proxy)
    {
        if (proxy == ProxyA) return "GripA";
        if (proxy == ProxyB) return "GripB";
        if (proxy == ProxyOther) return "Other";
        return "None";
    }

    private int HandHoldingProxy(int proxy, int leftProxy, int rightProxy)
    {
        if (proxy == ProxyNone) return HandNone;
        if (leftProxy == proxy) return HandLeft;
        if (rightProxy == proxy) return HandRight;
        return HandNone;
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
        if (gripState == GripIdle)
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
        bool wasHeld = gripState != GripIdle;
        bool willBeHeld = nextState != GripIdle;
        if (gripState == nextState && lowerHand == nextLower && upperHand == nextUpper) return;

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
            else if (willBeHeld && gripState == GripLocked)
                SolveOwnerPose();

            RequestSerialization();
        }

        if (!willBeHeld) ResetVisualRoot();
        ResetVisualHistory();
    }

    private bool ResolvePickupState(int lowerHeldHand, int upperHeldHand,
        out int nextState, out int nextLower, out int nextUpper, out bool promoted)
    {
        nextState = gripState;
        nextLower = lowerHand;
        nextUpper = upperHand;
        promoted = false;

        if (gripState == GripIdle)
        {
            if (lowerHeldHand == HandNone) return false;
            nextState = GripLowerHeld;
            nextLower = lowerHeldHand;
            nextUpper = HandNone;
            return true;
        }

        if (lowerHeldHand == HandNone)
        {
            if (upperHeldHand != HandNone)
            {
                nextState = GripLowerHeld;
                nextLower = upperHeldHand;
                nextUpper = HandNone;
                promoted = true;
            }
            else
            {
                nextState = GripIdle;
                nextLower = HandNone;
                nextUpper = HandNone;
            }
            return true;
        }

        nextLower = lowerHeldHand;
        nextUpper = upperHeldHand != lowerHeldHand ? upperHeldHand : HandNone;
        if (gripState != GripLocked)
            nextState = nextUpper == HandNone ? GripLowerHeld : GripTwoHandHeld;
        return nextState != gripState || nextLower != lowerHand || nextUpper != upperHand;
    }

    private bool ResolveUse(int hand, out int nextState,
        out int nextLower, out int nextUpper)
    {
        nextState = gripState;
        nextLower = lowerHand;
        nextUpper = upperHand;
        if (hand == HandNone || hand != lowerHand) return false;

        if (gripState == GripTwoHandHeld)
        {
            nextState = GripLocked;
            return true;
        }

        if (gripState == GripLocked)
        {
            nextState = upperHand != HandNone
                ? GripTwoHandHeld
                : GripLowerHeld;
            return true;
        }

        return false;
    }

    private int ToGripHand(HandType hand)
    {
        if (hand == HandType.LEFT) return HandLeft;
        if (hand == HandType.RIGHT) return HandRight;
        return HandNone;
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
        if (!ReferencesReady() || !IsLocalOwner() || lowerHand == HandNone) return false;
        Transform lowerProxyTransform = GetProxyTransform(_lowerProxy);
        if (lowerProxyTransform == null) return false;
        Vector3 lowerPosition = lowerProxyTransform.position;
        Quaternion lowerRotation = lowerProxyTransform.rotation;

        Vector3 rootPosition;
        Quaternion rootRotation;
        if (gripState == GripLocked && _hasLockOffset)
        {
            ApplyLockOffset(lowerPosition, lowerRotation, _lockPositionOffset, _lockRotationOffset,
                out rootPosition, out rootRotation);
        }
        else if (gripState == GripTwoHandHeld && upperHand != HandNone)
        {
            Transform upperProxyTransform = GetProxyTransform(_upperProxy);
            Vector3 upperPosition = upperProxyTransform != null ? upperProxyTransform.position : lowerPosition;
            if (upperProxyTransform != null &&
                (upperPosition - lowerPosition).sqrMagnitude >= minimumTwoHandSeparation * minimumTwoHandSeparation)
            {
                Quaternion baseRotation = lowerRotation * InvertRotation(lowerGrip.localRotation);
                rootRotation = AlignLocalYToAxis(baseRotation, upperPosition - lowerPosition);
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
        return true;
    }

    private Transform GetProxyTransform(int proxy)
    {
        if (proxy == ProxyA && gripA != null) return gripA.transform;
        if (proxy == ProxyB && gripB != null) return gripB.transform;
        return null;
    }

    private bool TryGetRemoteHandPose(VRCPlayerApi player, int hand,
        out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (player == null || hand == HandNone) return false;

        HumanBodyBones bone = hand == HandLeft
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
        rootRotation = handRotation * InvertRotation(anchorLocalRotation);
        rootPosition = handPosition - rootRotation * anchorLocalPosition;
    }

    private Quaternion AlignLocalYToAxis(Quaternion baseRotation, Vector3 targetAxis)
    {
        Vector3 up = targetAxis.normalized;
        if (up.sqrMagnitude < PoseEpsilon) return baseRotation;

        Vector3 right = baseRotation * Vector3.right;
        right = right - up * Vector3.Dot(right, up);
        if (right.sqrMagnitude < PoseEpsilon)
        {
            Vector3 forward = baseRotation * Vector3.forward;
            forward = forward - up * Vector3.Dot(forward, up);
            if (forward.sqrMagnitude >= PoseEpsilon)
                right = Vector3.Cross(up, forward);
        }

        if (right.sqrMagnitude < PoseEpsilon)
        {
            Vector3 fallback = Mathf.Abs(Vector3.Dot(up, Vector3.forward)) < 0.9f
                ? Vector3.forward
                : Vector3.right;
            right = Vector3.Cross(up, fallback);
        }

        right = right.normalized;
        Vector3 forwardAxis = Vector3.Cross(right, up).normalized;
        return RotationFromBasis(right, up, forwardAxis);
    }

    private Quaternion InvertRotation(Quaternion rotation)
    {
        float norm = rotation.x * rotation.x + rotation.y * rotation.y +
            rotation.z * rotation.z + rotation.w * rotation.w;
        if (norm < PoseEpsilon) return Quaternion.identity;

        Quaternion inverse = Quaternion.identity;
        inverse.x = -rotation.x / norm;
        inverse.y = -rotation.y / norm;
        inverse.z = -rotation.z / norm;
        inverse.w = rotation.w / norm;
        return inverse;
    }

    private Quaternion RotationFromBasis(Vector3 right, Vector3 up, Vector3 forward)
    {
        float m00 = right.x;
        float m01 = up.x;
        float m02 = forward.x;
        float m10 = right.y;
        float m11 = up.y;
        float m12 = forward.y;
        float m20 = right.z;
        float m21 = up.z;
        float m22 = forward.z;
        float trace = m00 + m11 + m22;
        Quaternion result = Quaternion.identity;

        if (trace > 0f)
        {
            float scale = Mathf.Sqrt(trace + 1f) * 2f;
            result.w = 0.25f * scale;
            result.x = (m21 - m12) / scale;
            result.y = (m02 - m20) / scale;
            result.z = (m10 - m01) / scale;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float scale = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
            result.w = (m21 - m12) / scale;
            result.x = 0.25f * scale;
            result.y = (m01 + m10) / scale;
            result.z = (m02 + m20) / scale;
        }
        else if (m11 > m22)
        {
            float scale = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
            result.w = (m02 - m20) / scale;
            result.x = (m01 + m10) / scale;
            result.y = 0.25f * scale;
            result.z = (m12 + m21) / scale;
        }
        else
        {
            float scale = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
            result.w = (m10 - m01) / scale;
            result.x = (m02 + m20) / scale;
            result.y = (m12 + m21) / scale;
            result.z = 0.25f * scale;
        }

        return result;
    }

    private bool CaptureLockOffset()
    {
        Transform lowerProxyTransform = GetProxyTransform(_lowerProxy);
        if (lowerProxyTransform == null || body == null) return false;
        Vector3 handPosition = lowerProxyTransform.position;
        Quaternion handRotation = lowerProxyTransform.rotation;

        CaptureLockPose(handPosition, handRotation, body.transform.position, body.transform.rotation,
            out _lockPositionOffset, out _lockRotationOffset);
        _hasLockOffset = true;
        return true;
    }

    private void CaptureLockPose(Vector3 handPosition, Quaternion handRotation,
        Vector3 rootPosition, Quaternion rootRotation,
        out Vector3 positionOffset, out Quaternion rotationOffset)
    {
        Quaternion inverseHandRotation = InvertRotation(handRotation);
        positionOffset = inverseHandRotation * (rootPosition - handPosition);
        rotationOffset = inverseHandRotation * rootRotation;
    }

    private void ApplyLockOffset(Vector3 handPosition, Quaternion handRotation,
        Vector3 positionOffset, Quaternion rotationOffset,
        out Vector3 rootPosition, out Quaternion rootRotation)
    {
        rootPosition = handPosition + handRotation * positionOffset;
        rootRotation = handRotation * rotationOffset;
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
        bool remoteHeld = gripState != GripIdle && !IsLocalOwner();
        UpdateProxy(gripA, _gripARenderer, _gripACollider, ProxyA, leftProxy, rightProxy, remoteHeld);
        UpdateProxy(gripB, _gripBRenderer, _gripBCollider, ProxyB, leftProxy, rightProxy, remoteHeld);
    }

    private void UpdateProxy(VRC_Pickup pickup, Renderer guide, Collider collider, int proxy,
        int leftProxy, int rightProxy, bool remoteHeld)
    {
        if (pickup == null) return;
        bool available = !remoteHeld && (proxy == _lowerProxy || (gripState != GripIdle && proxy == _upperProxy));
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

        _dropVelocity = (currentPosition - _previousDrivenPosition) / deltaTime;
        Quaternion deltaRotation = currentRotation * InvertRotation(_previousDrivenRotation);
        float angle;
        Vector3 axis;
        deltaRotation.ToAngleAxis(out angle, out axis);
        if (angle > 180f) angle = angle - 360f;
        _dropAngularVelocity = axis.sqrMagnitude > PoseEpsilon
            ? axis.normalized * (angle * Mathf.Deg2Rad / deltaTime)
            : Vector3.zero;

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
        if (gripState != GripTwoHandHeld || upperHand == HandNone) return true;

        Vector3 upperPosition;
        Quaternion upperRotation;
        if (!TryGetRemoteHandPose(owner, upperHand, out upperPosition, out upperRotation)) return false;
        Vector3 axis = upperPosition - lowerPosition;
        if (axis.sqrMagnitude < minimumTwoHandSeparation * minimumTwoHandSeparation) return true;

        Quaternion baseRotation = lowerRotation * InvertRotation(lowerGrip.localRotation);
        frameRotation = AlignLocalYToAxis(baseRotation, axis);
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
                RotationAngleDegrees(rotation, _historyRotations[newest]) > maximumHandSampleRotationStep)
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
        correctedPosition = objectPosition;
        correctedRotation = objectRotation;
        float timeGap = currentTime - pastTime;
        if (float.IsNaN(timeGap) ||
            timeGap < -PoseEpsilon || timeGap > maximumVisualTimeGap) return false;

        float positionDelta = Vector3.Distance(pastFramePosition, currentFramePosition);
        float rotationDelta = RotationAngleDegrees(pastFrameRotation, currentFrameRotation);
        if (float.IsNaN(positionDelta) || float.IsNaN(rotationDelta) ||
            positionDelta > maximumVisualPositionDelta ||
            rotationDelta > maximumVisualRotationDelta) return false;

        Quaternion frameDelta = currentFrameRotation * InvertRotation(pastFrameRotation);
        correctedPosition = currentFramePosition + frameDelta * (objectPosition - pastFramePosition);
        correctedRotation = frameDelta * objectRotation;
        if (Vector3.Distance(correctedPosition, objectPosition) > maximumVisualPositionDelta) return false;
        if (RotationAngleDegrees(correctedRotation, objectRotation) > maximumVisualRotationDelta) return false;
        return true;
    }

    private float RotationAngleDegrees(Quaternion first, Quaternion second)
    {
        float firstNorm = Mathf.Sqrt(first.x * first.x + first.y * first.y + first.z * first.z + first.w * first.w);
        float secondNorm = Mathf.Sqrt(second.x * second.x + second.y * second.y + second.z * second.z + second.w * second.w);
        if (firstNorm < PoseEpsilon || secondNorm < PoseEpsilon) return 180f;

        float dot = (first.x * second.x + first.y * second.y + first.z * second.z + first.w * second.w) /
            (firstNorm * secondNorm);
        dot = Mathf.Clamp(Mathf.Abs(dot), 0f, 1f);
        return 2f * Mathf.Acos(dot) * Mathf.Rad2Deg;
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
