using UnityEngine;
using VRC.SDKBase;

// Catch sound and haptics. The Ball decides that a catch happened and passes the catch
// position, the ball speed just before the catch and whether that flight was a pitch.
// Sound plays from a source that does not move with Mount or the ball; haptics only on
// the catcher's own client. Nothing here is synced or runs outside a catch.
public partial class GloveBehaviour
{
    [Header("Catch Sound")]
    [Tooltip("3D AudioSources placed once at the catch point; not under Mount. Only an idle source is used, so a playing sound never moves. Spatial Blend, distances and rolloff are set on each source.")]
    public AudioSource[] catchAudioSources;
    [Tooltip("Catch clip for hits, throws, slower pitches and unknown flights. When empty no catch sound plays.")]
    public AudioClip normalCatchClip;
    [Tooltip("Catch clip for a pitch caught at or above fastPitchThresholdKmh. When empty normalCatchClip is used.")]
    public AudioClip fastPitchCatchClip;
    [Tooltip("Ball speed just before the catch from which a pitch uses fastPitchCatchClip, in km/h. Default 130.")]
    public float fastPitchThresholdKmh = 130f;
    [Tooltip("Speed at or below which the catch uses the low volume and pitch, in km/h. Default 30.")]
    public float catchSlowSpeedKmh = 30f;
    [Tooltip("Speed at or above which the catch uses the high volume and pitch, in km/h. Default 150.")]
    public float catchFastSpeedKmh = 150f;
    [Range(0f, 1f), Tooltip("Volume of a slow catch. Default 0.35.")] public float catchMinVolume = 0.35f;
    [Range(0f, 1f), Tooltip("Volume of a fast catch. Default 1.")] public float catchMaxVolume = 1f;
    [Range(0.5f, 2f), Tooltip("Playback pitch of a slow catch. Default 0.9.")] public float catchMinPitch = 0.9f;
    [Range(0.5f, 2f), Tooltip("Playback pitch of a fast catch. Default 1.1.")] public float catchMaxPitch = 1.1f;

    [Header("Catch Haptics")]
    [Tooltip("Vibrate the glove-hand controller of the local catcher in VR. Default on.")]
    public bool catchHaptics = true;
    [Range(0f, 0.5f), Tooltip("Vibration length, in seconds. Default 0.05.")] public float catchHapticDuration = 0.05f;
    [Range(0f, 1f), Tooltip("Vibration strength. Default 0.3.")] public float catchHapticAmplitude = 0.3f;
    [Range(0f, 320f), Tooltip("Vibration frequency passed to PlayHapticEventInHand. Default 0.5, as for bat contact.")]
    public float catchHapticFrequency = 0.5f;

    private const float MinCatchPitch = 0.5f;
    private const float MaxCatchPitch = 2f;
    private const float MaxCatchHapticDuration = 0.5f;
    private const float MaxCatchHapticFrequency = 320f;
    private int _nextCatchAudio;

    // Diagnostics: sounds started and haptic events requested on this client.
    [HideInInspector] public int catchSoundsPlayed;
    [HideInInspector] public int catchHapticsPlayed;

    // speedMetersPerSecond below zero means unknown: the slow settings and the normal clip.
    public void PlayCatch(Vector3 position, float speedMetersPerSecond, bool pitch, bool local)
    {
        float speedKmh = CatchSpeedKmh(speedMetersPerSecond);
        PlayCatchSound(CatchClip(pitch, speedKmh), position, CatchVolume(speedKmh), CatchPitch(speedKmh));
        if (local) PlayCatchHaptics();
    }

    // A speed that is negative, NaN or infinite is treated as 0 km/h.
    public static float CatchSpeedKmh(float speedMetersPerSecond)
    {
        float speedKmh = speedMetersPerSecond * 3.6f;
        return speedKmh >= 0f && speedKmh <= float.MaxValue ? speedKmh : 0f;
    }

    public const int CatchClipNone = 0;
    public const int CatchClipNormal = 1;
    public const int CatchClipFastPitch = 2;

    public AudioClip CatchClip(bool pitch, float speedKmh)
    {
        int choice = CatchClipChoice(pitch, speedKmh, fastPitchCatchClip != null, normalCatchClip != null);
        if (choice == CatchClipFastPitch) return fastPitchCatchClip;
        return choice == CatchClipNormal ? normalCatchClip : null;
    }

    // Only a pitch at or above the threshold uses the fast-pitch clip; everything else, and a
    // missing fast-pitch clip, uses the normal clip; without a normal clip nothing plays.
    public int CatchClipChoice(bool pitch, float speedKmh, bool hasFastPitchClip, bool hasNormalClip)
    {
        if (pitch && hasFastPitchClip && speedKmh >= fastPitchThresholdKmh) return CatchClipFastPitch;
        return hasNormalClip ? CatchClipNormal : CatchClipNone;
    }

    public float CatchVolume(float speedKmh)
    {
        return Mathf.Clamp01(SafeLevel(CatchLevel(speedKmh, catchMinVolume, catchMaxVolume), 0f));
    }

    public float CatchPitch(float speedKmh)
    {
        return Mathf.Clamp(SafeLevel(CatchLevel(speedKmh, catchMinPitch, catchMaxPitch), 1f), MinCatchPitch, MaxCatchPitch);
    }

    private float CatchLevel(float speedKmh, float low, float high)
    {
        return Mathf.Lerp(low, high, Mathf.InverseLerp(catchSlowSpeedKmh, catchFastSpeedKmh, speedKmh));
    }

    private float SafeLevel(float value, float fallback)
    {
        return FieldingMath.Finite(value) ? value : fallback;
    }

    private void PlayCatchSound(AudioClip clip, Vector3 position, float volume, float pitch)
    {
        if (clip == null || !FieldingMath.Finite(position.x) || !FieldingMath.Finite(position.y) ||
            !FieldingMath.Finite(position.z)) return;
        AudioSource source = IdleCatchAudio();
        if (source == null) return;
        source.transform.position = position;
        source.clip = clip;
        source.volume = volume;
        source.pitch = pitch;
        source.Play();
        catchSoundsPlayed++;
    }

    // The next idle source in turn; none when every source is still playing.
    private AudioSource IdleCatchAudio()
    {
        if (catchAudioSources == null) return null;
        int count = catchAudioSources.Length;
        for (int i = 0; i < count; i++)
        {
            int index = (_nextCatchAudio + i) % count;
            AudioSource source = catchAudioSources[index];
            if (source == null || source.isPlaying) continue;
            _nextCatchAudio = (index + 1) % count;
            return source;
        }
        return null;
    }

    // The glove is worn on the hand opposite the throwing hand.
    public static VRC_Pickup.PickupHand GloveHapticHand(int throwingHand)
    {
        return throwingHand == HandLeft ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left;
    }

    private void PlayCatchHaptics()
    {
        if (!catchHaptics || !_isLocalOwner) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player) || !player.IsUserInVR()) return;
        player.PlayHapticEventInHand(GloveHapticHand(throwHand),
            Mathf.Clamp(SafeLevel(catchHapticDuration, 0f), 0f, MaxCatchHapticDuration),
            Mathf.Clamp01(SafeLevel(catchHapticAmplitude, 0f)),
            Mathf.Clamp(SafeLevel(catchHapticFrequency, 0f), 0f, MaxCatchHapticFrequency));
        catchHapticsPlayed++;
    }
}
