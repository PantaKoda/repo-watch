using System.Text.Json.Serialization;

namespace RepoWatch.GitHub.Repositories;

// REST shapes. Only the fields Repo Watch reads; everything is optional so a missing field
// degrades one item rather than failing a whole section.

internal sealed record OwnerDto(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("type")] string? Type);

internal sealed record RepositoryInfoDto
{
    [JsonPropertyName("id")] public long Id { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("owner")] public OwnerDto? Owner { get; init; }

    [JsonPropertyName("private")] public bool Private { get; init; }

    [JsonPropertyName("archived")] public bool Archived { get; init; }

    [JsonPropertyName("has_issues")] public bool HasIssues { get; init; }

    [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
}

internal sealed record RunsPageDto([property: JsonPropertyName("workflow_runs")] List<RunDto?>? WorkflowRuns);

internal sealed record RunPullRequestDto([property: JsonPropertyName("number")] int Number);

internal sealed record RunDto
{
    [JsonPropertyName("id")] public long Id { get; init; }

    [JsonPropertyName("workflow_id")] public long WorkflowId { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("run_number")] public int RunNumber { get; init; }

    [JsonPropertyName("run_attempt")] public int RunAttempt { get; init; }

    [JsonPropertyName("head_sha")] public string? HeadSha { get; init; }

    [JsonPropertyName("head_branch")] public string? HeadBranch { get; init; }

    [JsonPropertyName("event")] public string? Event { get; init; }

    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("conclusion")] public string? Conclusion { get; init; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }

    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("pull_requests")] public List<RunPullRequestDto>? PullRequests { get; init; }
}

/// <summary>The combined status of a ref; only the resolved head commit is read.</summary>
internal sealed record CombinedStatusDto([property: JsonPropertyName("sha")] string? Sha);

// GraphQL shapes.

internal sealed record GraphQLRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] Dictionary<string, string> Variables);

internal sealed record GraphQLErrorDto(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record LoginNode([property: JsonPropertyName("login")] string? Login);

internal sealed record NameNode([property: JsonPropertyName("name")] string? Name);

internal sealed record Connection<T>(
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("nodes")] List<T?>? Nodes);

internal sealed record PullRequestsResponse(
    [property: JsonPropertyName("data")] PullRequestsData? Data,
    [property: JsonPropertyName("errors")] List<GraphQLErrorDto>? Errors);

internal sealed record PullRequestsData([property: JsonPropertyName("repository")] PullRequestsRepository? Repository);

internal sealed record PullRequestsRepository([property: JsonPropertyName("pullRequests")] Connection<PullRequestNode>? PullRequests);

internal sealed record PullRequestNode
{
    [JsonPropertyName("databaseId")] public long DatabaseId { get; init; }

    [JsonPropertyName("number")] public int Number { get; init; }

    [JsonPropertyName("title")] public string? Title { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }

    [JsonPropertyName("isDraft")] public bool IsDraft { get; init; }

    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("mergeable")] public string? Mergeable { get; init; }

    [JsonPropertyName("headRefName")] public string? HeadRefName { get; init; }

    [JsonPropertyName("baseRefName")] public string? BaseRefName { get; init; }

    [JsonPropertyName("headRefOid")] public string? HeadRefOid { get; init; }

    [JsonPropertyName("author")] public LoginNode? Author { get; init; }

    [JsonPropertyName("reviewRequests")] public Connection<ReviewRequestNode>? ReviewRequests { get; init; }

    [JsonPropertyName("reviews")] public Connection<ReviewNode>? Reviews { get; init; }

    [JsonPropertyName("commits")] public Connection<CommitNode>? Commits { get; init; }
}

internal sealed record ReviewRequestNode([property: JsonPropertyName("requestedReviewer")] RequestedReviewerNode? RequestedReviewer);

internal sealed record RequestedReviewerNode
{
    [JsonPropertyName("__typename")] public string? TypeName { get; init; }

    [JsonPropertyName("login")] public string? Login { get; init; }

    [JsonPropertyName("slug")] public string? Slug { get; init; }

    [JsonPropertyName("organization")] public LoginNode? Organization { get; init; }
}

internal sealed record OidNode([property: JsonPropertyName("oid")] string? Oid);

internal sealed record ReviewNode
{
    [JsonPropertyName("databaseId")] public long DatabaseId { get; init; }

    [JsonPropertyName("state")] public string? State { get; init; }

    [JsonPropertyName("submittedAt")] public DateTimeOffset? SubmittedAt { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }

    [JsonPropertyName("author")] public LoginNode? Author { get; init; }

    [JsonPropertyName("commit")] public OidNode? Commit { get; init; }
}

internal sealed record CommitNode([property: JsonPropertyName("commit")] CommitDetail? Commit);

internal sealed record CommitDetail([property: JsonPropertyName("statusCheckRollup")] StatusCheckRollupNode? StatusCheckRollup);

internal sealed record StatusCheckRollupNode([property: JsonPropertyName("contexts")] Connection<CheckContextNode>? Contexts);

internal sealed record DatabaseIdNode([property: JsonPropertyName("databaseId")] long DatabaseId);

internal sealed record CheckSuiteNode(
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("app")] DatabaseIdNode? App);

/// <summary>A CheckRun or StatusContext from the rollup union; fields of the other type are null.</summary>
internal sealed record CheckContextNode
{
    [JsonPropertyName("__typename")] public string? TypeName { get; init; }

    [JsonPropertyName("databaseId")] public long DatabaseId { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("conclusion")] public string? Conclusion { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }

    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("checkSuite")] public CheckSuiteNode? CheckSuite { get; init; }

    [JsonPropertyName("context")] public string? Context { get; init; }

    [JsonPropertyName("state")] public string? State { get; init; }

    [JsonPropertyName("targetUrl")] public string? TargetUrl { get; init; }

    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed record IssuesResponse(
    [property: JsonPropertyName("data")] IssuesData? Data,
    [property: JsonPropertyName("errors")] List<GraphQLErrorDto>? Errors);

internal sealed record IssuesData([property: JsonPropertyName("repository")] IssuesRepository? Repository);

internal sealed record IssuesRepository(
    [property: JsonPropertyName("hasIssuesEnabled")] bool HasIssuesEnabled,
    [property: JsonPropertyName("issues")] Connection<IssueNode>? Issues);

internal sealed record IssueNode
{
    [JsonPropertyName("databaseId")] public long DatabaseId { get; init; }

    [JsonPropertyName("number")] public int Number { get; init; }

    [JsonPropertyName("title")] public string? Title { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }

    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("author")] public LoginNode? Author { get; init; }

    [JsonPropertyName("labels")] public Connection<NameNode>? Labels { get; init; }

    [JsonPropertyName("assignees")] public Connection<LoginNode>? Assignees { get; init; }
}

[JsonSerializable(typeof(RepositoryInfoDto))]
[JsonSerializable(typeof(RunsPageDto))]
[JsonSerializable(typeof(CombinedStatusDto))]
[JsonSerializable(typeof(GraphQLRequest))]
[JsonSerializable(typeof(PullRequestsResponse))]
[JsonSerializable(typeof(IssuesResponse))]
internal sealed partial class RepositoryDataJsonContext : JsonSerializerContext;
