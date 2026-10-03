namespace RepoWatch.Core.Configuration;

/// <summary>A configuration problem with the setting key that caused it and how to fix it.</summary>
public sealed record ConfigurationError(string Key, string Problem, string Fix)
{
    public override string ToString() => $"{Key}: {Problem} {Fix}";
}
