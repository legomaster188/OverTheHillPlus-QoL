using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OverTheHillPlus.Audio;

/// <summary>
/// A thin door into the game's FMOD for playing our own sound files. The game turns Unity's audio off (it uses
/// FMOD only), so an AudioSource would stay silent; FMOD's core API reads mp3, ogg and wav itself.
///
/// <para>The functions come from the fmodstudio library the game has already loaded (looked up by name, never
/// loaded from anywhere else), and the core system is the game's own (FMODUnity's RuntimeManager). Our channels play
/// on FMOD's master output, next to the game's sounds, so the callers apply their own volume (optionally scaled by
/// the game's volume settings, see <see cref="GameVolume"/>). Every call is checked: a missing export, a stopped
/// channel or a bad file is a false / zero, never an exception. Main thread only.</para>
///
/// <para>Used by Radio and Horns+. Nothing is latched before the game's FMOD is up: until then every call is a
/// cheap "not ready", and the lookup is tried again (at most twice a second).</para>
/// </summary>
internal static class FmodCore
{
    // FMOD_MODE
    public const uint LoopOff = 0x1, LoopNormal = 0x2, Mode2D = 0x8, CreateStream = 0x80, CreateSample = 0x100,
        NonBlocking = 0x10000, IgnoreTags = 0x2000000;

    // FMOD_OPENSTATE
    public const int OpenReady = 0, OpenLoading = 1, OpenError = 2, OpenConnecting = 3, OpenBuffering = 4;

    private const uint TimeUnitMs = 1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CreateSoundFn(IntPtr system, byte[] name, uint mode, IntPtr exinfo, out IntPtr sound);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int PlaySoundFn(IntPtr system, IntPtr sound, IntPtr group, int paused, out IntPtr channel);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int HandleFn(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int OpenStateFn(IntPtr sound, out int state, out uint percent, out int starving, out int diskbusy);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetUIntFn(IntPtr handle, out uint value, uint unit);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetBoolFn(IntPtr handle, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetBoolFn(IntPtr handle, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetFloatFn(IntPtr handle, float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetUIntFn(IntPtr handle, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int LookupFn(IntPtr system, byte[] path, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetVolumeFn(IntPtr handle, out float volume, out float final);

    private static CreateSoundFn _createSound;
    private static PlaySoundFn _playSound;
    private static HandleFn _soundRelease, _channelStop;
    private static OpenStateFn _openState;
    private static GetUIntFn _soundLength, _channelPosition;
    private static GetBoolFn _isPlaying, _busMute;
    private static SetBoolFn _setPaused;
    private static SetFloatFn _setVolume, _setPan;
    private static SetUIntFn _setMode;
    private static LookupFn _getVca, _getBus;
    private static GetVolumeFn _vcaVolume, _busVolume;

    private static bool _bound, _bindFailed;
    private static float _nextBindTry;
    private static IntPtr _core, _studio;
    private static float _nextCoreTry;
    private static readonly Dictionary<string, IntPtr> Mixers = new();

    /// <summary>Why FMOD can't be used (null while it can, or before the first try).</summary>
    public static string Problem { get; private set; }

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string name);

    /// <summary>The game's FMOD runs and the functions are found (true from then on).</summary>
    public static bool Ready => _bound || (CoreSystem != IntPtr.Zero && Bind());

    /// <summary>
    /// Looks the functions up once FMOD runs. A library that isn't loaded yet is tried again later; only a library
    /// that is loaded but lacks a function turns this off for the session.
    /// </summary>
    private static bool Bind()
    {
        if (_bound) return true;
        if (_bindFailed) return false;
        float now = UnityEngine.Time.unscaledTime;
        if (now < _nextBindTry) return false;
        _nextBindTry = now + 0.5f;
        try
        {
            IntPtr lib = GetModuleHandleW("fmodstudio.dll");
            if (lib == IntPtr.Zero) { Problem = "the game's FMOD library is not loaded yet"; return false; }
            T Get<T>(string name) where T : Delegate =>
                NativeLibrary.TryGetExport(lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p)
                    : throw new EntryPointNotFoundException(name);
            _createSound = Get<CreateSoundFn>("FMOD_System_CreateSound");
            _playSound = Get<PlaySoundFn>("FMOD_System_PlaySound");
            _soundRelease = Get<HandleFn>("FMOD_Sound_Release");
            _openState = Get<OpenStateFn>("FMOD_Sound_GetOpenState");
            _soundLength = Get<GetUIntFn>("FMOD_Sound_GetLength");
            _channelStop = Get<HandleFn>("FMOD_Channel_Stop");
            _channelPosition = Get<GetUIntFn>("FMOD_Channel_GetPosition");
            _isPlaying = Get<GetBoolFn>("FMOD_Channel_IsPlaying");
            _setPaused = Get<SetBoolFn>("FMOD_Channel_SetPaused");
            _setVolume = Get<SetFloatFn>("FMOD_Channel_SetVolume");
            _setPan = Get<SetFloatFn>("FMOD_Channel_SetPan");
            _setMode = Get<SetUIntFn>("FMOD_Channel_SetMode");
            _getVca = Get<LookupFn>("FMOD_Studio_System_GetVCA");
            _vcaVolume = Get<GetVolumeFn>("FMOD_Studio_VCA_GetVolume");
            _getBus = Get<LookupFn>("FMOD_Studio_System_GetBus");
            _busVolume = Get<GetVolumeFn>("FMOD_Studio_Bus_GetVolume");
            _busMute = Get<GetBoolFn>("FMOD_Studio_Bus_GetMute");
            _bound = true;
            Problem = null;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "FmodCore.Bind");
            _bindFailed = true;
            Problem = "the game's FMOD library is missing a function (" + e.Message + ")";
        }
        return _bound;
    }

    /// <summary>The game's FMOD core system (zero until FMOD has started).</summary>
    public static IntPtr CoreSystem
    {
        get
        {
            if (_core != IntPtr.Zero) return _core;
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextCoreTry) return IntPtr.Zero;
            _nextCoreTry = now + 0.5f;
            try
            {
                // Checked first: asking for CoreSystem before FMOD started would make FMODUnity start it.
                if (!FMODUnity.RuntimeManager.IsInitialized) { Problem = "the game's audio hasn't started yet"; return IntPtr.Zero; }
                var core = FMODUnity.RuntimeManager.CoreSystem.handle;
                var studio = FMODUnity.RuntimeManager.StudioSystem.handle;
                if (core == IntPtr.Zero) return IntPtr.Zero;
                _studio = studio;
                _core = core;
            }
            catch (Exception e) { DebugLog.Error("core", e, "FmodCore.CoreSystem"); Problem = "the game's FMOD is not reachable (" + e.Message + ")"; }
            return _core;
        }
    }

    /// <summary>The game is muted (FMODUnity mutes everything while the window is in the background, if set to).</summary>
    public static bool GameMuted
    {
        get
        {
            try { return FMODUnity.RuntimeManager.IsMuted; } catch (Exception ex) { DebugLog.Error("core", ex, "FmodCore.GameMuted"); return false; }
        }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Opens a sound file. Zero on failure (<paramref name="result"/> is FMOD's error code).</summary>
    public static IntPtr CreateSound(string path, uint mode, out int result)
    {
        result = -1;
        if (!Ready || string.IsNullOrEmpty(path)) return IntPtr.Zero;
        result = _createSound(_core, Utf8(path), mode, IntPtr.Zero, out var sound);
        return result == 0 ? sound : IntPtr.Zero;
    }

    /// <summary>FMOD_OPENSTATE of a sound opened with <see cref="NonBlocking"/> (<see cref="OpenError"/> on failure).</summary>
    public static int OpenState(IntPtr sound)
    {
        if (!_bound || sound == IntPtr.Zero) return OpenError;
        return _openState(sound, out int state, out _, out _, out _) == 0 ? state : OpenError;
    }

    public static void Release(IntPtr sound)
    {
        if (_bound && sound != IntPtr.Zero) _soundRelease(sound);
    }

    /// <summary>Starts a sound on a new channel, paused when asked (set volume and pan first, then unpause).</summary>
    public static IntPtr Play(IntPtr sound, bool paused)
    {
        if (!Ready || sound == IntPtr.Zero) return IntPtr.Zero;
        return _playSound(_core, sound, IntPtr.Zero, paused ? 1 : 0, out var ch) == 0 ? ch : IntPtr.Zero;
    }

    /// <summary>False for a stopped, finished or stolen channel.</summary>
    public static bool IsPlaying(IntPtr channel) =>
        _bound && channel != IntPtr.Zero && _isPlaying(channel, out int p) == 0 && p != 0;

    public static void Stop(IntPtr channel)
    {
        if (_bound && channel != IntPtr.Zero) _channelStop(channel);
    }

    public static void SetPaused(IntPtr channel, bool paused)
    {
        if (_bound && channel != IntPtr.Zero) _setPaused(channel, paused ? 1 : 0);
    }

    public static void SetVolume(IntPtr channel, float volume)
    {
        if (_bound && channel != IntPtr.Zero) _setVolume(channel, Math.Clamp(float.IsFinite(volume) ? volume : 0f, 0f, 2f));
    }

    /// <summary>-1 = left, 0 = centre, 1 = right.</summary>
    public static void SetPan(IntPtr channel, float pan)
    {
        if (_bound && channel != IntPtr.Zero) _setPan(channel, Math.Clamp(float.IsFinite(pan) ? pan : 0f, -1f, 1f));
    }

    /// <summary>Loop on or off for this channel (<see cref="LoopNormal"/> / <see cref="LoopOff"/>).</summary>
    public static void SetLoop(IntPtr channel, bool loop)
    {
        if (_bound && channel != IntPtr.Zero) _setMode(channel, loop ? LoopNormal : LoopOff);
    }

    public static uint LengthMs(IntPtr sound) => _bound && sound != IntPtr.Zero && _soundLength(sound, out uint v, TimeUnitMs) == 0 ? v : 0;

    public static uint PositionMs(IntPtr channel) => _bound && channel != IntPtr.Zero && _channelPosition(channel, out uint v, TimeUnitMs) == 0 ? v : 0;

    /// <summary>
    /// The volume the player set for a part of the game's mix: a VCA ("vca:/SFX", "vca:/Music") or a bus ("bus:/"
    /// is the master). 1 when it can't be read; 0 for a muted bus.
    /// </summary>
    public static float GameVolume(string path)
    {
        if (!Ready || _studio == IntPtr.Zero || string.IsNullOrEmpty(path)) return 1f;
        bool vca = path.StartsWith("vca:", StringComparison.Ordinal);
        if (!Mixers.TryGetValue(path, out var h))
        {
            if ((vca ? _getVca(_studio, Utf8(path), out h) : _getBus(_studio, Utf8(path), out h)) != 0) h = IntPtr.Zero;
            Mixers[path] = h;
        }
        if (h == IntPtr.Zero) return 1f;
        if (!vca && _busMute(h, out int muted) == 0 && muted != 0) return 0f;
        int r = vca ? _vcaVolume(h, out float v, out _) : _busVolume(h, out v, out _);
        return r == 0 && float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 1f;
    }
}
