using UnityEngine;
using VRC.SDKBase;

public partial class BallBehaviour
{
    [Header("Bounce Audio")]
    [Tooltip("Collision clip; may be left unassigned.")] public AudioClip impactClip;
    // An empty mask accepts all layers; assign a mask to restrict collisions.
    [Tooltip("Collision layers that produce audio; zero accepts all layers.")] public LayerMask impactLayers;
    [Tooltip("Play only on the local owner when enabled.")] public bool requireLocalOwner;
    [Tooltip("Minimum normal collision speed, in m/s.")] public float minimumSpeed = 0.8f;
    [Tooltip("Normal collision speed for maximum volume, in m/s.")] public float maximumSpeed = 10f;
    [Range(0f, 1f), Tooltip("Volume at minimum speed.")] public float minimumVolume = 0.35f;
    [Range(0f, 1f), Tooltip("Volume at maximum speed.")] public float maximumVolume = 1f;
    [Range(0.1f, 3f), Tooltip("Impact playback pitch.")] public float pitch = 1f;
    [Tooltip("Minimum time between impacts, in seconds.")] public float cooldown = 0.1f;
    private float _nextImpactTime;

    private void PlayImpact(Collision collision)
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

    [Header("Wind Audio")]
    [Tooltip("Flight wind clip. Playback is one-shot until reset.")] public AudioClip windClip;
    [Tooltip("Speed where wind begins, in metres per second.")] public float windStartSpeed = 20f;
    [Tooltip("Speed where wind reaches maximum volume, in metres per second.")] public float windMaxSpeed = 40f;
    [Range(0f, 1f), Tooltip("Maximum wind AudioSource volume.")] public float windMaxVolume = 0.35f;

    private void UpdateWind()
    {
        if (windAudio == null) return;
        if (!flying || body == null || windClip == null)
        { if (windAudio.isPlaying) windAudio.Stop(); windAudio.volume = 0f; return; }
        float volume = WindVolume(body.velocity.magnitude, windStartSpeed, windMaxSpeed, windMaxVolume);
        windAudio.volume = volume;
        if (volume > 0f && !windAudio.isPlaying && windAudio.clip != windClip)
        { windAudio.clip = windClip; windAudio.Play(); }
    }

    private float WindVolume(float speed, float start, float maximum, float volume)
    {
        if (maximum <= start) return speed >= start ? volume : 0f;
        return Mathf.Clamp01((speed - start) / (maximum - start)) * volume;
    }

    private void StopBallAudio()
    {
        if (windAudio != null) { windAudio.Stop(); windAudio.volume = 0f; windAudio.clip = null; }
        if (impactAudio != null) impactAudio.Stop();
    }
}
