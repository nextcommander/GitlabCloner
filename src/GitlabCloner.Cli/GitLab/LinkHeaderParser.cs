namespace GitlabCloner.Cli.GitLab;

/// <summary>
/// Extracts the URL for a given rel value from an RFC 5988 Link header.
/// Example: &lt;https://host/api/v4/projects?id_after=42&gt;; rel="next", &lt;...&gt;; rel="first"
/// </summary>
public static class LinkHeaderParser
{
    public static string? FindRel(string? headerValue, string rel)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        foreach (var part in SplitLinks(headerValue))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                continue;
            }

            var url = segments[0];
            if (!url.StartsWith('<') || !url.EndsWith('>'))
            {
                continue;
            }

            var matches = segments.Skip(1).Any(parameter =>
            {
                var equals = parameter.IndexOf('=');
                if (equals < 0)
                {
                    return false;
                }

                var name = parameter[..equals].Trim();
                var value = parameter[(equals + 1)..].Trim().Trim('"');
                return name.Equals("rel", StringComparison.OrdinalIgnoreCase) &&
                       value.Equals(rel, StringComparison.OrdinalIgnoreCase);
            });

            if (matches)
            {
                return url[1..^1];
            }
        }

        return null;
    }

    /// <summary>Splits on commas, but not on commas inside &lt;...&gt; (query strings).</summary>
    private static IEnumerable<string> SplitLinks(string headerValue)
    {
        var start = 0;
        var insideUrl = false;

        for (var i = 0; i < headerValue.Length; i++)
        {
            switch (headerValue[i])
            {
                case '<':
                    insideUrl = true;
                    break;
                case '>':
                    insideUrl = false;
                    break;
                case ',' when !insideUrl:
                    yield return headerValue[start..i].Trim();
                    start = i + 1;
                    break;
            }
        }

        if (start < headerValue.Length)
        {
            yield return headerValue[start..].Trim();
        }
    }
}
