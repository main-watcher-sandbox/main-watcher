using System.Net.Http.Headers;
using System.Text.Json;
using MainWatcher.Gate;

// Runs the gate inside a GitHub Actions job (.github/actions/gate). Exit code 1 fails the merge group.
// Writes the fail-open-reason output, which the gate template turns into a gate-fail-open check run.

var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? "";
var eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "";
var apiUrl = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";
var token = Environment.GetEnvironmentVariable("GATE_GITHUB_TOKEN");
var botLogin = Environment.GetEnvironmentVariable("GATE_BOT_LOGIN") is { Length: > 0 } login ? login : Gate.DefaultBotLogin;

// A missing token or repository makes the API calls fail, so the gate fails open (ADR-008).
using var payload = JsonDocument.Parse(eventPath is null ? "{}" : File.ReadAllText(eventPath));
using var http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
if (!string.IsNullOrEmpty(token))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
http.DefaultRequestHeaders.UserAgent.ParseAdd("main-watcher-gate");
http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

var gate = new Gate(new GitHubApi(http, log: Console.WriteLine), repository, botLogin);
var verdict = await gate.DecideAsync(GateEvent.Read(eventName, payload.RootElement), DateTimeOffset.UtcNow);

var annotation = verdict switch
{
    { FailOpenReason: not null } => "warning",
    { Outcome: GateOutcome.Fail } => "error",
    _ => "notice",
};
Console.WriteLine($"::{annotation} title={Escape(verdict.Title, property: true)}::{Escape(verdict.Detail)}");

Append("GITHUB_STEP_SUMMARY", $"## {verdict.Title}\n\n{verdict.Detail}\n");
Append("GITHUB_OUTPUT", $"fail-open-reason={verdict.FailOpenReason}\n");

return verdict.Outcome == GateOutcome.Pass ? 0 : 1;

static void Append(string fileVariable, string text)
{
    if (Environment.GetEnvironmentVariable(fileVariable) is { Length: > 0 } path)
        File.AppendAllText(path, text);
}

// Workflow command escaping: https://github.com/actions/toolkit/blob/main/packages/core/src/command.ts
static string Escape(string value, bool property = false)
{
    var escaped = value.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
    return property ? escaped.Replace(":", "%3A").Replace(",", "%2C") : escaped;
}
