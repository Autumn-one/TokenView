namespace TokenView.Wpf.Services;

public sealed record RepoConfig(string Owner, string Repo, string Branch, string File)
{
    public bool HasRepository => !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repo);
}
