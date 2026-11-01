using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// Generates procedural audio as real .wav files (standard importer path —
/// procedural AudioClip .asset files are unreliable in VRChat world bundles).
/// Menu: HordeShooter/Build Audio Clips
/// </summary>
public static class HordeAudioBuilder
{
    [MenuItem("HordeShooter/Build Audio Clips")]
    public static void BuildAll()
    {
        EnsureFolder("Assets/HordeShooter", "Audio");
        WriteWav("GunShot", 0.16f, 44100, t =>
        {
            float env = Mathf.Exp(-t * 38f);
            float noise = (Random.value * 2f - 1f);
            float boom = Mathf.Sin(2f * Mathf.PI * (90f - t * 220f) * t);
            return Mathf.Clamp((noise * 0.7f + boom * 0.8f) * env, -1f, 1f) * 0.8f;
        });
        WriteWav("WaveHorn", 1.1f, 44100, t =>
        {
            float f = 98f + t * 40f;
            float env = Mathf.SmoothStep(0f, 1f, Mathf.Min(t * 8f, 1f)) * Mathf.Exp(-Mathf.Max(0f, t - 0.7f) * 6f);
            float s = Mathf.Sin(2f * Mathf.PI * f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * f * 2f * t) * 0.25f;
            return s * env * 0.7f;
        });
        WriteWav("HordeAmbient", 2.0f, 22050, t =>
        {
            float s = Mathf.Sin(2f * Mathf.PI * 55f * t) * 0.4f
                    + Mathf.Sin(2f * Mathf.PI * 83f * t + Mathf.Sin(2f * Mathf.PI * 1.5f * t)) * 0.3f
                    + Mathf.Sin(2f * Mathf.PI * 41f * t) * 0.25f;
            float wobble = 0.8f + 0.2f * Mathf.Sin(2f * Mathf.PI * 0.5f * t);
            return s * wobble * 0.35f;
        }, loopable: true);
        WriteWav("HitMarker", 0.06f, 44100, t =>
        {
            float env = Mathf.Exp(-t * 90f);
            return Mathf.Sin(2f * Mathf.PI * 1400f * t) * env * 0.5f;
        });

        // remove the old procedural .asset clips (superseded by .wav)
        foreach (var n in new string[] { "GunShot", "WaveHorn", "HordeAmbient", "HitMarker" })
            AssetDatabase.DeleteAsset($"Assets/HordeShooter/Audio/{n}.asset");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[HordeAudioBuilder] .wav clips written to Assets/HordeShooter/Audio");
    }

    private static void WriteWav(string name, float seconds, int rate, System.Func<float, float> gen, bool loopable = false)
    {
        string path = $"Assets/HordeShooter/Audio/{name}.wav";
        int n = (int)(seconds * rate);
        float[] data = new float[n];
        for (int i = 0; i < n; i++) data[i] = gen(i / (float)rate);
        if (loopable)
        {
            int fade = rate / 4;
            for (int i = 0; i < fade; i++)
            {
                float k = (float)i / fade;
                data[i] = data[i] * k + data[n - fade + i] * (1f - k);
            }
        }
        // 16-bit PCM mono WAV
        byte[] bytes = new byte[44 + n * 2];
        WriteAscii(bytes, 0, "RIFF");
        WriteInt(bytes, 4, 36 + n * 2);
        WriteAscii(bytes, 8, "WAVE");
        WriteAscii(bytes, 12, "fmt ");
        WriteInt(bytes, 16, 16);
        WriteShort(bytes, 20, 1);          // PCM
        WriteShort(bytes, 22, 1);          // mono
        WriteInt(bytes, 24, rate);
        WriteInt(bytes, 28, rate * 2);     // byte rate
        WriteShort(bytes, 32, 2);          // block align
        WriteShort(bytes, 34, 16);         // bits per sample
        WriteAscii(bytes, 36, "data");
        WriteInt(bytes, 40, n * 2);
        for (int i = 0; i < n; i++)
        {
            short v = (short)Mathf.Clamp(data[i] * 32767f, -32768f, 32767f);
            bytes[44 + i * 2] = (byte)(v & 0xFF);
            bytes[44 + i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        File.WriteAllBytes(path, bytes);
        AssetDatabase.ImportAsset(path);
    }

    private static void WriteAscii(byte[] b, int at, string s)
    {
        for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i];
    }

    private static void WriteInt(byte[] b, int at, int v)
    {
        b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
    }

    private static void WriteShort(byte[] b, int at, short v)
    {
        b[at] = (byte)v; b[at + 1] = (byte)(v >> 8);
    }

    private static void EnsureFolder(string parent, string folder)
    {
        if (!AssetDatabase.IsValidFolder($"{parent}/{folder}"))
            AssetDatabase.CreateFolder(parent, folder);
    }
}
