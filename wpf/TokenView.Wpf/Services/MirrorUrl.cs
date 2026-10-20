namespace TokenView.Wpf.Services;

internal static class MirrorUrl
{
    public static string? Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var result = template.Trim();
        foreach (var pair in values)
        {
            var token = "{" + pair.Key + "}";
            if (!result.Contains(token, StringComparison.Ordinal))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                return null;
            }
            result = result.Replace(token, pair.Value.Trim(), StringComparison.Ordinal);
        }

        if (result.Contains('{') || result.Contains('}'))
        {
            return null;
        }
        return IsHttp(result) ? result : null;
    }

    public static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
