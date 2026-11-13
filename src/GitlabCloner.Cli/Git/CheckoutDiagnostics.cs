using System.Text.RegularExpressions;

namespace GitlabCloner.Cli.Git;

/// <summary>
/// Interprets git clone output.
///
/// On Windows it is common for a clone to download all objects and refs while only
/// the working tree cannot be written: paths Windows cannot represent (names ending
/// in a space or a dot, reserved device names, characters such as ':'), overly long
/// paths, file locks. In that case git prints:
///
///     error: invalid path 'docs/Ipa &amp; Apk /Build APK.docx'
///     fatal: unable to checkout working tree
///     warning: Clone succeeded, but checkout failed.
///
/// The last line is git's way of saying "the data is complete, the working tree is
/// not", and it is printed whatever the cause. It means the repository is recoverable.
/// </summary>
public static partial class CheckoutDiagnostics
{
    private const string CheckoutFailedMarker = "Clone succeeded, but checkout failed";

    [GeneratedRegex(@"invalid path '([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidPathPattern();

    /// <summary>Paths git reported by name that cannot be created on Windows.</summary>
    public static IReadOnlyList<string> InvalidPaths(GitResult result) => InvalidPaths(Combine(result));

    public static IReadOnlyList<string> InvalidPaths(string? gitOutput)
    {
        if (string.IsNullOrEmpty(gitOutput))
        {
            return [];
        }

        return InvalidPathPattern().Matches(gitOutput)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Did the failure happen only during checkout? If so, the git data (objects and
    /// all refs) is complete; the repository should be recovered instead of discarded.
    /// </summary>
    public static bool IsCheckoutOnlyFailure(GitResult result)
    {
        var output = Combine(result);
        return output.Contains(CheckoutFailedMarker, StringComparison.OrdinalIgnoreCase) ||
               InvalidPaths(output).Count > 0;
    }

    private static string Combine(GitResult result) =>
        result.StandardError + "\n" + result.StandardOutput;
}
