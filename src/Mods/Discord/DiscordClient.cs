using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace OverTheHillPlus.Discord;

/// <summary>
/// The Discord IPC connection, on its own background thread (never the game's main thread): finds Discord's pipe
/// \\.\pipe\discord-ipc-0..9, says hello (handshake, op 0), waits for READY, then sends SET_ACTIVITY (op 1) whenever
/// what should be shown changes (at most every 4 s; Discord allows 5 updates per 20 s). Pings are answered. When
/// Discord is not running or the pipe breaks it retries with a backoff (5 s up to 60 s). Failures are silent: one log
/// line the first time, nothing on later retries.
/// </summary>
internal static class DiscordClient
{
    private const int ConnectTimeoutMs = 250, ReadyTimeoutMs = 5000, MinSendGapMs = 4000;

    private static readonly object Gate = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static Thread _thread;
    private static volatile bool _stop;

    // Written on the main thread, read on the client thread (under Gate).
    private static PresenceState _state;
    private static PresenceOptions _options;
    private static bool _wanted;
    private static string _clientId = "";

    private static bool _loggedFailure, _loggedConnected, _loggedError;
    private static bool _reachedReady; // client thread only
    private static volatile string _statusText = "starting";

    /// <summary>The status line; every change goes to the debug log (from the presence thread, which DebugLog allows).</summary>
    private static string _status
    {
        get => _statusText;
        set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal)) return;
            _statusText = value;
            DebugLog.Info(DiscordPlugin.ModId, "presence status: " + value);
        }
    }

    /// <summary>One line for the mod page.</summary>
    public static string Status => _status;

    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "OTH Discord presence" };
        _thread.Start();
    }

    /// <summary>Main thread: what to show (copied), whether to show anything, and the application id.</summary>
    public static void Publish(PresenceState state, PresenceOptions options, bool wanted, string clientId)
    {
        lock (Gate)
        {
            bool idChanged = !string.Equals(_clientId, clientId ?? "", StringComparison.Ordinal);
            _state = state;
            _options = options;
            _wanted = wanted;
            _clientId = clientId ?? "";
            if (idChanged) Wake.Set();
        }
        Wake.Set();
    }

    /// <summary>Retry now instead of waiting for the backoff (the mod page's button).</summary>
    public static void RetryNow() => Wake.Set();

    /// <summary>Game closing: stop the thread (Discord drops the presence when the pipe closes).</summary>
    public static void Stop()
    {
        _stop = true;
        Wake.Set();
        try { _thread?.Join(500); } catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordClient.Stop"); }
    }

    private static void Snapshot(out PresenceState state, out PresenceOptions options, out bool wanted, out string id)
    {
        lock (Gate)
        {
            state = _state;
            options = _options;
            wanted = _wanted;
            id = _clientId;
        }
    }

    private static void Run()
    {
        // An exception escaping a thread ends the game: everything stays inside this catch.
        try { Loop(); }
        catch (Exception e)
        {
            DebugLog.Error("discord", e, "DiscordClient.Run");
            _status = "stopped after an error";
            try { DiscordPlugin.Logger?.LogInfo("[Discord] Presence stopped: " + e.Message); } catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordClient.Run"); }
        }
    }

    private static void Loop()
    {
        int failures = 0;
        while (!_stop)
        {
            Snapshot(out _, out _, out bool wanted, out string id);
            if (!wanted)
            {
                _status = "off";
                Wake.WaitOne(2000);
                continue;
            }
            if (!DiscordPresence.ValidClientId(id))
            {
                _status = "the Discord application id is not valid (17-20 digits)";
                Wake.WaitOne(2000);
                continue;
            }
            string error;
            try
            {
                _reachedReady = false;
                error = Session(id);
                // A clean end (switched off, id changed, game closing), or a connection that got as far as READY:
                // the next failure starts the backoff again from 5 s.
                if (error == null || _reachedReady) failures = 0;
            }
            catch (Exception e) { DebugLog.Error("discord", e, "DiscordClient.Loop"); error = e.Message; }
            if (_stop) break;
            if (error == null) continue;
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                DiscordPlugin.Logger?.LogInfo($"[Discord] Presence not shown ({error}). It retries quietly while the game runs.");
            }
            int wait = DiscordPresence.BackoffSeconds(failures++);
            _status = $"waiting for Discord ({error}); next try in {wait} s";
            Wake.WaitOne(wait * 1000);
        }
        _status = "stopped";
    }

    /// <summary>One connection, until it breaks (returns why) or ends cleanly (returns null).</summary>
    private static string Session(string id)
    {
        using var conn = Connection.Open();
        if (conn == null) return "Discord is not running";
        conn.Write(DiscordFrame.OpHandshake, DiscordPresence.HandshakeJson(id));
        if (!conn.Ready.Wait(ReadyTimeoutMs) || conn.Broken) return conn.Error ?? "Discord did not answer";
        if (!_loggedConnected)
        {
            _loggedConnected = true;
            DiscordPlugin.Logger?.LogInfo("[Discord] Connected to Discord.");
        }
        _status = "connected";
        _reachedReady = true;
        int pid = Environment.ProcessId;
        string sentKey = null;
        long lastSend = -MinSendGapMs;
        long nonce = 0;
        while (!_stop && !conn.Broken)
        {
            Snapshot(out var state, out var options, out bool wanted, out string nowId);
            if (!string.Equals(nowId, id, StringComparison.Ordinal)) return null; // reconnect with the new id
            if (!wanted)
            {
                if (sentKey != null) conn.Write(DiscordFrame.OpFrame, DiscordPresence.ClearActivityJson(pid, (++nonce).ToString()));
                return null;
            }
            if (state != null && options != null)
            {
                string key = DiscordPresence.ActivityKey(state, options);
                long now = Environment.TickCount64;
                if (key != sentKey && now - lastSend >= MinSendGapMs)
                {
                    conn.Write(DiscordFrame.OpFrame, DiscordPresence.SetActivityJson(pid, state, options, (++nonce).ToString()));
                    sentKey = key;
                    lastSend = now;
                    _status = "connected: showing your game";
                }
            }
            Wake.WaitOne(1000);
        }
        if (_stop && sentKey != null)
        {
            try { conn.Write(DiscordFrame.OpFrame, DiscordPresence.ClearActivityJson(pid, (++nonce).ToString())); } catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordClient.Session"); }
        }
        return conn.Broken ? conn.Error ?? "Discord closed the connection" : null;
    }

    internal static void NoteError(string message)
    {
        if (_loggedError) return;
        _loggedError = true;
        DiscordPlugin.Logger?.LogInfo("[Discord] Discord refused the presence: " + message);
    }

    /// <summary>The pipe plus a reader thread (pings, READY, errors). Overlapped I/O so reads and writes can overlap.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly object _writeLock = new();
        private readonly Thread _reader;
        public readonly ManualResetEventSlim Ready = new(false);
        public volatile bool Broken;
        public volatile string Error;

        private Connection(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "OTH Discord reader" };
            _reader.Start();
        }

        public static Connection Open()
        {
            for (int i = 0; i < 10; i++)
            {
                if (_stop) return null;
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(ConnectTimeoutMs);
                    return new Connection(pipe);
                }
                catch (Exception ex)
                {
                    DebugLog.Error("discord", ex, "Connection.Open");
                    try { pipe?.Dispose(); } catch (Exception ex2) { DebugLog.Error("discord", ex2, "Connection.Open"); }
                }
            }
            return null;
        }

        public void Write(int op, string json)
        {
            byte[] frame = DiscordFrame.Encode(op, json);
            lock (_writeLock)
            {
                _pipe.Write(frame, 0, frame.Length);
                _pipe.Flush();
            }
        }

        private void ReadLoop()
        {
            var header = new byte[DiscordFrame.HeaderSize];
            try
            {
                while (!Broken)
                {
                    if (!ReadExactly(header, header.Length)) { Fail("Discord closed the connection"); return; }
                    if (!DiscordFrame.TryReadHeader(header, out int op, out int length)) { Fail("unexpected data from Discord"); return; }
                    var body = new byte[length];
                    if (!ReadExactly(body, length)) { Fail("Discord closed the connection"); return; }
                    string json = System.Text.Encoding.UTF8.GetString(body);
                    switch (op)
                    {
                        case DiscordFrame.OpPing:
                            Write(DiscordFrame.OpPong, json);
                            break;
                        case DiscordFrame.OpClose:
                            Fail("Discord closed the connection: " + Short(json));
                            return;
                        case DiscordFrame.OpFrame:
                            var kind = DiscordPresence.Classify(json);
                            if (kind == DiscordReply.Ready) Ready.Set();
                            else if (kind == DiscordReply.Error) NoteError(Short(json));
                            break;
                    }
                }
            }
            catch (Exception e) { DebugLog.Error("discord", e, "Connection.ReadLoop"); Fail(e.Message); }
        }

        private bool ReadExactly(byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _pipe.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private void Fail(string why)
        {
            // Runs on the reader thread: nothing here may throw (an exception escaping a thread ends the game).
            try
            {
                Error ??= why;
                Broken = true;
                Ready.Set(); // wake a handshake wait
                Wake.Set();
            }
            catch (Exception ex) { DebugLog.Error("discord", ex, "Connection.Fail"); }
        }

        private static string Short(string s) => s == null ? "" : s.Length <= 200 ? s : s.Substring(0, 200);

        public void Dispose()
        {
            Broken = true;
            try { _pipe.Dispose(); } catch (Exception ex) { DebugLog.Error("discord", ex, "Connection.Dispose"); }
            try { _reader.Join(500); } catch (Exception ex) { DebugLog.Error("discord", ex, "Connection.Dispose"); }
            // Ready is not disposed: a reader thread that outlives the join may still set it.
        }
    }
}
