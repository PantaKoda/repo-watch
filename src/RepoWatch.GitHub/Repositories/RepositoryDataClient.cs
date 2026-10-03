using System.Globalization;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Mapping;

namespace RepoWatch.GitHub.Repositories;

/// <summary>Repository metadata plus whether GitHub has issues turned on for it.</summary>
public sealed record RepositoryInfo(RepositoryMetadata Metadata, bool HasIssues);

/// <summary>What to load for one repository's Actions section.</summary>
public sealed record ActionsRequest(string Owner, string Name, IReadOnlyList<string> Branches, IReadOnlyList<long> WorkflowIds);

/// <summary>Result of a section load: data, "feature unavailable", or an error.</summary>
public sealed record SectionResult<T>(T? Value, bool FeatureUnavailable, ResourceError? Error) where T : class
{
    public static SectionResult<T> Ok(T value) => new(value, false, null);

    public static SectionResult<T> Unavailable() => new(null, true, null);

    public static SectionResult<T> Fail(ResourceError error) => new(null, false, error);
}

/// <summary>
/// Loads one repository's data with read-only requests. Actions use REST; pull requests (with
/// reviews, review requests, mergeability and the head commit's checks) and issues each use one
/// GraphQL query, which replaces several REST calls per pull request and gives exact open counts
/// (the GraphQL issues connection never includes pull requests). Every section loads and fails
/// independently. Titles and other content are untrusted text and are only carried, never interpreted.
/// </summary>
public sealed class RepositoryDataClient(GitHubApiClient api)
{
    /// <summary>Recent runs shown in the Actions list.</summary>
    public const int RecentRunCount = 20;

    /// <summary>Open pull requests examined per refresh (most recently updated first).</summary>
    public const int PullRequestPage = 30;

    /// <summary>Open issues listed (most recently updated first); the count is always exact.</summary>
    public const int IssuePage = 20;

    /// <summary>GET /repositories/{id}: by ID, so renames and transfers are followed.</summary>
    public async Task<ApiResult<RepositoryInfo>> GetRepositoryAsync(RepositoryKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var result = await api.GetAsync(api.ApiUri(Invariant($"repositories/{key.RepositoryId}")), RepositoryDataJsonContext.Default.RepositoryInfoDto, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return ApiResult<RepositoryInfo>.Fail(result.Error!);
        }

        var dto = result.Value!;
        if (dto.Id != key.RepositoryId || dto.Owner?.Login is not { Length: > 0 } owner || string.IsNullOrEmpty(dto.Name) || HttpsUri(dto.HtmlUrl) is not { } html)
        {
            return ApiResult<RepositoryInfo>.Fail(api.GraphQLError(null));
        }

        var metadata = new RepositoryMetadata
        {
            Key = key,
            Owner = owner,
            Name = dto.Name,
            OwnerKind = string.Equals(dto.Owner.Type, "Organization", StringComparison.OrdinalIgnoreCase) ? RepositoryOwnerKind.Organization : RepositoryOwnerKind.User,
            IsPrivate = dto.Private,
            IsArchived = dto.Archived,
            DefaultBranch = string.IsNullOrEmpty(dto.DefaultBranch) ? "main" : dto.DefaultBranch,
            HtmlUrl = html,
        };
        return ApiResult<RepositoryInfo>.Ok(new RepositoryInfo(metadata, dto.HasIssues));
    }

    /// <summary>
    /// Recent runs (GET /actions/runs) plus each tracked branch's health: the branch head and the
    /// runs for that commit (GET /actions/runs?head_sha=). The head comes from the combined status
    /// (GET /commits/{branch}/status), which needs only Commit statuses: read; the git refs and
    /// branches endpoints would need Contents access, which Repo Watch does not request.
    /// The first branch is the primary one. A 404 on the run list means Actions is unavailable.
    /// </summary>
    public async Task<SectionResult<ActionsState>> GetActionsAsync(ActionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repo = RepoPath(request.Owner, request.Name);
        var recent = await api.GetAsync(api.ApiUri(Invariant($"{repo}/actions/runs?per_page={RecentRunCount}")), RepositoryDataJsonContext.Default.RunsPageDto, cancellationToken).ConfigureAwait(false);
        if (!recent.IsSuccess)
        {
            return recent.Error!.Kind == ResourceErrorKind.NotFound ? SectionResult<ActionsState>.Unavailable() : SectionResult<ActionsState>.Fail(recent.Error);
        }

        var branches = new List<CommitWorkflowSummary>();
        for (var i = 0; i < request.Branches.Count; i++)
        {
            var branch = request.Branches[i];
            var head = await api.GetAsync(api.ApiUri($"{repo}/commits/{EscapePath(branch)}/status?per_page=1"), RepositoryDataJsonContext.Default.CombinedStatusDto, cancellationToken).ConfigureAwait(false);
            if (!head.IsSuccess)
            {
                // A missing branch (or an empty repository) has no health; other errors fail the section.
                if (head.Error!.Kind == ResourceErrorKind.NotFound)
                {
                    continue;
                }

                return SectionResult<ActionsState>.Fail(head.Error);
            }

            if (head.Value!.Sha is not { Length: > 0 } sha)
            {
                continue;
            }

            var runs = await api.GetAsync(api.ApiUri(Invariant($"{repo}/actions/runs?head_sha={sha}&per_page=100")), RepositoryDataJsonContext.Default.RunsPageDto, cancellationToken).ConfigureAwait(false);
            if (!runs.IsSuccess)
            {
                return SectionResult<ActionsState>.Fail(runs.Error!);
            }

            branches.Add(WorkflowRunSelection.ForCommit(Filter(ToRuns(runs.Value!), request.WorkflowIds), sha, branch));
        }

        return SectionResult<ActionsState>.Ok(new ActionsState
        {
            RecentRuns = Filter(ToRuns(recent.Value!), request.WorkflowIds).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToList(),
            DefaultBranch = branches.FirstOrDefault(),
            Branches = branches,
        });
    }

    /// <summary>
    /// Open pull requests with reviews, pending review requests, mergeability and the head commit's
    /// check rollup in one GraphQL query. <paramref name="mineOnly"/> keeps pull requests authored by
    /// <paramref name="login"/> or requesting their review directly; team requests count only when
    /// membership is known, which this version does not establish.
    /// </summary>
    public async Task<SectionResult<PullRequestsState>> GetPullRequestsAsync(string owner, string name, bool mineOnly, string login, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var request = new GraphQLRequest(PullRequestsQuery, new Dictionary<string, string> { ["owner"] = owner, ["name"] = name });
        var result = await api.PostGraphQLAsync(request, RepositoryDataJsonContext.Default.GraphQLRequest, RepositoryDataJsonContext.Default.PullRequestsResponse, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return SectionResult<PullRequestsState>.Fail(result.Error!);
        }

        var connection = result.Value!.Data?.Repository?.PullRequests;
        if (connection is null)
        {
            return SectionResult<PullRequestsState>.Fail(api.GraphQLError(result.Value.Errors?.FirstOrDefault()?.Type));
        }

        var entries = new List<PullRequestEntry>();
        foreach (var node in connection.Nodes ?? [])
        {
            if (node is null || ToPullRequest(node) is not { } pullRequest)
            {
                continue;
            }

            if (mineOnly && !IsMine(pullRequest, login))
            {
                continue;
            }

            var reviews = (node.Reviews?.Nodes ?? []).Select(ToReview).OfType<PullRequestReview>().ToList();
            entries.Add(new PullRequestEntry
            {
                PullRequest = pullRequest,
                Checks = Resource<CommitChecksSummary>.NotLoaded.Succeeded(ToChecks(node, pullRequest.HeadSha), now),
                Reviews = Resource<ReviewSummary>.NotLoaded.Succeeded(ReviewSummary.From(reviews, pullRequest.RequestedReviewers, pullRequest.HeadSha), now),
            });
        }

        return SectionResult<PullRequestsState>.Ok(new PullRequestsState
        {
            Items = entries,
            OpenCount = ItemCount.Exact(connection.TotalCount),
        });
    }

    /// <summary>Open issues, most recently updated first, with an exact open count (pull requests excluded).</summary>
    public async Task<SectionResult<IssuesState>> GetIssuesAsync(string owner, string name, CancellationToken cancellationToken)
    {
        var request = new GraphQLRequest(IssuesQuery, new Dictionary<string, string> { ["owner"] = owner, ["name"] = name });
        var result = await api.PostGraphQLAsync(request, RepositoryDataJsonContext.Default.GraphQLRequest, RepositoryDataJsonContext.Default.IssuesResponse, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return SectionResult<IssuesState>.Fail(result.Error!);
        }

        var repository = result.Value!.Data?.Repository;
        if (repository is null)
        {
            return SectionResult<IssuesState>.Fail(api.GraphQLError(result.Value.Errors?.FirstOrDefault()?.Type));
        }

        if (!repository.HasIssuesEnabled)
        {
            return SectionResult<IssuesState>.Unavailable();
        }

        if (repository.Issues is not { } issues)
        {
            return SectionResult<IssuesState>.Fail(api.GraphQLError(result.Value.Errors?.FirstOrDefault()?.Type));
        }

        return SectionResult<IssuesState>.Ok(new IssuesState
        {
            Items = (issues.Nodes ?? []).Select(ToIssue).OfType<Issue>().ToList(),
            OpenCount = ItemCount.Exact(issues.TotalCount),
        });
    }

    public static bool IsMine(PullRequest pullRequest, string login)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return string.Equals(pullRequest.AuthorLogin, login, StringComparison.OrdinalIgnoreCase)
            || pullRequest.RequestedReviewers.Any(r => r.Kind == ReviewerKind.User && string.Equals(r.Login, login, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<WorkflowRun> Filter(IEnumerable<WorkflowRun> runs, IReadOnlyList<long> workflowIds) =>
        workflowIds.Count == 0 ? runs : runs.Where(r => workflowIds.Contains(r.WorkflowId));

    private static List<WorkflowRun> ToRuns(RunsPageDto page) => (page.WorkflowRuns ?? []).Select(ToRun).OfType<WorkflowRun>().ToList();

    private static WorkflowRun? ToRun(RunDto? dto)
    {
        if (dto is null || dto.Id <= 0 || string.IsNullOrEmpty(dto.HeadSha) || HttpsUri(dto.HtmlUrl) is not { } html || dto.CreatedAt is not { } created)
        {
            return null;
        }

        return new WorkflowRun
        {
            Id = dto.Id,
            WorkflowId = dto.WorkflowId,
            WorkflowName = string.IsNullOrEmpty(dto.Name) ? Invariant($"Workflow {dto.WorkflowId}") : dto.Name,
            RunNumber = dto.RunNumber,
            RunAttempt = Math.Max(1, dto.RunAttempt),
            HeadSha = dto.HeadSha,
            HeadBranch = dto.HeadBranch,
            Event = dto.Event ?? "unknown",
            PullRequestNumbers = (dto.PullRequests ?? []).Select(p => p.Number).Where(n => n > 0).ToList(),
            Outcome = GitHubStatusMapping.ToOutcome(dto.Status, dto.Conclusion),
            HtmlUrl = html,
            CreatedAt = created,
            UpdatedAt = dto.UpdatedAt ?? created,
        };
    }

    private static PullRequest? ToPullRequest(PullRequestNode node)
    {
        if (node.DatabaseId <= 0 || node.Number <= 0 || string.IsNullOrEmpty(node.HeadRefOid) || HttpsUri(node.Url) is not { } html || node.CreatedAt is not { } created)
        {
            return null;
        }

        var requests = (node.ReviewRequests?.Nodes ?? []).Select(r => r?.RequestedReviewer).Select(ToReviewRequest).OfType<ReviewRequest>().ToList();
        return new PullRequest
        {
            Id = node.DatabaseId,
            Number = node.Number,
            Title = node.Title ?? "",
            AuthorLogin = node.Author?.Login ?? "ghost",
            State = PullRequestState.Open,
            IsDraft = node.IsDraft,
            HeadSha = node.HeadRefOid,
            HeadRef = node.HeadRefName ?? "",
            BaseRef = node.BaseRefName ?? "",
            RequestedReviewers = requests,
            // GraphQL "mergeable" only says whether the branches conflict; branch protection and
            // required checks are not known here, so this never becomes "ready to merge".
            MergeState = node.IsDraft ? MergeState.Draft : node.Mergeable switch
            {
                "MERGEABLE" => MergeState.Clean,
                "CONFLICTING" => MergeState.Conflicting,
                _ => MergeState.Unknown,
            },
            HtmlUrl = html,
            CreatedAt = created,
            UpdatedAt = node.UpdatedAt ?? created,
        };
    }

    private static ReviewRequest? ToReviewRequest(RequestedReviewerNode? reviewer) => reviewer?.TypeName switch
    {
        "User" or "Bot" or "Mannequin" when !string.IsNullOrEmpty(reviewer.Login) => new ReviewRequest(ReviewerKind.User, reviewer.Login),
        "Team" when !string.IsNullOrEmpty(reviewer.Slug) => new ReviewRequest(ReviewerKind.Team, $"{reviewer.Organization?.Login ?? "?"}/{reviewer.Slug}"),
        _ => null,
    };

    private static PullRequestReview? ToReview(ReviewNode? node)
    {
        if (node is null || node.SubmittedAt is not { } submitted || GitHubStatusMapping.ToReviewState(node.State) is not { } state || state == ReviewState.Pending
            || HttpsUri(node.Url) is not { } html)
        {
            return null; // unsubmitted reviews are private to their author
        }

        return new PullRequestReview
        {
            Id = node.DatabaseId,
            AuthorLogin = node.Author?.Login ?? "ghost",
            State = state,
            CommitSha = node.Commit?.Oid ?? "",
            SubmittedAt = submitted,
            HtmlUrl = html,
        };
    }

    private static CommitChecksSummary ToChecks(PullRequestNode node, string headSha)
    {
        var commit = node.Commits?.Nodes?.FirstOrDefault()?.Commit;
        var contexts = commit?.StatusCheckRollup?.Contexts;
        var checkRuns = new List<CheckRun>();
        var statuses = new List<CommitStatus>();
        foreach (var context in contexts?.Nodes ?? [])
        {
            switch (context?.TypeName)
            {
                case "CheckRun" when context.DatabaseId > 0 && HttpsUri(context.Url) is { } url:
                    checkRuns.Add(new CheckRun
                    {
                        Id = context.DatabaseId,
                        Name = context.Name ?? "check",
                        AppId = context.CheckSuite?.App?.DatabaseId ?? 0,
                        CheckSuiteId = context.CheckSuite?.DatabaseId ?? 0,
                        HeadSha = headSha,
                        Outcome = GitHubStatusMapping.ToOutcome(context.Status?.ToLowerInvariant(), context.Conclusion?.ToLowerInvariant()),
                        HtmlUrl = url,
                        StartedAt = context.StartedAt,
                        CompletedAt = context.CompletedAt,
                    });
                    break;
                case "StatusContext" when !string.IsNullOrEmpty(context.Context):
                    statuses.Add(new CommitStatus
                    {
                        Id = 0,
                        Context = context.Context,
                        Sha = headSha,
                        Outcome = GitHubStatusMapping.FromCommitStatusState(context.State?.ToLowerInvariant()),
                        TargetUrl = HttpsUri(context.TargetUrl),
                        CreatedAt = context.CreatedAt ?? DateTimeOffset.MinValue,
                    });
                    break;
            }
        }

        var summary = CommitChecks.Summarize(headSha, checkRuns, statuses);
        var complete = contexts is null || contexts.TotalCount <= (contexts.Nodes?.Count ?? 0);
        if (complete)
        {
            return summary;
        }

        // Unloaded checks could be failing: only a known failure or pending state stays meaningful.
        var state = summary.Rollup.State is RollupState.Passing or RollupState.Neutral or RollupState.NoChecks ? RollupState.Unknown : summary.Rollup.State;
        return summary with { Rollup = summary.Rollup with { State = state }, IsComplete = false };
    }

    private static Issue? ToIssue(IssueNode? node)
    {
        if (node is null || node.DatabaseId <= 0 || node.Number <= 0 || HttpsUri(node.Url) is not { } html || node.CreatedAt is not { } created)
        {
            return null;
        }

        return new Issue
        {
            Id = node.DatabaseId,
            Number = node.Number,
            Title = node.Title ?? "",
            AuthorLogin = node.Author?.Login ?? "ghost",
            State = IssueState.Open,
            Labels = (node.Labels?.Nodes ?? []).Select(l => l?.Name).OfType<string>().ToList(),
            Assignees = (node.Assignees?.Nodes ?? []).Select(a => a?.Login).OfType<string>().ToList(),
            HtmlUrl = html,
            CreatedAt = created,
            UpdatedAt = node.UpdatedAt ?? created,
        };
    }

    private static string RepoPath(string owner, string name) => $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";

    /// <summary>Branch names may contain "/", which stays a path separator in the ref path.</summary>
    private static string EscapePath(string branch) => string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));

    private static Uri? HttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    internal const string PullRequestsQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, first: 30, orderBy: {field: UPDATED_AT, direction: DESC}) {
              totalCount
              nodes {
                databaseId number title url isDraft createdAt updatedAt mergeable
                headRefName baseRefName headRefOid
                author { login }
                reviewRequests(first: 20) {
                  nodes { requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Mannequin { login } ... on Team { slug organization { login } } } }
                }
                reviews(last: 50) {
                  nodes { databaseId state submittedAt url author { login } commit { oid } }
                }
                commits(last: 1) {
                  nodes { commit { statusCheckRollup { contexts(first: 100) {
                    totalCount
                    nodes {
                      __typename
                      ... on CheckRun { databaseId name status conclusion url startedAt completedAt checkSuite { databaseId app { databaseId } } }
                      ... on StatusContext { context state targetUrl createdAt }
                    }
                  } } } }
                }
              }
            }
          }
        }
        """;

    internal const string IssuesQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            hasIssuesEnabled
            issues(states: OPEN, first: 20, orderBy: {field: UPDATED_AT, direction: DESC}) {
              totalCount
              nodes {
                databaseId number title url createdAt updatedAt
                author { login }
                labels(first: 10) { nodes { name } }
                assignees(first: 10) { nodes { login } }
              }
            }
          }
        }
        """;
}
