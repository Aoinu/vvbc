using Tsukimi;
using UnityEngine;
using VRC.Udon;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class BallBehaviour : TsukimiBehaviour
{
    [Header("References")]
    [Tooltip("Rigidbody on the Ball root.")] public Rigidbody body;
    [Tooltip("Collider on the Ball root.")] public SphereCollider ballCollider;
    [Tooltip("Renderer for the physical ball model.")] public Renderer ballRenderer;
    [Tooltip("Speed trail following the ball.")] public TrailRenderer speedTrail;
    [Tooltip("World-space flight path renderer.")] public LineRenderer flightPath;
    [Tooltip("Bounce audio source on the ball.")] public AudioSource impactAudio;
    [Tooltip("Wind audio source on the ball.")] public AudioSource windAudio;
    [Tooltip("Optional strike-zone observer.")] public PitchCrossingBehaviour motionObserver;
    [HideInInspector] public UdonBehaviour feedbackListener;
    [Header("Flight Physics")]
    [Tooltip("Aerodynamic drag coefficient, in inverse metres. Default 0.00622.")] public float dragCoefficient = 0.00622f;
    [Tooltip("Magnus coefficient. Default 0.00063; spin is in radians per second.")] public float magnusCoefficient = 0.00063f;
    [Tooltip("Ground surfaces reported to the strike observer.")] public LayerMask groundLayers;

    // The owner of the Ball lifecycle turns flight on only for a free ball.
    [HideInInspector] public bool flying;

    // Local generation and contact state. This is not a network authority token.
    [HideInInspector] public int flightSequence;
    [HideInInspector] public bool contactConsumed;
    private bool _contactPending;
    private int _pendingSequence;
    private float _pendingTime;
    private Vector3 _pendingPosition, _pendingVelocity, _pendingSpin;

}
