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
    public float minimumVolume = 0.1f;
    public float maximumVolume = 1f;
    public float pitch = 1f;
    public float cooldown = 0.1f;
    private float _nextImpactTime;

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
