using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Audio;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Horns;

/// <summary>
/// Plays the chosen horns. Every car's horn button reaches every player through the game itself
/// (CarAudioManager.wasHonkPressedLastFrame), so for each car whose owner picked a horn with this mod (you, or a
/// player whose choice came over the mod network) this plays that horn while the car honks and silences the game's
/// own horn sound for it. The sound sits at the car: louder the closer it is, panned left / right from the camera.
/// Cars of players without the mod (or with the stock horn) keep the game's horn.
/// </summary>
internal static class HornPlayer
{
    private sealed class Car
    {
        public CarAudioManager Audio;
        public int Owner;
        public bool Local;
        public bool WasHonking;
        public IntPtr Channel;
        public HornMode Mode;
        public IntPtr Sound;
        /// <summary>FMOD gave no channel last time: the game's horn plays for this car until then.</summary>
        public float NoChannelUntil;
    }

    private static readonly Dictionary<IntPtr, Car> Cars = new();
    /// <summary>CarAudioManager pointers whose stock horn we replace (read by the StartHorn patch).</summary>
    private static readonly HashSet<IntPtr> Replaced = new();
    private static float _nextScan, _nextVolume;
    private static float _gameVolume = 1f;
    private static Camera _camera;
    private static IntPtr _preview;
    private static float _previewStopAt;

    public static bool ShouldMute(IntPtr carAudio) => Replaced.Contains(carAudio);

    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (_preview != IntPtr.Zero && now >= _previewStopAt) { FmodCore.Stop(_preview); _preview = IntPtr.Zero; }
        if (!FmodCore.Ready || !HornNet.Active)
        {
            StopAll();
            return;
        }
        if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
        if (now >= _nextVolume)
        {
            _nextVolume = now + 0.5f;
            _gameVolume = HornsConfig.FollowGameVolume.Value ? FmodCore.GameVolume("bus:/") * FmodCore.GameVolume("vca:/SFX") : 1f;
            if (_camera == null) _camera = Camera.main;
        }

        Replaced.Clear();
        foreach (var kv in Cars)
        {
            var car = kv.Value;
            bool honking;
            try
            {
                if (car.Audio == null) continue;
                honking = car.Audio.wasHonkPressedLastFrame;
            }
            catch (Exception ex) { DebugLog.Error("horns", ex, "HornPlayer.Tick"); continue; }

            var sound = Resolve(car, out HornMode mode);
            if (sound != IntPtr.Zero && now < car.NoChannelUntil) sound = IntPtr.Zero; // ours failed just now: the game's horn
            if (sound != IntPtr.Zero) Replaced.Add(kv.Key);

            if (honking && !car.WasHonking && sound != IntPtr.Zero && !Start(car, sound, mode))
            {
                // No channel for ours: let the game's horn play, now and for the next few seconds.
                car.NoChannelUntil = now + 5f;
                Replaced.Remove(kv.Key);
                sound = IntPtr.Zero;
            }
            else if (!honking && car.WasHonking && car.Mode == HornMode.Hold) StopCar(car);
            car.WasHonking = honking;

            if (car.Channel == IntPtr.Zero) continue;
            if (!FmodCore.IsPlaying(car.Channel) || (car.Sound != sound && car.Mode == HornMode.Hold)) { StopCar(car); continue; }
            if (sound != IntPtr.Zero) SilenceStock(car);
            Place(car);
        }
    }

    /// <summary>The sound this car's owner picked, or zero for the game's horn.</summary>
    private static IntPtr Resolve(Car car, out HornMode mode)
    {
        mode = HornMode.Hold;
        HornChoice c;
        HornNet.Peer peer = null;
        if (car.Local) c = HornNet.Mine;
        else
        {
            if (!HornsConfig.HearOthers.Value) return IntPtr.Zero;
            peer = HornNet.PeerOf(car.Owner);
            if (peer == null) return IntPtr.Zero;
            c = peer.Choice;
        }
        mode = c.Mode;
        switch (c.Kind)
        {
            case HornKind.BuiltIn:
                var preset = HornSynth.Find(c.BuiltInId);
                if (preset == null) return IntPtr.Zero; // a newer version's horn: the game's horn instead
                mode = preset.Mode;
                return HornSounds.BuiltIn(c.BuiltInId);
            case HornKind.Custom:
                if (car.Local) return HornsFeature.OwnSound;
                if (!HornsConfig.OthersCustomFiles.Value) return IntPtr.Zero;
                string key = peer.SoundKey(c.Hash);
                return HornSounds.Has(key) ? HornSounds.Custom(key, null) : IntPtr.Zero;
            default:
                return IntPtr.Zero;
        }
    }

    /// <summary>Starts our horn on the car; false (and the game's horn is left alone) when FMOD gave no channel.</summary>
    private static bool Start(Car car, IntPtr sound, HornMode mode)
    {
        StopCar(car);
        var ch = FmodCore.Play(sound, paused: true);
        if (ch == IntPtr.Zero) { LogOnce("play", "No FMOD channel for a horn: the game's horn plays instead"); return false; }
        FmodCore.SetLoop(ch, mode == HornMode.Hold);
        car.Channel = ch;
        car.Sound = sound;
        car.Mode = mode;
        Place(car);
        FmodCore.SetPaused(ch, false);
        SilenceStock(car);
        return true;
    }

    /// <summary>Volume from the distance to the camera, pan from which side the car is on.</summary>
    private static void Place(Car car)
    {
        float vol = HornsConfig.Volume.Value * _gameVolume * (FmodCore.GameMuted ? 0f : 1f);
        float pan = 0f;
        try
        {
            var cam = _camera != null ? _camera.transform : null;
            if (cam != null)
            {
                Vector3 d = car.Audio.transform.position - cam.position;
                float dist = d.magnitude;
                float range = Mathf.Max(30f, HornsConfig.Range.Value);
                float near = dist <= 5f ? 1f : 1f / (1f + (dist - 5f) / 15f);
                float edge = Mathf.Clamp01((range - dist) / (range * 0.25f)); // fades out over the last quarter
                vol *= near * edge;
                if (dist > 0.01f) pan = Vector3.Dot(cam.right, d / dist) * Mathf.Clamp01(dist / 8f) * 0.85f;
            }
        }
        catch (Exception ex) { DebugLog.Error("horns", ex, "HornPlayer.Place"); /* car or camera went away mid-frame: keep the last values */ }
        FmodCore.SetVolume(car.Channel, vol);
        FmodCore.SetPan(car.Channel, pan);
    }

    private static void SilenceStock(Car car)
    {
        try
        {
            var e = car.Audio.hornAudioEmitter;
            if (e != null && e.IsPlaying()) e.Stop(false);
        }
        catch (Exception ex) { DebugLog.Error("horns", ex, "HornPlayer.SilenceStock"); }
    }

    private static void StopCar(Car car)
    {
        if (car.Channel != IntPtr.Zero) FmodCore.Stop(car.Channel);
        car.Channel = IntPtr.Zero;
        car.Sound = IntPtr.Zero;
    }

    public static void StopAll()
    {
        foreach (var c in Cars.Values) StopCar(c);
        Replaced.Clear();
    }

    /// <summary>Finds the cars and their owners (once a second).</summary>
    private static void Scan()
    {
        var seen = new HashSet<IntPtr>();
        int local = int.MinValue;
        try { var lp = GameState.LocalPlayer; if (lp.HasValue) local = lp.Value.RawEncoded; } catch (Exception ex) { DebugLog.Error("horns", ex, "HornPlayer.Scan"); }
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CarAudioManager>()))
            {
                var a = o?.TryCast<CarAudioManager>();
                if (a == null) continue;
                int owner;
                try
                {
                    var fp = a.fusionPlayer;
                    if (fp == null || fp.Object == null) continue;
                    owner = fp.Object.InputAuthority.RawEncoded;
                }
                catch (Exception ex) { DebugLog.Error("horns", ex, "HornPlayer.Scan"); continue; }
                seen.Add(a.Pointer);
                if (!Cars.TryGetValue(a.Pointer, out var car)) Cars[a.Pointer] = car = new Car { Audio = a };
                car.Owner = owner;
                car.Local = owner == local;
            }
        }
        catch (Exception e) { DebugLog.Error("horns", e, "HornPlayer.Scan"); LogOnce("scan", "Finding the cars failed: " + e.Message); }
        var gone = new List<IntPtr>();
        foreach (var kv in Cars) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var k in gone) { StopCar(Cars[k]); Cars.Remove(k); }
    }

    /// <summary>Plays a horn to you only, in the middle (the MODS page's "Try it").</summary>
    public static string Preview(IntPtr sound, HornMode mode)
    {
        if (!FmodCore.Ready) return "Failed: the game's audio isn't ready" + (FmodCore.Problem != null ? " (" + FmodCore.Problem + ")" : "");
        if (sound == IntPtr.Zero) return "Failed: that horn could not be loaded (see the log)";
        if (_preview != IntPtr.Zero) FmodCore.Stop(_preview);
        _preview = FmodCore.Play(sound, paused: true);
        if (_preview == IntPtr.Zero) return "Failed: no free audio channel";
        FmodCore.SetLoop(_preview, mode == HornMode.Hold);
        FmodCore.SetVolume(_preview, HornsConfig.Volume.Value * (HornsConfig.FollowGameVolume.Value ? FmodCore.GameVolume("bus:/") * FmodCore.GameVolume("vca:/SFX") : 1f));
        FmodCore.SetPaused(_preview, false);
        _previewStopAt = Time.unscaledTime + (mode == HornMode.Hold ? 1.2f : 10.5f);
        return null;
    }

    private static void LogOnce(string key, string message) => HornsUtil.LogOnce(key, message);
}

/// <summary>
/// The moment the game starts a car's horn sound: when that car plays a horn from this mod instead, the game's sound
/// is stopped straight away (the per-frame check in <see cref="HornPlayer"/> covers it if this patch can't apply).
/// StartHorn runs once per press, not every frame. The game's HornHonked event still fires, so whatever reacts to a
/// horn in the world still does.
/// </summary>
[HarmonyPatch(typeof(CarAudioManager), nameof(CarAudioManager.StartHorn))]
internal static class StartHornPatch
{
    private static void Postfix(CarAudioManager __instance)
    {
        try
        {
            if (__instance == null || !HornPlayer.ShouldMute(__instance.Pointer)) return;
            var e = __instance.hornAudioEmitter;
            if (e != null) e.Stop(false);
        }
        catch (Exception ex) { DebugLog.Error("horns", ex, "StartHornPatch.Postfix"); /* never break the game's horn */ }
    }
}
