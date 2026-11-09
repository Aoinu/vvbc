using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Put this on the colliding Rigidbody, independently of physics observers.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class FeedbackImpactAudioBehaviour : TsukimiBehaviour
{
    public AudioSource impactAudio;
    public AudioClip impactClip;
    // An empty mask accepts all layers; assign a mask to restrict collisions.
    public LayerMask impactLayers;
    public bool requireLocalOwner;
    public float minimumSpeed = 0.8f;
    public float maximumSpeed = 10f;
    public float minimumVolume = 0.35f;
    public float maximumVolume = 1f;
    public float pitch = 1f;
    public float cooldown = 0.1f;
    [Header("Bat Contact")]
    public AudioSource contactAudio;
    public AudioClip contactClip;
    public float contactMinVolume = 0.45f;
    public float contactMaxVolume = 1f;
    public float contactMinPitch = 0.8f;
    public float contactMaxPitch = 1.2f;
    public float hapticDuration = 0.08f;
    public float hapticAmplitude = 0.5f;
    public float hapticFrequency = 0.5f;
    [HideInInspector] public float remoteContactStrength;
    [HideInInspector] public Vector3 remoteContactPosition;
    private float _nextImpactTime;

    public void PlayLocalContact(Vector3 position, float strength, int hands)
    {
        PlayContact(position, strength);
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player == null || !player.IsUserInVR()) return;
        if ((hands & 1) != 0) player.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, hapticDuration, hapticAmplitude, hapticFrequency);
        if ((hands & 2) != 0) player.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, hapticDuration, hapticAmplitude, hapticFrequency);
    }

    public void PlayRemoteContact()
    {
        PlayContact(remoteContactPosition, remoteContactStrength);
    }

    private void PlayContact(Vector3 position, float strength)
    {
        if (contactAudio == null || contactClip == null) return;
        strength = Mathf.Clamp01(strength);
        // Contact Audio is a Bat-root child (does not follow Body); place it once at the contact point.
        contactAudio.transform.position = position;
        contactAudio.clip = contactClip;
        contactAudio.volume = ContactVolume(strength);
        contactAudio.pitch = ContactPitch(strength);
        contactAudio.Play();
    }


    private float ContactVolume(float strength)
    {
        return Mathf.Lerp(contactMinVolume, contactMaxVolume, strength);
    }

    private float ContactPitch(float strength)
    {
        return Mathf.Lerp(contactMinPitch, contactMaxPitch, strength);
    }

    public void OnCollisionEnter(Collision collision)
    {
        if (collision == null || collision.contactCount == 0 || impactAudio == null || impactClip == null ||
            Time.time < _nextImpactTime || (impactLayers.value != 0 &&
            (impactLayers.value & (1 << collision.gameObject.layer)) == 0)) return;
        if (requireLocalOwner && (Networking.LocalPlayer == null || !Networking.IsOwner(Networking.LocalPlayer, gameObject))) return;
        ContactPoint point = collision.GetContact(0);
        float speed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, point.normal));
        float volume = ImpactVolume(speed, minimumSpeed, maximumSpeed, minimumVolume, maximumVolume);
        if (speed < minimumSpeed) return;
        _nextImpactTime = Time.time + Mathf.Max(0f, cooldown);
        impactAudio.clip = impactClip;
        impactAudio.pitch = pitch;
        impactAudio.volume = volume;
        impactAudio.Play();
    }

    private float ImpactVolume(float speed, float minimum, float maximum, float lowVolume, float highVolume)
    {
        if (speed < minimum) return 0f;
        if (maximum <= minimum) return highVolume;
        return Mathf.Lerp(lowVolume, highVolume, (speed - minimum) / (maximum - minimum));
    }
}
