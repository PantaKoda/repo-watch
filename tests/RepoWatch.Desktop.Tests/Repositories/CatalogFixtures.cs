namespace RepoWatch.Desktop.Tests.Repositories;

/// <summary>Synthetic GitHub responses in the documented shapes for installation and repository listings.</summary>
internal static class CatalogFixtures
{
    public static string Installation(long id, string login, string type = "User", string? suspendedAt = null) =>
        $$$"""{"id":{{{id}}},"account":{"login":"{{{login}}}","id":{{{id * 10}}},"type":"{{{type}}}"},"target_type":"{{{type}}}","target_id":{{{id * 10}}},"repository_selection":"selected","suspended_at":{{{(suspendedAt is null ? "null" : $"\"{suspendedAt}\"")}}},"html_url":"https://github.com/settings/installations/{{{id}}}","permissions":{"metadata":"read","actions":"read","checks":"read","statuses":"read","issues":"read","pull_requests":"read"}}""";

    public static string Repository(long id, string owner, string name, bool isPrivate = false, string ownerType = "User", bool archived = false) =>
        $$"""{"id":{{id}},"name":"{{name}}","owner":{"login":"{{owner}}","id":1,"type":"{{ownerType}}"},"private":{{(isPrivate ? "true" : "false")}},"archived":{{(archived ? "true" : "false")}},"default_branch":"main","html_url":"https://github.com/{{owner}}/{{name}}","description":"Synthetic fixture for {{name}}"}""";

    public static string InstallationsJson(params string[] items) => $$"""{"total_count":{{items.Length}},"installations":[{{string.Join(",", items)}}]}""";

    public static string RepositoriesJson(params string[] items) => $$"""{"total_count":{{items.Length}},"repositories":[{{string.Join(",", items)}}]}""";

    /// <summary>Queues a personal installation (two private/public repos) and an organization installation.</summary>
    public static void QueueTypicalCatalog(QueueHandler http)
    {
        http.Json(InstallationsJson(Installation(1, "octo-test"), Installation(2, "acme-org", "Organization")))
            .Json(RepositoriesJson(Repository(11, "octo-test", "dotfiles", isPrivate: true), Repository(12, "octo-test", "blog")))
            .Json(RepositoriesJson(Repository(21, "acme-org", "api", isPrivate: true, ownerType: "Organization"),
                Repository(22, "acme-org", "web", ownerType: "Organization"),
                Repository(23, "acme-org", "legacy", ownerType: "Organization", archived: true)));
    }

    public static async Task<AccountKit> SignedInWithCatalogAsync()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        QueueTypicalCatalog(kit.Http);
        await kit.Catalog.RefreshAsync();
        return kit;
    }
}
