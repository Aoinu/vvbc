using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Persistence;

// One glove per player under the PlayerObject. The owner decides the fitting (synced
// on change and persisted in PlayerData); every client rebuilds the pose from the
// owner's hand bone each frame. The glove also presents a ball held by its owner.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class GloveBehaviour : TsukimiBehaviour
{
    public const int HandLeft = 1;
    public const int HandRight = 2;
    public const int BallNone = 0;
    public const int BallInSocket = 1;
    public const int BallInRemoteHand = 2;
    private const string KeyHand = "vvbc.glove.throwHand";
    private const string KeyPosition = "vvbc.glove.position";
    private const string KeyRotation = "vvbc.glove.rotation";
    private const string KeyScale = "vvbc.glove.scale";

    [Header("References")]
    [Tooltip("Follow target written from the glove-hand bone. Parent of the visual, trigger and socket.")]
    [SerializeField] private Transform mount;
    [Tooltip("Ball position while the ball is in this glove.")]
    [SerializeField] private Transform socket;
    [Tooltip("Catch volume. Enabled only on the owner's client while worn.")]
    [SerializeField] private SphereCollider catchTrigger;
    [Tooltip("Visual root; hidden while not worn.")]
    [SerializeField] private GameObject visual;
    [Tooltip("Optional Animator with a bool parameter named by closedParameter.")]
    [SerializeField] private Animator animator;
    [Tooltip("Pickup used only during calibration. Not a child of Mount.")]
    [SerializeField] private VRC_Pickup calibrationHandle;

    [Header("Defaults")]
    [Tooltip("Throwing hand before calibration: 1 Left, 2 Right. The glove is worn on the other hand.")]
    public int defaultThrowHand = HandRight;
    [Tooltip("Glove position relative to the glove-hand bone before calibration, in metres.")]
    public Vector3 defaultOffsetPosition = Vector3.zero;
    [Tooltip("Glove rotation relative to the glove-hand bone before calibration, in degrees.")]
    public Vector3 defaultOffsetEuler = Vector3.zero;
    [Tooltip("Uniform glove scale before calibration.")]
    public float defaultScale = 1f;

    [Header("Calibration Limits")]
    [Tooltip("Largest allowed distance from the hand bone, in metres. Default 0.30.")]
    public float maxOffsetDistance = 0.3f;
    [Tooltip("Smallest scale. Default 0.8.")] public float minScale = 0.8f;
    [Tooltip("Largest scale. Default 1.2.")] public float maxScale = 1.2f;
    [Tooltip("Scale change per UI step. Default 0.05.")] public float scaleStep = 0.05f;

    [Header("Animation")]
    [Tooltip("Animator bool set while the ball is in this glove.")]
    public string closedParameter = "Closed";

    [Header("Debug (local only)")]
    [Tooltip("Show the catch volume as a translucent sphere to the glove's owner while worn or calibrating. Local, not synced, no effect on catching. Default off.")]
    public bool showCatchVolume = false;
    [Tooltip("Display-only sphere (unit mesh, no collider) under the catch trigger; it is fitted to the trigger's center and radius.")]
    [SerializeField] private Transform catchVolumeVisual;

    // Synced fitting; written only by the owner, sent only on change.
    [UdonSynced, HideInInspector] public bool equipped;
    [UdonSynced, HideInInspector] public int throwHand = HandRight;
    [UdonSynced, HideInInspector] public Vector3 offsetPosition;
    [UdonSynced, HideInInspector] public Quaternion offsetRotation = new Quaternion(0f, 0f, 0f, 1f);
    [UdonSynced, HideInInspector] public float gloveScale = 1f;

    // Read by the local Ball's catch check; valid only on the owner's client.
    [HideInInspector] public bool catchReady;
    [HideInInspector] public Vector3 catchCenter;
    [HideInInspector] public float catchRadius;

    private VRCPlayerApi _owner;
    private bool _isLocalOwner;
    private bool _initialized;
    private bool _received;
    private bool _calibrating;
    private bool _handleHeld;
    private Transform _handleTransform;
    private Transform _catchTransform;

    // Fitting used for the pose: the synced values, or the drafts while calibrating.
    private int _activeHand = HandRight;
    private Vector3 _activePosition;
    private Quaternion _activeRotation = new Quaternion(0f, 0f, 0f, 1f);
    private float _activeScale = 1f;
    private float _appliedScale = -1f;

    private int _ballMode;
    private Transform _ball;
    private Vector3 _ballOffset;
    private bool _closed;
    private GloveLockerBehaviour _listener;
    private bool _catchVolumeShown;
    private float _catchVolumeRadius = -1f;
    private Vector3 _catchVolumeCenter;

    private void Start()
    {
        Initialize();
    }

    private void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (calibrationHandle != null) _handleTransform = calibrationHandle.transform;
        if (catchTrigger != null) _catchTransform = catchTrigger.transform;
        RefreshOwner();
        // Every client starts from the same prefab defaults; a received fitting wins.
        if (!_received)
        {
            throwHand = ValidHand(defaultThrowHand) ? defaultThrowHand : HandRight;
            offsetPosition = defaultOffsetPosition;
            offsetRotation = Quaternion.Euler(defaultOffsetEuler);
            gloveScale = defaultScale;
        }
        UseSyncedFitting();
        if (calibrationHandle != null)
        {
            calibrationHandle.pickupable = false;
            calibrationHandle.gameObject.SetActive(false);
        }
        ApplyPresentation();
    }

    private void RefreshOwner()
    {
        _owner = Networking.GetOwner(gameObject);
        _isLocalOwner = Utilities.IsValid(_owner) && _owner.isLocal;
    }

    public bool IsLocalOwner()
    {
        Initialize();
        return _isLocalOwner;
    }

    public bool IsCalibrating() { return _calibrating; }
    public bool IsHandleHeld() { return _handleHeld; }
    public bool HoldsBall() { return _ballMode != BallNone; }
    public int DraftThrowHand() { return _activeHand; }
    public float DraftScale() { return _activeScale; }
    public float DraftOffsetDistance() { return _activePosition.magnitude; }

    public bool DraftValid()
    {
        return FieldingMath.FittingValid(_activePosition, _activeScale, maxOffsetDistance, minScale, maxScale) &&
            FieldingMath.ValidRotation(_activeRotation);
    }

    public bool IsCatchCollider(Collider other)
    {
        return other != null && other == catchTrigger;
    }

    public void SetListener(GloveLockerBehaviour listener)
    {
        _listener = listener;
    }

    public override void OnDeserialization()
    {
        _received = true;
        Initialize();
        if (!_calibrating) UseSyncedFitting();
        ApplyPresentation();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        RefreshOwner();
        ApplyPresentation();
    }

    // Presentation is local: visual shown while worn or calibrating; the catch
    // trigger exists only on the owner's client.
    private void ApplyPresentation()
    {
        bool visible = equipped || _calibrating;
        if (visual != null && visual.activeSelf != visible) visual.SetActive(visible);
        if (catchTrigger != null) catchTrigger.enabled = _isLocalOwner && equipped && !_calibrating;
        if (!_isLocalOwner || !equipped || _calibrating) catchReady = false;
        ApplyClosed();
        UpdateCatchVolumeVisual();
    }

    // The catch volume is drawn only for the owner, while the glove is worn or calibrating.
    public bool CatchVolumeVisible()
    {
        return showCatchVolume && _isLocalOwner && (equipped || _calibrating);
    }

    // For a future settings UI; the Inspector field works the same way.
    public void SetCatchVolumeVisible(bool value)
    {
        showCatchVolume = value;
        UpdateCatchVolumeVisual();
    }

    // The visual is a child of the trigger, so it follows Mount (bone, calibration and scale)
    // through the hierarchy. Only its local fit to the trigger's center and radius is written,
    // and only when they change. Nothing runs here while the display is off.
    private void UpdateCatchVolumeVisual()
    {
        if (catchVolumeVisual == null) return;
        bool show = catchTrigger != null && CatchVolumeVisible();
        if (show != _catchVolumeShown)
        {
            _catchVolumeShown = show;
            catchVolumeVisual.gameObject.SetActive(show);
        }
        if (!show) return;
        float radius = catchTrigger.radius;
        Vector3 center = catchTrigger.center;
        if (radius == _catchVolumeRadius && center == _catchVolumeCenter) return;
        _catchVolumeRadius = radius;
        _catchVolumeCenter = center;
        catchVolumeVisual.localPosition = center;
        catchVolumeVisual.localScale = CatchVolumeLocalScale(radius, catchVolumeVisual.parent.lossyScale);
    }

    // A SphereCollider's world radius is its radius times the largest absolute axis of its
    // lossy scale; a unit sphere mesh under it is scaled per axis. This local scale makes the
    // mesh that same sphere, also under a non-uniform parent scale.
    public static Vector3 CatchVolumeLocalScale(float radius, Vector3 parentLossy)
    {
        float x = Mathf.Abs(parentLossy.x), y = Mathf.Abs(parentLossy.y), z = Mathf.Abs(parentLossy.z);
        float largest = Mathf.Max(x, Mathf.Max(y, z));
        float diameter = 2f * radius * largest;
        if (!(x > 0.000001f) || !(y > 0.000001f) || !(z > 0.000001f)) return Vector3.one * (2f * radius);
        return new Vector3(diameter / x, diameter / y, diameter / z);
    }

    private void ApplyClosed()
    {
        if (animator == null || string.IsNullOrEmpty(closedParameter)) return;
        if (visual != null && !visual.activeInHierarchy) return;
        animator.SetBool(closedParameter, _closed);
    }

    public override void PostLateUpdate()
    {
        if (!equipped && !_calibrating && _ballMode == BallNone) return;
        if (!Utilities.IsValid(_owner))
        {
            RefreshOwner();
            if (!Utilities.IsValid(_owner)) { catchReady = false; return; }
        }
        if (equipped || _calibrating || _ballMode == BallInSocket) UpdateMount();
        if (_ball == null) return;
        if (_ballMode == BallInSocket && socket != null) _ball.SetPositionAndRotation(socket.position, socket.rotation);
        else if (_ballMode == BallInRemoteHand) UpdateRemoteHandBall();
    }

    private void UpdateMount()
    {
        if (mount == null) return;
        HumanBodyBones bone = _activeHand == HandLeft ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand;
        Vector3 bonePosition = _owner.GetBonePosition(bone);
        Quaternion boneRotation = _owner.GetBoneRotation(bone);
        bool valid = FieldingMath.ValidPose(bonePosition, boneRotation);
        if (_calibrating && _handleTransform != null) UpdateCalibration(valid, bonePosition, boneRotation);
        else if (valid) mount.SetPositionAndRotation(bonePosition + boneRotation * _activePosition, boneRotation * _activeRotation);
        if (_appliedScale != _activeScale)
        {
            _appliedScale = _activeScale;
            mount.localScale = new Vector3(_activeScale, _activeScale, _activeScale);
            if (catchTrigger != null)
            {
                Vector3 lossy = _catchTransform.lossyScale;
                catchRadius = catchTrigger.radius * Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
            }
        }
        catchReady = valid && _isLocalOwner && equipped && !_calibrating && catchTrigger != null;
        if (catchReady) catchCenter = _catchTransform.TransformPoint(catchTrigger.center);
        // Debug only: picks up the Inspector toggle and trigger edits while the display is on.
        if (showCatchVolume || _catchVolumeShown) UpdateCatchVolumeVisual();
    }

    // While held, the handle owns the glove pose and the draft offset follows it; the
    // glove hand moving does not move the held glove. While free, the handle sits on Mount.
    private void UpdateCalibration(bool valid, Vector3 bonePosition, Quaternion boneRotation)
    {
        bool held = calibrationHandle.IsHeld;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (held && Utilities.IsValid(local) && local.IsUserInVR() &&
            calibrationHandle.currentHand != (_activeHand == HandLeft ? VRC_Pickup.PickupHand.Left : VRC_Pickup.PickupHand.Right))
        {
            calibrationHandle.Drop();
            held = false;
        }
        if (held)
        {
            Vector3 handlePosition = _handleTransform.position;
            Quaternion handleRotation = _handleTransform.rotation;
            mount.SetPositionAndRotation(handlePosition, handleRotation);
            if (valid)
            {
                Quaternion inverse = Quaternion.Inverse(boneRotation);
                _activePosition = inverse * (handlePosition - bonePosition);
                _activeRotation = inverse * handleRotation;
            }
        }
        else
        {
            if (valid) mount.SetPositionAndRotation(bonePosition + boneRotation * _activePosition, boneRotation * _activeRotation);
            _handleTransform.SetPositionAndRotation(mount.position, mount.rotation);
        }
        if (held != _handleHeld)
        {
            _handleHeld = held;
            NotifyListener();
        }
    }

    private void UpdateRemoteHandBall()
    {
        HumanBodyBones bone = throwHand == HandLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
        Vector3 position = _owner.GetBonePosition(bone);
        Quaternion rotation = _owner.GetBoneRotation(bone);
        if (FieldingMath.ValidPose(position, rotation)) _ball.position = position + rotation * _ballOffset;
    }

    // Called by the Ball. mode is BallInSocket or BallInRemoteHand; offset is in the
    // throwing-hand bone frame for BallInRemoteHand.
    public void HoldBall(Transform ball, int mode, Vector3 offset)
    {
        Initialize();
        _ball = ball;
        _ballMode = ball == null ? BallNone : mode;
        _ballOffset = offset;
        SetClosed(_ballMode == BallInSocket);
        NotifyListener();
    }

    public void ReleaseBall()
    {
        _ball = null;
        _ballMode = BallNone;
        SetClosed(false);
        NotifyListener();
    }

    private void SetClosed(bool closed)
    {
        if (_closed == closed) return;
        _closed = closed;
        ApplyClosed();
    }

    public void SetEquipped(bool value)
    {
        Initialize();
        if (!_isLocalOwner || equipped == value) return;
        if (!value && (_ballMode != BallNone || _calibrating)) return;
        equipped = value;
        RequestSerialization();
        ApplyPresentation();
        NotifyListener();
    }

    public bool BeginCalibration()
    {
        Initialize();
        if (!_isLocalOwner || _calibrating || _ballMode != BallNone) return false;
        if (!equipped)
        {
            equipped = true;
            RequestSerialization();
        }
        _calibrating = true;
        _handleHeld = false;
        UseSyncedFitting();
        if (calibrationHandle != null)
        {
            calibrationHandle.gameObject.SetActive(true);
            calibrationHandle.pickupable = true;
        }
        ApplyPresentation();
        NotifyListener();
        return true;
    }

    // Switching hands starts the new hand from the defaults: bone frames of the two
    // hands are not mirror images on every avatar.
    public void SetDraftThrowHand(int hand)
    {
        if (!_calibrating || !ValidHand(hand) || hand == _activeHand) return;
        DropHandle();
        _activeHand = hand;
        _activePosition = defaultOffsetPosition;
        _activeRotation = Quaternion.Euler(defaultOffsetEuler);
        NotifyListener();
    }

    public void StepDraftScale(int direction)
    {
        if (!_calibrating) return;
        _activeScale = FieldingMath.StepScale(_activeScale, direction, scaleStep, minScale, maxScale);
        NotifyListener();
    }

    public bool ConfirmCalibration()
    {
        if (!CommitDraft()) return false;
        SaveFitting();
        return true;
    }

    // Validates and sends the draft, then leaves calibration. PlayerData is written by the caller.
    private bool CommitDraft()
    {
        if (!_calibrating || !_isLocalOwner || !DraftValid()) return false;
        throwHand = _activeHand;
        offsetPosition = _activePosition;
        offsetRotation = Normalize(_activeRotation);
        gloveScale = _activeScale;
        RequestSerialization();
        EndCalibration();
        return true;
    }

    public void CancelCalibration()
    {
        if (!_calibrating) return;
        EndCalibration();
    }

    private void EndCalibration()
    {
        DropHandle();
        _calibrating = false;
        _handleHeld = false;
        if (calibrationHandle != null)
        {
            calibrationHandle.pickupable = false;
            calibrationHandle.gameObject.SetActive(false);
        }
        UseSyncedFitting();
        ApplyPresentation();
        NotifyListener();
    }

    private void DropHandle()
    {
        if (calibrationHandle != null && calibrationHandle.IsHeld) calibrationHandle.Drop();
    }

    private void UseSyncedFitting()
    {
        _activeHand = ValidHand(throwHand) ? throwHand : HandRight;
        _activePosition = offsetPosition;
        _activeRotation = FieldingMath.ValidRotation(offsetRotation) ? offsetRotation : Quaternion.identity;
        _activeScale = gloveScale;
    }

    private void SaveFitting()
    {
        PlayerData.SetInt(KeyHand, throwHand);
        PlayerData.SetVector3(KeyPosition, offsetPosition);
        PlayerData.SetQuaternion(KeyRotation, offsetRotation);
        PlayerData.SetFloat(KeyScale, gloveScale);
    }

    public override void OnPlayerRestored(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        Initialize();
        if (!_isLocalOwner) return;
        int hand = throwHand;
        Vector3 position = offsetPosition;
        Quaternion rotation = offsetRotation;
        float scale = gloveScale;
        bool found = false;
        if (PlayerData.TryGetInt(player, KeyHand, out int storedHand)) { hand = storedHand; found = true; }
        if (PlayerData.TryGetVector3(player, KeyPosition, out Vector3 storedPosition)) { position = storedPosition; found = true; }
        if (PlayerData.TryGetQuaternion(player, KeyRotation, out Quaternion storedRotation)) { rotation = storedRotation; found = true; }
        if (PlayerData.TryGetFloat(player, KeyScale, out float storedScale)) { scale = storedScale; found = true; }
        if (!found) return;
        if (!RestoreFitting(hand, position, rotation, scale))
            Debug.LogWarning("Glove: ignored a stored fitting outside the calibration limits");
    }

    // Applies a restored fitting; sends it only when it differs from the current one.
    public bool RestoreFitting(int hand, Vector3 position, Quaternion rotation, float scale)
    {
        if (!ValidHand(hand) || !FieldingMath.ValidRotation(rotation) ||
            !FieldingMath.FittingValid(position, scale, maxOffsetDistance, minScale, maxScale)) return false;
        rotation = Normalize(rotation);
        if (hand == throwHand && position == offsetPosition && rotation == offsetRotation && scale == gloveScale) return true;
        throwHand = hand;
        offsetPosition = position;
        offsetRotation = rotation;
        gloveScale = scale;
        RequestSerialization();
        if (!_calibrating) UseSyncedFitting();
        NotifyListener();
        return true;
    }

    private void NotifyListener()
    {
        if (_listener != null) _listener.OnGloveChanged();
    }

    private bool ValidHand(int hand)
    {
        return hand == HandLeft || hand == HandRight;
    }

    private Quaternion Normalize(Quaternion rotation)
    {
        float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
        if (!(length > 0.000001f)) return Quaternion.identity;
        return new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
    }
}
