using UnityEngine;

/// Bus horn (procedural two-tone, no clip needed). Hold Horn (H / gamepad north) to honk. Auto-spawns,
/// 2D audio. Swap in a recorded horn later by replacing the synth in OnAudioFilterRead if you like.
public class BusAudio : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<BusAudio>() == null)
            new GameObject("BusAudio").AddComponent<BusAudio>();
    }

    [Range(0f, 1f)] public float hornVolume = 0.32f;

    int _sampleRate;
    double _hornA, _hornB;
    float _env;                 // smooth on/off so it doesn't click
    volatile bool _hornOn;

    void Start()
    {
        _sampleRate = AudioSettings.outputSampleRate;
        if (_sampleRate <= 0) _sampleRate = 44100;

        AudioSource src = gameObject.AddComponent<AudioSource>();
        src.clip = AudioClip.Create("busHornLoop", _sampleRate, 1, _sampleRate, false); // silent carrier
        src.loop = true;
        src.spatialBlend = 0f;
        src.playOnAwake = false;
        src.Play();
    }

    void Update()
    {
        GameInput gi = GameInput.Instance;
        _hornOn = gi != null && gi.horn != null && gi.horn.IsPressed();
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        float sr = _sampleRate <= 0 ? 44100f : _sampleRate;
        float target = _hornOn ? 1f : 0f;

        for (int i = 0; i < data.Length; i += channels)
        {
            _env = Mathf.Lerp(_env, target, 0.002f);   // click-free attack/release

            float sample = 0f;
            if (_env > 0.0001f)
            {
                _hornA += 370.0 / sr; if (_hornA >= 1.0) _hornA -= 1.0;
                _hornB += 466.0 / sr; if (_hornB >= 1.0) _hornB -= 1.0;
                float horn = (Mathf.Sin((float)(_hornA * 2.0 * Mathf.PI)) + Mathf.Sin((float)(_hornB * 2.0 * Mathf.PI))) * 0.5f;
                sample = horn * hornVolume * _env;
            }

            sample = Mathf.Clamp(sample, -1f, 1f);
            for (int c = 0; c < channels; c++) data[i + c] = sample;
        }
    }
}
