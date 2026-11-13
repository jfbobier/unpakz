using Unpakz.Engine;

namespace Unpakz.Tests;

/// <summary>Executable spec for the single chokepoint that validates archive entry names.</summary>
public class PathGuardTests
{
    // ---- accepted ----
    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("dir/a.txt", "dir/a.txt")]
    [InlineData("dir\\sub\\a.txt", "dir/sub/a.txt")]   // backslashes normalized
    [InlineData("./a.txt", "a.txt")]                    // '.' segments dropped
    [InlineData("dir//a.txt", "dir/a.txt")]             // empty segments collapsed
    [InlineData("dir/.hidden", "dir/.hidden")]
    [InlineData("a..b/c", "a..b/c")]                    // '..' inside a name is fine
    [InlineData("console.txt", "console.txt")]          // not a reserved name
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("héllo/日本語.txt", "héllo/日本語.txt")]
    public void Accepts_safe_names(string input, string expected)
    {
        var r = PathGuard.Validate(input);
        Assert.True(r.IsSafe, r.Reason.ToString());
        Assert.Equal(expected, r.RelativePath);
    }

    [Fact]
    public void Directory_entry_with_trailing_slash_is_accepted_and_flagged()
    {
        var r = PathGuard.Validate("dir/sub/");
        Assert.True(r.IsSafe);
        Assert.True(r.IsDirectory);
        Assert.Equal("dir/sub", r.RelativePath);
    }

    // ---- traversal / absolute ----
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a/b/../../../evil.txt")]
    [InlineData("..")]
    [InlineData("a/..")]                                // refuse '..' outright, even if it would stay inside
    public void Rejects_parent_traversal(string input) =>
        Assert.Equal(PathRejection.Traversal, PathGuard.Validate(input).Reason);

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\x.dll")]
    [InlineData("C:\\windows\\x.dll")]
    [InlineData("C:/windows/x.dll")]
    [InlineData("c:evil.txt")]                          // drive-relative
    [InlineData("\\\\server\\share\\x")]                // UNC
    [InlineData("//server/share/x")]
    [InlineData("\\\\?\\C:\\x")]                        // extended-length prefix
    [InlineData("\\\\.\\PhysicalDrive0")]               // device namespace
    public void Rejects_absolute_and_rooted(string input) =>
        Assert.Equal(PathRejection.Rooted, PathGuard.Validate(input).Reason);

    // ---- Windows-specific hazards ----
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("dir/PRN")]
    [InlineData("aux.tar.gz")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT3")]
    [InlineData("COM\u00B9")]                           // superscript digits are reserved too
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    public void Rejects_reserved_device_names(string input) =>
        Assert.Equal(PathRejection.ReservedName, PathGuard.Validate(input).Reason);

    [Theory]
    [InlineData("file.txt:stream")]                     // alternate data stream
    [InlineData("file.txt::$DATA")]
    [InlineData("dir:evil/a.txt")]
    public void Rejects_colon_in_names(string input) =>
        Assert.Equal(PathRejection.InvalidCharacter, PathGuard.Validate(input).Reason);

    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a\0b")]
    [InlineData("a\u0001b")]
    [InlineData("a\u001Fb")]
    public void Rejects_invalid_filename_characters(string input) =>
        Assert.Equal(PathRejection.InvalidCharacter, PathGuard.Validate(input).Reason);

    [Theory]
    [InlineData("a.")]
    [InlineData("a ")]
    [InlineData("dir./a.txt")]
    [InlineData("dir /a.txt")]
    [InlineData("a/b...")]
    public void Rejects_trailing_dot_or_space(string input) =>
        Assert.Equal(PathRejection.TrailingDotOrSpace, PathGuard.Validate(input).Reason);

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("./")]
    public void Rejects_empty_results(string input)
    {
        var r = PathGuard.Validate(input);
        Assert.False(r.IsSafe);
        Assert.True(r.Reason is PathRejection.Empty or PathRejection.Rooted);
    }

    // ---- limits ----
    [Fact]
    public void Rejects_segment_longer_than_255()
    {
        Assert.True(PathGuard.Validate(new string('a', 255)).IsSafe);
        Assert.Equal(PathRejection.TooLong, PathGuard.Validate(new string('a', 256)).Reason);
    }

    [Fact]
    public void Rejects_total_length_over_limit()
    {
        var opts = new PathGuardOptions { MaxPathLength = 20 };
        Assert.True(PathGuard.Validate("aaaaaaaaaa/bbbbbbbb", opts).IsSafe);   // 19
        Assert.Equal(PathRejection.TooLong, PathGuard.Validate("aaaaaaaaaa/bbbbbbbbbbbb", opts).Reason);
    }

    [Fact]
    public void Rejects_excessive_depth()
    {
        var opts = new PathGuardOptions { MaxDepth = 3 };
        Assert.True(PathGuard.Validate("a/b/c", opts).IsSafe);
        Assert.Equal(PathRejection.TooDeep, PathGuard.Validate("a/b/c/d", opts).Reason);
    }

    // ---- invariants over many generated inputs ----
    [Fact]
    public void Safe_results_never_contain_dangerous_content()
    {
        var rng = new Random(12345);
        const string alphabet = "ab./\\:*?<>| \0CONcom1~\u00B9é";
        for (var i = 0; i < 200_000; i++)
        {
            var len = rng.Next(0, 24);
            var chars = new char[len];
            for (var j = 0; j < len; j++) chars[j] = alphabet[rng.Next(alphabet.Length)];
            var input = new string(chars);

            var r = PathGuard.Validate(input);
            if (!r.IsSafe) continue;

            Assert.DoesNotContain('\\', r.RelativePath);
            Assert.DoesNotContain(':', r.RelativePath);
            Assert.False(r.RelativePath.StartsWith('/'), input);
            foreach (var seg in r.RelativePath.Split('/'))
            {
                Assert.NotEmpty(seg);
                Assert.NotEqual(".", seg);
                Assert.NotEqual("..", seg);
                Assert.False(seg.EndsWith('.') || seg.EndsWith(' '), input);
            }
        }
    }

    [Fact]
    public void Validation_is_idempotent_for_safe_names()
    {
        foreach (var s in new[] { "a/b.txt", "dir\\x\\y", "./q/./r" })
        {
            var once = PathGuard.Validate(s);
            var twice = PathGuard.Validate(once.RelativePath);
            Assert.Equal(once.RelativePath, twice.RelativePath);
        }
    }
}
