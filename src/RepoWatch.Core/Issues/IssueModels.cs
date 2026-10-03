using RepoWatch.Core.State;

namespace RepoWatch.Core.Issues;

public enum IssueState
{
    Open,
    Closed,
}

/// <summary>An issue. Pull requests returned by the REST issues endpoint must not be modeled as issues.</summary>
public sealed record Issue
{
    public required long Id { get; init; }

    public required int Number { get; init; }

    /// <summary>Untrusted repository content; display as plain text only.</summary>
    public required string Title { get; init; }

    public required string AuthorLogin { get; init; }

    public required IssueState State { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public IReadOnlyList<string> Assignees { get; init; } = [];

    public required Uri HtmlUrl { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record IssuesState
{
    /// <summary>Most recently updated open issues (may be a subset of all open issues).</summary>
    public IReadOnlyList<Issue> Items { get; init; } = [];

    public required ItemCount OpenCount { get; init; }
}
