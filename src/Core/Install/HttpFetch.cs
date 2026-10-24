using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OverTheHillPlus.Core.Install;

/// <summary>
/// The installer's downloads: HTTPS only, 20 s timeout, "User-Agent: OTH-ModManager/&lt;version&gt;" and nothing else
/// about the player. Redirects are followed by hand, at most <see cref="DownloadHosts.MaxRedirects"/>, and only to
/// GitHub's HTTPS hosts (<see cref="DownloadHosts.IsAllowed"/>): release assets live on GitHub's download hosts.
/// Runs on a worker thread; results go back to the game through <see cref="MainThread"/>.
/// </summary>
internal static class HttpFetch
{
    private const long MaxBytes = 64L * 1024 * 1024;

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        // No automatic redirects: each hop is checked against DownloadHosts before it is followed.
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = MaxBytes };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OTH-ModManager/" + CorePlugin.Version);
        return c;
    });

    /// <summary>The body of an HTTPS GET. Called from <see cref="Background.Run{T}"/> work, so never on the main thread.</summary>
    public static async Task<byte[]> Get(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !DownloadHosts.IsAllowed(uri))
            throw new InvalidOperationException("Only HTTPS downloads from GitHub are allowed.");
        for (int hops = 0; ; hops++)
        {
            using var response = await Client.Value.GetAsync(uri).ConfigureAwait(false);
            if (DownloadHosts.IsRedirect((int)response.StatusCode))
            {
                uri = DownloadHosts.NextHop(uri, response.Headers.Location, hops);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A short reason for the page: "timed out", "GitHub answered 404", the network error.</summary>
    public static string Describe(Exception e)
    {
        while (e is AggregateException { InnerException: not null }) e = e.InnerException;
        if (e is TaskCanceledException || e is TimeoutException) return "the connection timed out (20 s)";
        if (e is HttpRequestException h && h.StatusCode != null) return $"GitHub answered {(int)h.StatusCode.Value} ({h.StatusCode.Value})";
        return e.Message;
    }
}

/// <summary>
/// Runs a download (or any slow work) on a thread-pool thread and hands its result, or the exception it threw, to
/// the main thread through <see cref="MainThread"/>. (Not Task.Run: its nullable-annotated overloads need a compiler
/// attribute this project doesn't have.)
/// </summary>
internal static class Background
{
    private interface IJob
    {
        void Run();
    }

    private sealed class Job<T> : IJob
    {
        private readonly Func<Task<T>> _work;
        private readonly Action<T, Exception> _done;

        public Job(Func<Task<T>> work, Action<T, Exception> done)
        {
            _work = work;
            _done = done;
        }

        public async void Run()
        {
            T result = default;
            Exception error = null;
            try { result = await _work().ConfigureAwait(false); }
            catch (Exception e) { DebugLog.Error("core", e, "Job.Run"); error = e; }
            MainThread.Post(() => _done(result, error));
        }
    }

    public static void Run<T>(Func<Task<T>> work, Action<T, Exception> done) =>
        ThreadPool.QueueUserWorkItem(Start, new Job<T>(work, done));

    private static void Start(object job) => ((IJob)job).Run();
}

/// <summary>Work handed back to the game's main thread (UI state, Unity calls); CoreBehaviour.Update drains it.</summary>
internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();

    public static void Post(Action a)
    {
        if (a != null) Queue.Enqueue(a);
    }

    /// <summary>Runs everything posted so far (each in its own try/catch). Main thread only.</summary>
    public static void Drain()
    {
        for (int i = 0; i < 64 && Queue.TryDequeue(out var a); i++)
        {
            try { a(); }
            catch (Exception e) { CorePlugin.Logger?.LogError("[Core] " + e); }
        }
    }
}
