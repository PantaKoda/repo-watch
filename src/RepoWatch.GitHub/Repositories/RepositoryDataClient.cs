using System.Globalization;
using System.Text.Json;
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

    /// <summary>Pull requests whose checks are loaded per refresh (two REST requests each); the rest show "not loaded".</summary>
    public const int ChecksLoadedFor = 10;

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
            HasIssues = dto.HasIssues,
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
        var missing = new List<string>();
        for (var i = 0; i < request.Branches.Count; i++)
        {
            var branch = request.Branches[i];
            var head = await api.GetAsync(api.ApiUri($"{repo}/commits/{EscapePath(branch)}/status?per_page=1"), RepositoryDataJsonContext.Default.CombinedStatusDto, cancellationToken).ConfigureAwait(false);
            if (!head.IsSuccess)
            {
                // A missing branch (or an empty repository) has no health; other errors fail the section.
                if (head.Error!.Kind == ResourceErrorKind.NotFound)
                {
                    missing.Add(branch);
                    continue;
                }

                return SectionResult<ActionsState>.Fail(head.Error);
            }

            if (head.Value!.Sha is not { Length: > 0 } sha)
            {
                missing.Add(branch);
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
            // Only the first requested branch is primary; a missing one is never replaced by the next.
            DefaultBranch = branches.FirstOrDefault(b => request.Branches.Count > 0 && b.Branch == request.Branches[0]),
            Branches = branches,
            MissingBranches = missing,
        });
    }

    /// <summary>
    /// Open pull requests with reviews, pending review requests and mergeability from one GraphQL query,
    /// plus the head commit's checks over REST for the first <see cref="ChecksLoadedFor"/> listed.
    /// <list type="bullet">
    /// <item>All: the most recently updated open pull requests, with the exact open count.</item>
    /// <item>Mine (<paramref name="mineOnly"/>): two searches, authored by <paramref name="login"/> and
    /// requesting their review, so older pull requests are not missed. Team requests count only when
    /// membership is known, which this version does not establish. The count is exact only when both
    /// searches returned every result.</item>
    /// </list>
    /// GraphQL can return partial data: an error inside one pull request marks its reviews as failed
    /// instead of reading the missing field as "none". Checks use REST (GET /commits/{sha}/check-runs and
    /// /status) because GraphQL commit data needs Contents access, which Repo Watch does not request
    /// (verified live: statusCheckRollup was denied).
    /// </summary>
    public async Task<SectionResult<PullRequestsState>> GetPullRequestsAsync(string owner, string name, bool mineOnly, string login, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<(string List, int Index, PullRequestNode Node)> nodes;
        IReadOnlyList<MergedPullRequest> merged;
        IReadOnlyList<GraphQLErrorDto> errors;
        ItemCount? count = null;
        if (mineOnly)
        {
            var scope = $"is:pr is:open repo:{owner}/{name}";
            var request = new GraphQLRequest(MyPullRequestsQuery, new Dictionary<string, string>
            {
                ["authored"] = $"{scope} author:{login}",
                ["requested"] = $"{scope} review-requested:{login}",
                ["merged"] = $"is:pr is:merged repo:{owner}/{name} author:{login} sort:updated-desc",
            });
            var result = await api.PostGraphQLAsync(request, RepositoryDataJsonContext.Default.GraphQLRequest, RepositoryDataJsonContext.Default.MyPullRequestsResponse, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return SectionResult<PullRequestsState>.Fail(result.Error!);
            }

            if (result.Value!.Data is not { Authored: { } authored, Requested: { } requested })
            {
                return SectionResult<PullRequestsState>.Fail(api.GraphQLError(result.Value.Errors?.FirstOrDefault()?.Type));
            }

            nodes = [.. Indexed("authored", authored.Nodes), .. Indexed("requested", requested.Nodes)];
            merged = ToMerged(result.Value.Data.Merged);
            errors = result.Value.Errors ?? [];
            var complete = authored.IssueCount <= (authored.Nodes?.Count ?? 0) && requested.IssueCount <= (requested.Nodes?.Count ?? 0);
            count = complete ? null : ItemCount.AtLeast(0); // resolved below from the matched pull requests
        }
        else
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

            nodes = Indexed("pullRequests", connection.Nodes);
            merged = ToMerged(result.Value.Data!.Repository!.Merged);
            errors = result.Value.Errors ?? [];
            count = ItemCount.Exact(connection.TotalCount);
        }

        var entries = new List<PullRequestEntry>();
        var seen = new HashSet<int>();
        foreach (var (list, index, node) in nodes)
        {
            if (ToPullRequest(node) is not { } pullRequest || (mineOnly && !IsMine(pullRequest, login)) || !seen.Add(pullRequest.Number))
            {
                continue;
            }

            var reviewsError = ReviewsError(errors, list, index);
            var reviews = (node.Reviews?.Nodes ?? []).Select(ToReview).OfType<PullRequestReview>().ToList();
            entries.Add(new PullRequestEntry
            {
                PullRequest = pullRequest,
                Reviews = reviewsError is null
                    ? Resource<ReviewSummary>.NotLoaded.Succeeded(ReviewSummary.From(reviews, pullRequest.RequestedReviewers, pullRequest.HeadSha), now)
                    : Resource<ReviewSummary>.NotLoaded.Failed(reviewsError),
            });
        }

        // The repository query is already newest first; merged search results need ordering.
        var items = mineOnly ? entries.OrderByDescending(e => e.PullRequest.UpdatedAt).ThenByDescending(e => e.PullRequest.Number).ToList() : entries;
        ResourceError? stop = null;
        for (var i = 0; i < items.Count && i < ChecksLoadedFor; i++)
        {
            // Once rate limited, further check requests would hit the same limit.
            items[i] = items[i] with
            {
                Checks = stop is not null
                    ? Resource<CommitChecksSummary>.NotLoaded.Failed(stop)
                    : await LoadChecksAsync(RepoPath(owner, name), items[i].PullRequest.HeadSha, now, cancellationToken).ConfigureAwait(false),
            };
            if (items[i].Checks.LastError is { Kind: ResourceErrorKind.RateLimited } limited)
            {
                stop = limited;
            }
        }

        return SectionResult<PullRequestsState>.Ok(new PullRequestsState
        {
            Items = items,
            RecentlyMerged = merged,
            OpenCount = count switch
            {
                null => ItemCount.Exact(entries.Count),
                { IsExact: false } => ItemCount.AtLeast(entries.Count),
                { } exact => exact,
            },
        });
    }

    /// <summary>
    /// The error, if any, that makes one listed pull request's reviews incomplete: an error whose path
    /// points into that node, or an error not tied to any node (the data can't be trusted to be complete).
    /// </summary>
    private ResourceError? ReviewsError(IReadOnlyList<GraphQLErrorDto> errors, string list, int index)
    {
        foreach (var error in errors)
        {
            var path = error.Path ?? [];
            var nodesAt = path.FindIndex(p => p.ValueKind == JsonValueKind.String && p.GetString() == "nodes");
            var target = nodesAt > 0 && path[nodesAt - 1].ValueKind == JsonValueKind.String ? path[nodesAt - 1].GetString() : null;
            var targetIndex = nodesAt >= 0 && nodesAt + 1 < path.Count && path[nodesAt + 1].ValueKind == JsonValueKind.Number ? path[nodesAt + 1].GetInt32() : -1;
            if (target is not null && targetIndex >= 0 && (target != list || targetIndex != index))
            {
                continue; // about another pull request
            }

            return api.GraphQLError(error.Type);
        }

        return null;
    }

    /// <summary>
    /// Check runs (Checks: read) and legacy statuses (Commit statuses: read) of one commit. A partial
    /// list never rolls up to Passing.
    /// </summary>
    private async Task<Resource<CommitChecksSummary>> LoadChecksAsync(string repo, string sha, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var runs = await api.GetAsync(api.ApiUri($"{repo}/commits/{sha}/check-runs?per_page=100"), RepositoryDataJsonContext.Default.CheckRunsPageDto, cancellationToken).ConfigureAwait(false);
        if (!runs.IsSuccess)
        {
            return Resource<CommitChecksSummary>.NotLoaded.Failed(runs.Error!);
        }

        var statuses = await api.GetAsync(api.ApiUri($"{repo}/commits/{sha}/status?per_page=100"), RepositoryDataJsonContext.Default.CombinedStatusDto, cancellationToken).ConfigureAwait(false);
        if (!statuses.IsSuccess)
        {
            return Resource<CommitChecksSummary>.NotLoaded.Failed(statuses.Error!);
        }

        var checkRuns = (runs.Value!.CheckRuns ?? []).Select(ToCheckRun).OfType<CheckRun>().ToList();
        var commitStatuses = (statuses.Value!.Statuses ?? []).Select(s => ToStatus(s, sha)).OfType<CommitStatus>().ToList();
        var summary = CommitChecks.Summarize(sha, checkRuns, commitStatuses);
        var complete = runs.Value.TotalCount <= (runs.Value.CheckRuns?.Count ?? 0) && statuses.Value.TotalCount <= (statuses.Value.Statuses?.Count ?? 0);
        if (!complete)
        {
            // Unloaded checks could be failing: only a known failure or pending state stays meaningful.
            var state = summary.Rollup.State is RollupState.Passing or RollupState.Neutral or RollupState.NoChecks ? RollupState.Unknown : summary.Rollup.State;
            summary = summary with { Rollup = summary.Rollup with { State = state }, IsComplete = false };
        }

        return Resource<CommitChecksSummary>.NotLoaded.Succeeded(summary, now);
    }

    private static CheckRun? ToCheckRun(CheckRunDto? dto)
    {
        if (dto is null || dto.Id <= 0 || string.IsNullOrEmpty(dto.HeadSha) || HttpsUri(dto.HtmlUrl) is not { } html)
        {
            return null;
        }

        return new CheckRun
        {
            Id = dto.Id,
            Name = dto.Name ?? "check",
            AppId = dto.App?.Id ?? 0,
            CheckSuiteId = dto.CheckSuite?.Id ?? 0,
            HeadSha = dto.HeadSha,
            Outcome = GitHubStatusMapping.ToOutcome(dto.Status, dto.Conclusion),
            HtmlUrl = html,
            StartedAt = dto.StartedAt,
            CompletedAt = dto.CompletedAt,
        };
    }

    private static CommitStatus? ToStatus(StatusDto? dto, string sha) => dto is null || string.IsNullOrEmpty(dto.Context)
        ? null
        : new CommitStatus
        {
            Id = dto.Id,
            Context = dto.Context,
            Sha = sha,
            Outcome = GitHubStatusMapping.FromCommitStatusState(dto.State),
            TargetUrl = HttpsUri(dto.TargetUrl),
            CreatedAt = dto.CreatedAt ?? DateTimeOffset.MinValue,
        };
    private static List<MergedPullRequest> ToMerged(Connection<MergedNode>? connection) => (connection?.Nodes ?? [])
        .Where(n => n is { Number: > 0, MergedAt: not null } && HttpsUri(n.Url) is not null)
        .Select(n => new MergedPullRequest(n!.Number, n.Title ?? "", HttpsUri(n.Url)!, n.MergedAt!.Value))
        .ToList();

    private static List<(string List, int Index, PullRequestNode Node)> Indexed(string list, List<PullRequestNode?>? nodes) =>
        (nodes ?? []).Select((node, index) => (list, index, node)).Where(n => n.node is not null).Select(n => (n.list, n.index, n.node!)).ToList();

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

    private const string PullRequestFields = """
        fragment PullRequestFields on PullRequest {
                databaseId number title url isDraft createdAt updatedAt mergeable
                headRefName baseRefName headRefOid
                author { login }
                reviewRequests(first: 20) {
                  nodes { requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Mannequin { login } ... on Team { slug organization { login } } } }
                }
                reviews(last: 50) {
                  nodes { databaseId state submittedAt url author { login } commit { oid } }
                }
        }
        """;

    internal const string PullRequestsQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, first: 30, orderBy: {field: UPDATED_AT, direction: DESC}) {
              totalCount
              nodes { ...PullRequestFields }
            }
            merged: pullRequests(states: MERGED, first: 10, orderBy: {field: UPDATED_AT, direction: DESC}) {
              totalCount
              nodes { number title url mergedAt }
            }
          }
        }
        """ + "\n" + PullRequestFields;

    internal const string MyPullRequestsQuery = """
        query($authored: String!, $requested: String!, $merged: String!) {
          authored: search(type: ISSUE, query: $authored, first: 50) { issueCount nodes { ...PullRequestFields } }
          requested: search(type: ISSUE, query: $requested, first: 50) { issueCount nodes { ...PullRequestFields } }
          merged: search(type: ISSUE, query: $merged, first: 10) { issueCount nodes { ... on PullRequest { number title url mergedAt } } }
        }
        """ + "\n" + PullRequestFields;

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
