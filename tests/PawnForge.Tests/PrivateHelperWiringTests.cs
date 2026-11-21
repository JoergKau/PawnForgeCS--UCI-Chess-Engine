using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Architecture guard: a PRIVATE helper that is declared but never called in
/// its own file ("implemented but not wired") must fail the suite.
///
/// Why a test and not a build flag: C# has no build-time warning for
/// uncalled private methods (IDE0051 is IDE-only, not enforced by
/// `dotnet build`), and a `#if DEBUG` assertion INSIDE the method cannot
/// fire when the method is never called. This test greps the
/// `src/PawnForge.Core/Evaluation/*.cs` sources instead: for every
/// `private` method declaration the name must occur at least twice in the
/// comment-stripped file (declaration + at least one use). Comments are
/// stripped first, so XML <see cref="..."/> mentions cannot mask a miss.
///
/// Legitimate exceptions (a private helper kept on purpose, e.g. awaiting a
/// retired-module revival) go into the allowlist below WITH a reason — an
/// allowlist entry without a documented reason is a bug, not a convenience.
/// </summary>
public class PrivateHelperWiringTests
{
    /// <summary>(file, member) pairs deliberately exempt from the wiring
    /// check. Currently EMPTY — every private helper in the evaluation
    /// modules is called. Re-add entries only with a reason comment.</summary>
    private static readonly HashSet<(string File, string Name)> Allowlist = new();

    private static IEnumerable<string> EvaluationSourceFiles()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null &&
               !Directory.Exists(Path.Combine(dir, "src", "PawnForge.Core", "Evaluation")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        // Repo-layout guard: if this fails the test ran from an unexpected
        // working tree, not that the sources are unwired.
        Assert.NotNull(dir);
        return Directory.EnumerateFiles(
            Path.Combine(dir!, "src", "PawnForge.Core", "Evaluation"), "*.cs");
    }

    [Fact]
    public void PrivateHelpers_AreReferenced_InTheirFile()
    {
        var violations = new List<string>();

        foreach (string path in EvaluationSourceFiles())
        {
            string file = Path.GetFileName(path);

            // Strip comments (full-line // and /// first, then trailing //)
            // so <see cref="..."/> and prose mentions cannot satisfy the
            // "referenced" requirement.
            var strippedLines = File.ReadAllLines(path)
                .Select(l =>
                {
                    int c = l.IndexOf("//", StringComparison.Ordinal);
                    return c >= 0 ? l[..c] : l;
                });
            string stripped = string.Join('\n', strippedLines);

            int lineNo = 0;
            foreach (string line in File.ReadAllLines(path))
            {
                lineNo++;
                string code = line;
                int c = code.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) code = code[..c];
                if (!code.Contains("private")) continue;

                // Candidate name = the LAST identifier directly followed by
                // '(' on the declaration line. Field initializers are
                // excluded (an '=' before the candidate means this is an
                // assignment, not a method signature).
                var candidates = System.Text.RegularExpressions.Regex
                    .Matches(code, @"(\w+)\s*\(");
                if (candidates.Count == 0) continue;
                string name = candidates[^1].Groups[1].Value;
                int nameCol = candidates[^1].Index;
                int eq = code.IndexOf('=');
                if (eq >= 0 && eq < nameCol) continue; // field initializer

                if (Allowlist.Contains((file, name))) continue;

                // ≥2 occurrences = declaration + at least one use.
                if (System.Text.RegularExpressions.Regex
                    .Matches(stripped, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b").Count < 2)
                {
                    violations.Add($"{file}:{lineNo} — private method '{name}' is never called in its own file (unwired-private-helper class)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Uncalled private helpers found (implemented-but-not-wired):\n  " +
            string.Join("\n  ", violations));
    }
}
