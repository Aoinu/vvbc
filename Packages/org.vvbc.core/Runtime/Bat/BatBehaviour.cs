using Tsukimi;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon.Common;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class BatBehaviour : TsukimiBehaviour
{
    [Header("Scene References")]
    [SerializeField] private Transform lowerGrip;
    [SerializeField] private Transform upperGrip;
    [SerializeField] private Transform visualRoot;
    [SerializeField] private Rigidbody body;
    [SerializeField] private VRCObjectSync objectSync;
    [SerializeField] private VRC_Pickup gripA;
    [SerializeField] private VRC_Pickup gripB;

    [SerializeField] private BatMotionBehaviour motion;

    [Header("Diagnostics")]
    [SerializeField] private bool debugGripTransitions;

    [Header("Synchronized Grip State")]
    [UdonSynced]
    private bool isSpawned;
    [UdonSynced]
    private int gripState;
    [UdonSynced]
    private int lowerHand;
    [UdonSynced]
    private int upperHand;
    [UdonSynced]
    private int stateRevision;

    private int _lowerProxy = BatGripConstants.ProxyA;
    private int _upperProxy = BatGripConstants.ProxyB;
    private int _lastLeftProxy = BatGripConstants.ProxyNone;
    private int _lastRightProxy = BatGripConstants.ProxyNone;
    private Renderer _gripARenderer;
    private Renderer _gripBRenderer;
    private Collider _gripACollider;
    private Collider _gripBCollider;

    private Collider _bodyCollider;
    private Renderer[] _visualRenderers;
    private bool _ownershipMismatchReported;
    private bool _leftUseDown;
    private bool _rightUseDown;
    private bool _hadBodyControl;
    private bool _deferMotionUpdates;
    private bool _deferredResetVelocity;
    private bool _deferredSolvePose;
    private bool _deferredDiscontinuity;
    private bool _pickupRecoveryScheduled;
    private bool _gripSettlementScheduled;
    private int _gripEventVersion;
    private int _settlementVersion;
    private int _recentlyDroppedProxies;

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
        _visualRenderers = visualRoot.GetComponentsInChildren<Renderer>(true);
        ApplySpawnPresentation();
        ApplyBodyCollider();
        UpdateProxyPresentation(BatGripConstants.ProxyNone, BatGripConstants.ProxyNone);


        CheckBodyOwnership();
        _hadBodyControl = CanControlBody();
        if (_hadBodyControl && gripState != BatGripConstants.GripIdle)
            SetBodyHeld(true);
        RefreshHeldPickups();
        UpdateMotion(true, false, _hadBodyControl && gripState == BatGripConstants.GripLocked);
    }

    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        int hand = ToGripHand(args.handType);
        if (!value) { EndUse(hand); return; }
        if (!isSpawned || !CanControlBody()) return;
        RefreshHeldPickups();
        int proxy = hand == BatGripConstants.HandLeft ? _lastLeftProxy :
            hand == BatGripConstants.HandRight ? _lastRightProxy : BatGripConstants.ProxyNone;
        GripUse(proxy);
    }



    public override void OnDeserialization()
    {
        ApplySpawnPresentation();
        UpdateProxyPresentation(_lastLeftProxy, _lastRightProxy);
        UpdateMotion(false, false, false);
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        BodyOwnershipChanged();
    }

    public override void OnAvatarChanged(VRCPlayerApi player)
    {
        if (player == null || motion == null || !motion.gameObject.activeSelf) return;
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        if (owner != null && owner.playerId == player.playerId) motion.ResetRemoteCalibration();
    }

    // The proxy identifies the callback; hand == None is authoritative even if
    // the SDK still reports that pickup as held while OnDrop is executing.
    public void GripChanged(int proxy, int hand)
    {
        if (proxy != BatGripConstants.ProxyA && proxy != BatGripConstants.ProxyB) return;
        int proxyMask = proxy == BatGripConstants.ProxyA ? 1 : 2;
        if (hand == BatGripConstants.HandNone) _recentlyDroppedProxies |= proxyMask;
        else _recentlyDroppedProxies &= ~proxyMask;
        _gripEventVersion++;
        if (!_gripSettlementScheduled)
        {
            _gripSettlementScheduled = true;
            _settlementVersion = _gripEventVersion;
            SendCustomEventDelayedFrames(nameof(GripEventsSettled), 2);
        }
        if (hand == BatGripConstants.HandNone)
            EndUse(HandHoldingProxy(proxy, _lastLeftProxy, _lastRightProxy));
        if (_lastLeftProxy == proxy) _lastLeftProxy = BatGripConstants.ProxyNone;
        if (_lastRightProxy == proxy) _lastRightProxy = BatGripConstants.ProxyNone;
        if (hand == BatGripConstants.HandLeft) _lastLeftProxy = proxy;
        else if (hand == BatGripConstants.HandRight) _lastRightProxy = proxy;
        int previousRevision = stateRevision;
        ReconcilePickups(_lastLeftProxy, _lastRightProxy);
        UpdateProxyPresentation(_lastLeftProxy, _lastRightProxy);
        if (previousRevision == stateRevision) UpdateMotion(false, false, false);
    }

    // InputUse is the sole use-input source on both desktop and VR.
    private void GripUse(int proxy)
    {
        if (!isSpawned || !CanControlBody()) return;
        if (proxy != BatGripConstants.ProxyA && proxy != BatGripConstants.ProxyB) return;
        int hand = HandHoldingProxy(proxy, _lastLeftProxy, _lastRightProxy);
        if (hand == BatGripConstants.HandNone ||
            (hand == BatGripConstants.HandLeft && _leftUseDown) ||
            (hand == BatGripConstants.HandRight && _rightUseDown)) return;
        int nextState; int nextLower; int nextUpper;
        if (!ResolveUse(hand, out nextState, out nextLower, out nextUpper)) return;
        if (nextState == BatGripConstants.GripLocked &&
            (motion == null || !motion.gameObject.activeSelf || !motion.CaptureLock())) return;
        if (hand == BatGripConstants.HandLeft) _leftUseDown = true;
        else _rightUseDown = true;
        CommitGripState(nextState, nextLower, nextUpper, false);
    }

    private void EndUse(int hand)
    {
        if (hand == BatGripConstants.HandLeft) _leftUseDown = false;
        else if (hand == BatGripConstants.HandRight) _rightUseDown = false;
    }

    // Called by the relay on Body, and by Bat's own ownership callback.
    public void BodyOwnershipChanged()
    {
        CheckBodyOwnership();
        bool canControl = CanControlBody();
        bool recovered = canControl && !_hadBodyControl;
        _hadBodyControl = canControl;
        if (!canControl)
        {
            _leftUseDown = false;
            _rightUseDown = false;
        }
        if (recovered)
        {
            bool wasHeld = gripState != BatGripConstants.GripIdle;
            _deferMotionUpdates = true;
            RefreshHeldPickups();
            _deferMotionUpdates = false;
            if (wasHeld && gripState != BatGripConstants.GripIdle) SetBodyHeld(true);
        }
        UpdateProxyPresentation(_lastLeftProxy, _lastRightProxy);
        UpdateMotion(recovered || _deferredResetVelocity, _deferredSolvePose,
            recovered && gripState == BatGripConstants.GripLocked);
        if (_deferredDiscontinuity && objectSync != null && canControl) objectSync.FlagDiscontinuity();
        _deferredResetVelocity = false;
        _deferredSolvePose = false;
        _deferredDiscontinuity = false;
    }

    // A remote release finishes its existing visual interpolation before stopping.
    public void MotionReleaseFinished()
    {
        UpdateMotion(false, false, false);
    }

    private void RefreshHeldPickups()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        int left = FilterRecentlyDropped(GetHeldProxy(player, BatGripConstants.HandLeft));
        int right = FilterRecentlyDropped(GetHeldProxy(player, BatGripConstants.HandRight));
        ReconcilePickups(left, right);
    }

    private int FilterRecentlyDropped(int proxy)
    {
        if (proxy == BatGripConstants.ProxyA && (_recentlyDroppedProxies & 1) != 0) return BatGripConstants.ProxyNone;
        if (proxy == BatGripConstants.ProxyB && (_recentlyDroppedProxies & 2) != 0) return BatGripConstants.ProxyNone;
        return proxy;
    }

    // Recovery for a missed SDK callback; normal Pickup/Drop transitions are immediate.
    public void RecoverPickups()
    {
        _pickupRecoveryScheduled = false;
        if (!isSpawned || !CanControlBody()) return;
        if (!_gripSettlementScheduled) RefreshHeldPickups();
        SchedulePickupRecovery();
    }

    public void GripEventsSettled()
    {
        if (_settlementVersion != _gripEventVersion)
        {
            _settlementVersion = _gripEventVersion;
            SendCustomEventDelayedFrames(nameof(GripEventsSettled), 2);
            return;
        }
        _gripSettlementScheduled = false;
        _recentlyDroppedProxies = 0;
    }

    private void SchedulePickupRecovery()
    {
        if (_pickupRecoveryScheduled || !isSpawned || !CanControlBody()) return;
        _pickupRecoveryScheduled = true;
        SendCustomEventDelayedFrames(nameof(RecoverPickups), 60);
    }

    private int SelectMotionMode(bool spawned, bool localOwner, bool canControl, bool ownersMatch, int state)
    {
        if (!spawned || !ownersMatch) return BatMotionBehaviour.Inactive;
        if (localOwner)
        {
            if (!canControl) return BatMotionBehaviour.Inactive;
            return state == BatGripConstants.GripIdle ? BatMotionBehaviour.LocalFree : BatMotionBehaviour.LocalHeld;
        }
        return state == BatGripConstants.GripIdle ? BatMotionBehaviour.Inactive : BatMotionBehaviour.RemoteHeld;
    }

    private void UpdateMotion(bool resetVelocity, bool solveNow, bool recaptureLock)
    {
        if (_deferMotionUpdates) return;
        SchedulePickupRecovery();
        if (motion == null) return;
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        VRCPlayerApi bodyOwner = objectSync != null ? Networking.GetOwner(objectSync.gameObject) : null;
        bool localOwner = owner != null && owner.isLocal;
        bool ownersMatch = owner != null && bodyOwner != null && owner.playerId == bodyOwner.playerId;
        bool canControl = localOwner && ownersMatch && body != null && objectSync.gameObject == body.gameObject;
        int mode = SelectMotionMode(isSpawned, localOwner, canControl, ownersMatch, gripState);
        GameObject target = motion.gameObject;
        if (mode == BatMotionBehaviour.Inactive)
        {
            if (target.activeSelf)
            {
                if (isSpawned && ownersMatch && owner != null && !owner.isLocal &&
                    gripState == BatGripConstants.GripIdle && motion.BeginRemoteRelease()) return;
                motion.StopMotion();
                target.SetActive(false);
            }
            return;
        }
        if (!target.activeSelf) target.SetActive(true);
        motion.Configure(mode, gripState, lowerHand, upperHand, _lowerProxy, _upperProxy,
            _lastLeftProxy, _lastRightProxy, stateRevision, owner, resetVelocity, solveNow, recaptureLock, this);
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
        if ((_lastLeftProxy == BatGripConstants.ProxyA || _lastLeftProxy == BatGripConstants.ProxyB) &&
            leftProxy != _lastLeftProxy) EndUse(BatGripConstants.HandLeft);
        if ((_lastRightProxy == BatGripConstants.ProxyA || _lastRightProxy == BatGripConstants.ProxyB) &&
            rightProxy != _lastRightProxy) EndUse(BatGripConstants.HandRight);
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
        if (!isSpawned || !CanControlBody()) return;

        CommitGripState(nextState, nextLower, nextUpper, promoted);
    }

    private void SwapProxyRoles()
    {
        int previousLower = _lowerProxy;
        _lowerProxy = _upperProxy;
        _upperProxy = previousLower;
    }

    private void CommitGripState(int nextState, int nextLower,
        int nextUpper, bool promoted)
    {
        if (!CanControlBody()) return;
        CommitAuthorizedGripState(nextState, nextLower, nextUpper, promoted);
    }

    private void CommitAuthorizedGripState(int nextState, int nextLower,
        int nextUpper, bool promoted)
    {
        bool wasHeld = gripState != BatGripConstants.GripIdle;
        bool willBeHeld = nextState != BatGripConstants.GripIdle;
        if (gripState == nextState && lowerHand == nextLower && upperHand == nextUpper) return;

        if (promoted) SwapProxyRoles();
        gripState = nextState;
        lowerHand = nextLower;
        upperHand = nextUpper;
        stateRevision = stateRevision + 1;
        if (debugGripTransitions)
            Debug.Log("Bat state=" + gripState + " lowerHand=" + lowerHand + " upperHand=" + upperHand +
                " lowerProxy=" + ProxyName(_lowerProxy) + " upperProxy=" + ProxyName(_upperProxy) + " localOwner=" + IsLocalOwner());

        if (CanControlBody())
        {
            if (!wasHeld && willBeHeld)
            {
                SetBodyHeld(true);
            }
            else if (wasHeld && !willBeHeld)
            {
                SetBodyHeld(false);
                if (body != null)
                {
                    if (motion != null && motion.gameObject.activeSelf) motion.ReleaseVelocity();
                }
            }
        }

        RequestSerialization();

        ApplyBodyCollider();
        UpdateProxyPresentation(_lastLeftProxy, _lastRightProxy);
        bool resetVelocity = willBeHeld && (promoted || !wasHeld);
        bool solveNow = resetVelocity || (willBeHeld && gripState == BatGripConstants.GripLocked);
        if (_deferMotionUpdates)
        {
            _deferredResetVelocity = _deferredResetVelocity || resetVelocity;
            _deferredSolvePose = _deferredSolvePose || solveNow;
            _deferredDiscontinuity = _deferredDiscontinuity || promoted;
        }
        else
        {
            UpdateMotion(resetVelocity, solveNow, false);
            if (promoted && objectSync != null && CanControlBody()) objectSync.FlagDiscontinuity();
        }
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
        return localPlayer != null && Networking.IsOwner(localPlayer, gameObject);
    }

    private bool CanControlBody()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        return localPlayer != null && body != null && objectSync != null &&
            objectSync.gameObject == body.gameObject && Networking.IsOwner(localPlayer, gameObject) &&
            Networking.IsOwner(localPlayer, objectSync.gameObject);
    }

    private void CheckBodyOwnership()
    {
        if (objectSync == null) return;
        VRCPlayerApi batOwner = Networking.GetOwner(gameObject);
        VRCPlayerApi bodyOwner = Networking.GetOwner(objectSync.gameObject);
        if (batOwner == null || bodyOwner == null) return;
        if (batOwner.playerId == bodyOwner.playerId)
        {
            _ownershipMismatchReported = false;
            return;
        }
        if (_ownershipMismatchReported) return;
        _ownershipMismatchReported = true;
        Debug.LogError("BatBehaviour: Bat and Body owners differ; Body control and pickup are disabled until ownership matches.");
    }

    private Transform GetProxyTransform(int proxy)
    {
        if (proxy == BatGripConstants.ProxyA && gripA != null) return gripA.transform;
        if (proxy == BatGripConstants.ProxyB && gripB != null) return gripB.transform;
        return null;
    }

    private void SetBodyHeld(bool held)
    {
        if (!CanControlBody()) return;
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
        if (_bodyCollider != null) _bodyCollider.enabled = isSpawned && gripState == BatGripConstants.GripIdle;
    }

    private void ApplySpawnPresentation()
    {
        if (_visualRenderers == null && visualRoot != null)
            _visualRenderers = visualRoot.GetComponentsInChildren<Renderer>(true);
        if (_visualRenderers != null)
            for (int i = 0; i < _visualRenderers.Length; i++)
                if (_visualRenderers[i] != null) _visualRenderers[i].enabled = isSpawned;
        ApplyBodyCollider();
    }

    // Bat resolution belongs to the switch; all state and physics changes stay here.
    public bool Respawn(Vector3 position, Quaternion rotation)
    {
        if (!ReferencesReady() || !CanControlBody() || gripState != BatGripConstants.GripIdle || gripA.IsHeld || gripB.IsHeld)
            return false;
        return RespawnOwned(position, rotation);
    }

    private bool RespawnOwned(Vector3 position, Quaternion rotation)
    {
        if (!ReferencesReady() || !CanControlBody() || gripState != BatGripConstants.GripIdle || gripA.IsHeld || gripB.IsHeld)
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
        MarkSpawned();
        ApplySpawnPresentation();
        UpdateMotion(true, false, false);
        ApplyBodyCollider();
        Transform lowerProxy = GetProxyTransform(_lowerProxy);
        Transform upperProxy = GetProxyTransform(_upperProxy);
        if (lowerProxy != null) lowerProxy.SetPositionAndRotation(lowerGrip.position, lowerGrip.rotation);
        if (upperProxy != null) upperProxy.SetPositionAndRotation(upperGrip.position, upperGrip.rotation);
        UpdateProxyPresentation(BatGripConstants.ProxyNone, BatGripConstants.ProxyNone);
        return true;
    }

    private void MarkSpawned()
    {
        if (!IsLocalOwner() || isSpawned) return;
        isSpawned = true;
        RequestSerialization();
    }

    public Quaternion DefaultSpawnRotation()
    {
        // Body has identity local rotation in the Bat prefab; its parent stays fixed under ObjectSync.
        return transform.rotation;
    }

    private void UpdateProxyPresentation(int leftProxy, int rightProxy)
    {
        bool allowed = CanControlBody();
        UpdateProxy(gripA, _gripARenderer, _gripACollider, BatGripConstants.ProxyA, leftProxy, rightProxy, allowed);
        UpdateProxy(gripB, _gripBRenderer, _gripBCollider, BatGripConstants.ProxyB, leftProxy, rightProxy, allowed);
    }

    private void UpdateProxy(VRC_Pickup pickup, Renderer guide, Collider collider, int proxy,
        int leftProxy, int rightProxy, bool allowed)
    {
        if (pickup == null) return;
        bool available = CanPickupProxy(allowed, proxy);
        if (guide != null && guide.enabled != available) guide.enabled = available;
        if (collider != null && collider.enabled != available) collider.enabled = available;
        if (pickup.pickupable != available) pickup.pickupable = available;

        if (!available || proxy == leftProxy || proxy == rightProxy || pickup.IsHeld) return;
        Transform anchor = proxy == _lowerProxy ? lowerGrip : upperGrip;
        if (anchor != null) pickup.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
    }

    private bool CanPickupProxy(bool allowed, int proxy)
    {
        return isSpawned && allowed && (proxy == _lowerProxy ||
            (gripState != BatGripConstants.GripIdle && proxy == _upperProxy));
    }







}
