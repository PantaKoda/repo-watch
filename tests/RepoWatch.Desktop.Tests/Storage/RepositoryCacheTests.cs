using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.Desktop.Storage;
using RepoWatch.GitHub.Api;

namespace RepoWatch.Desktop.Tests.Storage;

public sealed class RepositoryCacheTests : IDisposable
{
    private static readonly AccountKey Octo = new("github.com", 4242);
    private static readonly AccountKey Other = new("github.com", 777);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "repowatch-cache-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalDatabase _database;

    public RepositoryCacheTests()
    {
        _database = new LocalDatabase(Path.Combine(_directory, "repowatch.db"));
        _database.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private RepositoryCache Cache() => new(_database, _time, NullLogger<RepositoryCache>.Instance);

    private static void Save(RepositoryCache cache, RepositorySnapshot snapshot) => cache.ForAccount(snapshot.Key.Account).Save(snapshot);

    private static IReadOnlyDictionary<long, RepositorySnapshot> Load(RepositoryCache cache, AccountKey account) => cache.ForAccount(account).Load();

    private static Core.Settings.WatchedRepository Watch(long id, string name = "hello") => new() { RepositoryId = id, Owner = "octo", Name = name };

    private static Uri Api(string path) => new("https://api.github.com/" + path);

    private static RepositorySnapshot Full(AccountKey account, long id, DateTimeOffset at)
    {
        var key = new RepositoryKey(account, id);
        var run = new WorkflowRun
        {
            Id = 30, WorkflowId = 7, WorkflowName = "CI", RunNumber = 12, RunAttempt = 2, HeadSha = "abc", HeadBranch = "main", Event = "push",
            PullRequestNumbers = [4], Outcome = CheckOutcome.Failure, HtmlUrl = new Uri("https://github.com/octo/hello/actions/runs/30"), CreatedAt = at, UpdatedAt = at,
        };
        var pull = new PullRequest
        {
            Id = 400, Number = 4, Title = "Fix <b>bug</b>", AuthorLogin = "octo", State = PullRequestState.Open, IsDraft = false, HeadSha = "def", HeadRef = "fix",
            BaseRef = "main", RequestedReviewers = [new ReviewRequest(ReviewerKind.Team, "acme/core")], MergeState = MergeState.Conflicting,
            HtmlUrl = new Uri("https://github.com/octo/hello/pull/4"), CreatedAt = at, UpdatedAt = at,
        };
        var review = new PullRequestReview { Id = 1, AuthorLogin = "alice", State = ReviewState.Approved, CommitSha = "def", SubmittedAt = at, HtmlUrl = new Uri("https://github.com/octo/hello/pull/4#r1") };
        var checkRun = new CheckRun { Id = 9, Name = "build", AppId = 1, CheckSuiteId = 2, HeadSha = "def", Outcome = CheckOutcome.Running, HtmlUrl = new Uri("https://github.com/octo/hello/runs/9") };
        return new RepositorySnapshot(key)
        {
            Metadata = Resource<RepositoryMetadata>.NotLoaded.Succeeded(new RepositoryMetadata
            {
                Key = key, Owner = "octo", Name = "hello", OwnerKind = RepositoryOwnerKind.Organization, IsPrivate = true, IsArchived = false,
                DefaultBranch = "main", HtmlUrl = new Uri("https://github.com/octo/hello"), HasIssues = true,
            }, at),
            Actions = Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState
            {
                RecentRuns = [run],
                DefaultBranch = WorkflowRunSelection.ForCommit([run], "abc", "main"),
                Branches = [WorkflowRunSelection.ForCommit([run], "abc", "main")],
                MissingBranches = ["release"],
            }, at),
            PullRequests = Resource<PullRequestsState>.NotLoaded.Succeeded(new PullRequestsState
            {
                Items =
                [
                    new PullRequestEntry
                    {
                        PullRequest = pull,
                        Checks = Resource<CommitChecksSummary>.NotLoaded.Succeeded(CommitChecks.Summarize("def", [checkRun], []), at),
                        Reviews = Resource<ReviewSummary>.NotLoaded.Succeeded(ReviewSummary.From([review], pull.RequestedReviewers, "def"), at),
                    },
                ],
                OpenCount = ItemCount.AtLeast(1),
            }, at),
            Issues = Resource<IssuesState>.NotLoaded.FeatureUnavailable(at),
        };
    }

    [Fact]
    public void Snapshots_round_trip_and_come_back_labeled_as_cached()
    {
        var at = _time.GetUtcNow();
        Save(Cache(), Full(Octo, 1, at));

        var restored = Load(Cache(), Octo)[1];

        Assert.Equal(Freshness.Cached, restored.Actions.GetFreshness(at.AddMinutes(1), TimeSpan.FromMinutes(10)));
        Assert.Equal("octo/hello", restored.Metadata.Value!.FullName);
        Assert.Equal(RepositoryOwnerKind.Organization, restored.Metadata.Value.OwnerKind);
        var actions = restored.Actions.Value!;
        Assert.Equal(2, actions.RecentRuns.Single().RunAttempt);
        Assert.Equal(RollupState.Failing, actions.DefaultBranch!.Rollup.State);
        Assert.Equal("main", actions.DefaultBranch.Branch);
        Assert.Equal(["release"], actions.MissingBranches);
        var entry = restored.PullRequests.Value!.Items.Single();
        Assert.Equal("Fix <b>bug</b>", entry.PullRequest.Title);
        Assert.Equal(MergeState.Conflicting, entry.PullRequest.MergeState);
        Assert.Equal(RollupState.Pending, entry.Checks.Value!.Rollup.State);
        Assert.Equal(1, entry.Reviews.Value!.Approvals);
        Assert.Equal(new ReviewRequest(ReviewerKind.Team, "acme/core"), entry.Reviews.Value.PendingRequests.Single());
        Assert.Equal(ItemCount.AtLeast(1), restored.PullRequests.Value.OpenCount);
        Assert.Equal(ResourceAvailability.FeatureUnavailable, restored.Issues.Availability);
    }

    [Fact]
    public void Lost_access_removes_the_cached_content()
    {
        var at = _time.GetUtcNow();
        var cache = Cache();
        Save(cache, Full(Octo, 1, at));

        Save(cache, Full(Octo, 1, at).WithAccessLost(new ResourceError(ResourceErrorKind.NotFound, "gone", at)));

        Assert.Empty(Load(cache, Octo));
    }

    [Fact]
    public void Accounts_are_isolated_and_sign_out_clears_only_that_account()
    {
        var at = _time.GetUtcNow();
        var cache = Cache();
        Save(cache, Full(Octo, 1, at));
        Save(cache, Full(Other, 2, at) with { Metadata = Resource<RepositoryMetadata>.NotLoaded });
        cache.ForAccount(Octo).Put(new Uri("https://api.github.com/repositories/1"), new CachedResponse("\"a\"", "{}"));
        cache.ForAccount(Other).Put(new Uri("https://api.github.com/repositories/2"), new CachedResponse("\"b\"", "{}"));

        Assert.Equal([1L], Load(cache, Octo).Keys);
        Assert.Null(cache.ForAccount(Octo).Get(new Uri("https://api.github.com/repositories/2")));

        cache.ClearAccount(Octo);

        Assert.Empty(Load(cache, Octo));
        Assert.Null(Cache().ForAccount(Octo).Get(new Uri("https://api.github.com/repositories/1")));
        Assert.Equal([2L], Load(cache, Other).Keys);
        Assert.NotNull(Cache().ForAccount(Other).Get(new Uri("https://api.github.com/repositories/2")));
    }

    [Fact]
    public async Task Signing_out_removes_the_accounts_cached_content()
    {
        var cache = Cache();
        var kit = new AccountKit { Cache = cache }.Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        var account = kit.Accounts.Identity!.Account;
        Save(cache, Full(account, 1, _time.GetUtcNow()));
        cache.ForAccount(account).Put(new Uri("https://api.github.com/repositories/1"), new CachedResponse("\"a\"", "{}"));

        await kit.Accounts.SignOutAsync();

        Assert.Empty(Load(cache, account));
        Assert.Null(Cache().ForAccount(account).Get(new Uri("https://api.github.com/repositories/1")));
    }

    [Fact]
    public void A_version_1_database_is_upgraded_and_keeps_its_settings()
    {
        var path = Path.Combine(_directory, "old.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE settings (scope TEXT NOT NULL PRIMARY KEY, schema_version INTEGER NOT NULL, json TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE settings_backup (id INTEGER PRIMARY KEY AUTOINCREMENT, scope TEXT NOT NULL, json TEXT NOT NULL, reason TEXT NOT NULL, backed_up_at TEXT NOT NULL);
                INSERT INTO settings VALUES ('app', 1, '{}', '2026-10-01T00:00:00Z');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var database = new LocalDatabase(path);
        database.Initialize();

        using (var connection = database.Open())
        {
            Assert.Equal(LocalDatabase.LatestSchemaVersion, LocalDatabase.GetUserVersion(connection));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM settings;";
            Assert.Equal(1L, command.ExecuteScalar());
        }

        var cache = new RepositoryCache(database, _time, NullLogger<RepositoryCache>.Instance);
        Save(cache, Full(Octo, 1, _time.GetUtcNow()));
        Assert.Single(Load(cache, Octo));
    }

    [Fact]
    public void ETags_survive_a_restart()
    {
        var uri = new Uri("https://api.github.com/repositories/1");
        Cache().ForAccount(Octo).Put(uri, new CachedResponse("W/\"v1\"", """{"id":1}"""));

        Assert.Equal(new CachedResponse("W/\"v1\"", """{"id":1}"""), Cache().ForAccount(Octo).Get(uri));
    }

    [Fact]
    public void Retention_drops_unwatched_and_old_entries()
    {
        var cache = Cache();
        Save(cache, Full(Octo, 1, _time.GetUtcNow()));
        Save(cache, Full(Octo, 2, _time.GetUtcNow()));
        cache.ForAccount(Octo).Put(new Uri("https://api.github.com/old"), new CachedResponse("\"x\"", "{}"));
        _time.Advance(TimeSpan.FromDays(10));
        Save(cache, Full(Octo, 3, _time.GetUtcNow()));

        cache.ForAccount(Octo).Prune([Watch(1), Watch(3)], TimeSpan.FromDays(7), maxResponses: 2000);

        Assert.Equal([3L], Load(cache, Octo).Keys.Order()); // 1 is too old, 2 isn't watched
        Assert.Null(Cache().ForAccount(Octo).Get(new Uri("https://api.github.com/old")));
    }

    [Fact]
    public void An_unreadable_row_is_discarded()
    {
        Save(Cache(), Full(Octo, 1, _time.GetUtcNow()));
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE repository_snapshots SET json = '{not json';";
            command.ExecuteNonQuery();
        }

        Assert.Empty(Load(Cache(), Octo));
        Assert.Empty(Load(Cache(), Octo)); // and removed, not re-read every time
    }

    [Fact]
    public void A_snapshot_stored_under_the_wrong_repository_is_ignored()
    {
        Save(Cache(), Full(Octo, 1, _time.GetUtcNow()));
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE repository_snapshots SET repository_id = 99;";
            command.ExecuteNonQuery();
        }

        Assert.Empty(Load(Cache(), Octo));
    }

    [Fact]
    public void A_handle_from_before_sign_out_writes_nothing_afterwards()
    {
        var cache = Cache();
        var handle = cache.ForAccount(Octo);
        cache.ClearAccount(Octo);

        // A refresh that finishes after the clear (sign-out) can't bring private content back.
        handle.Save(Full(Octo, 1, _time.GetUtcNow()));
        handle.Put(Api("repositories/1"), new CachedResponse("\"a\"", "{}"));

        Assert.False(handle.IsCurrent);
        Assert.Empty(Load(cache, Octo));
        Assert.Equal(0, cache.ForAccount(Octo).StoredResponseCount());

        var next = cache.ForAccount(Octo); // the next sign-in writes normally
        next.Save(Full(Octo, 1, _time.GetUtcNow()));
        Assert.Single(Load(cache, Octo));
    }

    [Fact]
    public void The_in_memory_front_is_bounded()
    {
        var handle = Cache().ForAccount(Octo, memoryEntries: 3);
        for (var i = 0; i < 10; i++)
        {
            handle.Put(Api($"repos/octo/hello/commits/{i:x40}/check-runs"), new CachedResponse($"\"{i}\"", "{}"));
        }

        Assert.Equal(3, handle.MemoryCount);
        Assert.Equal("\"0\"", handle.Get(Api($"repos/octo/hello/commits/{0:x40}/check-runs"))!.ETag); // evicted from memory, still on disk
        Assert.Equal(3, handle.MemoryCount);
    }

    [Fact]
    public void Removing_a_repository_forgets_its_cached_responses()
    {
        var cache = Cache();
        var handle = cache.ForAccount(Octo);
        Save(cache, Full(Octo, 1, _time.GetUtcNow()));
        handle.Put(Api("repositories/1"), new CachedResponse("\"a\"", "{}"));
        handle.Put(Api("repos/octo/hello/actions/runs?per_page=20"), new CachedResponse("\"b\"", "{}"));
        handle.Put(Api("repos/octo/hello-world/actions/runs?per_page=20"), new CachedResponse("\"c\"", "{}"));

        handle.Forget(1, "octo", "hello");

        Assert.Empty(Load(cache, Octo));
        var fresh = cache.ForAccount(Octo);
        Assert.Null(fresh.Get(Api("repositories/1")));
        Assert.Null(fresh.Get(Api("repos/octo/hello/actions/runs?per_page=20")));
        Assert.NotNull(fresh.Get(Api("repos/octo/hello-world/actions/runs?per_page=20"))); // another repository
    }

    [Fact]
    public void Pruning_caps_responses_and_drops_those_of_unwatched_repositories()
    {
        var cache = Cache();
        var handle = cache.ForAccount(Octo);
        for (var i = 0; i < 6; i++)
        {
            handle.Put(Api($"repos/octo/hello/commits/{i:x40}/status?per_page=100"), new CachedResponse($"\"{i}\"", "{}"));
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        handle.Put(Api("repos/octo/gone/actions/runs?per_page=20"), new CachedResponse("\"g\"", "{}"));

        handle.Prune([Watch(1)], TimeSpan.FromDays(30), maxResponses: 4);

        var fresh = cache.ForAccount(Octo);
        Assert.Equal(4, fresh.StoredResponseCount());
        Assert.Null(fresh.Get(Api("repos/octo/gone/actions/runs?per_page=20")));
        Assert.Null(fresh.Get(Api($"repos/octo/hello/commits/{0:x40}/status?per_page=100"))); // oldest first out
        Assert.NotNull(fresh.Get(Api($"repos/octo/hello/commits/{5:x40}/status?per_page=100")));
    }
}