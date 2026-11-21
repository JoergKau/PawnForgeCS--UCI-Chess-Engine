using System.Text;
using PawnForge.UCI;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// UCI integration of the opening book: the Own Book / Book File / Book Depth
/// options and the book-move bestmove emitted from `go` before any search.
/// </summary>
public class BookUciTests : IDisposable
{
    private readonly EngineOrchestrator _orchestrator = new();
    private readonly LockedTextWriter _out = new();
    private readonly bool _bookLoaded;

    public BookUciTests()
    {
        // Load the checked-in book explicitly (the test harness working dir
        // is not where the engine looks by default). When the third-party
        // book is absent (CI), the book tests no-op.
        var bookPath = TestHelper.FindBookPath();
        _bookLoaded = bookPath != null;
        if (bookPath != null)
            _orchestrator.LoadBook(bookPath);
    }

    public void Dispose()
    {
        _orchestrator.Stop();
    }

    private static string WaitFor(Func<string> text, string needle, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string t;
        do
        {
            Thread.Sleep(20);
            t = text();
        } while (!t.Contains(needle, StringComparison.Ordinal) && sw.ElapsedMilliseconds < timeoutMs);
        return t;
    }

    [Fact]
    public void BookMove_IsPlayedAsBestmove_InsteadOfSearch()
    {
        if (!_bookLoaded) return;
        Assert.True(_orchestrator.BookStatus.Contains("loaded"), _orchestrator.BookStatus);

        // `Own Book` defaults OFF (CCRL-ready); enable it for this test.
        _orchestrator.SetOption("Own Book", "true", _out);
        _orchestrator.SetPosition("startpos", Array.Empty<string>(), _out);
        _orchestrator.Go(UciParser.ParseGoCommand("go movetime 1000"), _out);

        string text = WaitFor(() => _out.Text, "bestmove");
        Assert.Contains("info string book move", text);
        var best = text.Split('\n').First(l => l.StartsWith("bestmove"));
        string[] bookMoves =
        {
            "e2e4", "g1f3", "d2d4", "c2c4", "f2f4", "c2c3",
            "e2e3", "g2g3", "b2b3", "a2a3", "h2h3", "b1c3"
        };
        // Trim: AppendLine emits Environment.NewLine (\r\n on Windows), which
        // leaves a trailing \r on the last line when splitting on '\n'.
        Assert.Contains(best.Substring("bestmove ".Length).Trim(), bookMoves); // book move, no search
    }

    [Fact]
    public void BookDisabled_OwnBookOff_RunsNormalSearch()
    {
        if (!_bookLoaded) return;
        _orchestrator.SetOption("Own Book", "false", _out);
        _orchestrator.SetPosition("startpos", Array.Empty<string>(), _out);
        _orchestrator.Go(UciParser.ParseGoCommand("go movetime 300"), _out);

        string text = WaitFor(() => _out.Text, "bestmove");
        Assert.DoesNotContain("info string book move", text);
        var best = text.Split('\n').First(l => l.StartsWith("bestmove"));
        Assert.NotEqual("bestmove 0000", best);
    }

    [Fact]
    public void BookDepth_Exceeded_FallsBackToSearch()
    {
        // Book Depth 1 (ply): after 1.e4 the position is 1 ply in, so the book
        // must not be used and the engine searches normally.
        if (!_bookLoaded) return;
        // Book must be enabled for the depth gate to be exercised.
        _orchestrator.SetOption("Own Book", "true", _out);
        _orchestrator.SetOption("Book Depth", "1", _out);
        _orchestrator.SetPosition("startpos", new[] { "e2e4" }, _out);
        _orchestrator.Go(UciParser.ParseGoCommand("go movetime 300"), _out);

        string text = WaitFor(() => _out.Text, "bestmove");
        Assert.DoesNotContain("info string book move", text);
        Assert.Contains("info depth", text); // a real search ran
    }

    [Fact]
    public void BookFileOption_LoadsTheBook()
    {
        var bookPath = TestHelper.FindBookPath();
        if (bookPath == null) return;
        _orchestrator.SetOption("Book File", bookPath, _out);
        Assert.Contains("loaded", _orchestrator.BookStatus);
    }
    private sealed class LockedTextWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get { lock (_gate) return _sb.ToString(); }
        }

        public override void Write(char value) { lock (_gate) _sb.Append(value); }
        public override void Write(string? value) { lock (_gate) _sb.Append(value); }
        public override void WriteLine(string? value) { lock (_gate) _sb.AppendLine(value); }
        public override void WriteLine() { lock (_gate) _sb.AppendLine(); }
    }
}
