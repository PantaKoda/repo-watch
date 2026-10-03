using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Status;

namespace RepoWatch.Core.Tests;

/// <summary>Builders for test data. Values are synthetic, not taken from any real repository.</summary>
internal static class Fixtures
{
    public const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string PreviousSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly AccountKey Account = new("github.com", 1001);

    public static WorkflowRun Run(
        long id,
        CheckOutcome outcome,
        int attempt = 1,
        int runNumber = 1,
        long workflowId = 10,
        string sha = HeadSha,
        string @event = "push",
        int minutes = 0) => new()
        {
            Id = id,
            WorkflowId = workflowId,
            WorkflowName = $"workflow-{workflowId}",
            RunNumber = runNumber,
            RunAttempt = attempt,
            HeadSha = sha,
            HeadBranch = "main",
            Event = @event,
            Outcome = outcome,
            HtmlUrl = new Uri($"https://github.com/o/r/actions/runs/{id}/attempts/{attempt}"),
            CreatedAt = T0,
            UpdatedAt = T0.AddMinutes(minutes),
        };

    public static CheckRun Check(long id, string name, CheckOutcome outcome, long appId = 15368, string sha = HeadSha) => new()
    {
        Id = id,
        Name = name,
        AppId = appId,
        HeadSha = sha,
        Outcome = outcome,
        HtmlUrl = new Uri($"https://github.com/o/r/runs/{id}"),
    };

    public static CommitStatus LegacyStatus(long id, string context, CheckOutcome outcome, int minutes, string sha = HeadSha) => new()
    {
        Id = id,
        Context = context,
        Sha = sha,
        Outcome = outcome,
        CreatedAt = T0.AddMinutes(minutes),
    };

    public static PullRequestReview Review(long id, string author, ReviewState state, int minutes, string sha = HeadSha) => new()
    {
        Id = id,
        AuthorLogin = author,
        State = state,
        CommitSha = sha,
        SubmittedAt = T0.AddMinutes(minutes),
        HtmlUrl = new Uri($"https://github.com/o/r/pull/1#pullrequestreview-{id}"),
    };
}
