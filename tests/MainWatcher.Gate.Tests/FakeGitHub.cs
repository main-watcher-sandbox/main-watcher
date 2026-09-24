using System.Net;
using System.Text;
using System.Text.Json;

namespace MainWatcher.Gate.Tests;

/// <summary>
/// Serves canned GitHub API responses by path and query. A request with no response set up fails
/// the test, so every call the gate makes is one the test expects.
/// </summary>
sealed class FakeGitHub : HttpMessageHandler
{
    public const string Repo = "sandbox/target";
    public static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    readonly Dictionary<string, Func<HttpResponseMessage>> _responses = [];

    public List<string> Requests { get; } = [];
    public List<TimeSpan> Delays { get; } = [];

    public FakeGitHub Respond(string pathAndQuery, object body) =>
        Respond(pathAndQuery, () => Json(HttpStatusCode.OK, body));

    public FakeGitHub Respond(string pathAndQuery, Func<HttpResponseMessage> response)
    {
        _responses[pathAndQuery] = response;
        return this;
    }

    public FakeGitHub LockIssues(params object[] issues) =>
        Respond($"/repos/{Repo}/issues?state=open&labels=main-broken&per_page=100", issues);

    public FakeGitHub Compare(string compareBase, string headSha, params object[] commits) =>
        Respond($"/repos/{Repo}/compare/{compareBase}...{headSha}?per_page=100&page=1", new { total_commits = commits.Length, commits });

    public FakeGitHub CommitPulls(string sha, params object[] pulls) =>
        Respond($"/repos/{Repo}/commits/{sha}/pulls?per_page=100", pulls);

    public Gate Gate() =>
        new(new GitHubApi(new HttpClient(this) { BaseAddress = new Uri("https://api.github.test/") },
            (delay, _) => { Delays.Add(delay); return Task.CompletedTask; }), Repo);

    public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.PathAndQuery;
        Requests.Add(key);
        return _responses.TryGetValue(key, out var response)
            ? Task.FromResult(response())
            : throw new InvalidOperationException($"Unexpected request: GET {key}");
    }

    // Fixtures, shaped like the GitHub REST API responses the gate reads.

    public static string Sha(char c) => new(c, 40);

    public static object Issue(int number, string body, string login = "actium-main-watcher[bot]", string type = "Bot") =>
        new { number, html_url = $"https://github.test/{Repo}/issues/{number}", body, user = new { login, type } };

    public static object Lock(int number, DateTimeOffset leaseUntil) =>
        Issue(number, $"main is broken\n\n<!-- main-watcher last_green={Sha('0')} lease_until={leaseUntil:yyyy-MM-ddTHH:mm:ssZ} -->");

    public static object Commit(string sha, string message = "Some change") =>
        new { sha, commit = new { message } };

    public static object Pull(int number, params string[] labels) =>
        new
        {
            number,
            title = $"PR {number}",
            state = "open",
            @base = new { @ref = "main" },
            labels = labels.Select(name => new { name }).ToArray(),
        };

    public static GateEvent MergeGroup(int headPr = 2) =>
        new("merge_group", Sha('a'), Sha('f'), $"gh-readonly-queue/main/pr-{headPr}-{Sha('a')}", "refs/heads/main");
}
