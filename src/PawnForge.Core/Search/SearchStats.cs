namespace PawnForge.Core.Search;

/// <summary>
/// Search instrumentation. Counters are
/// per-thread and only incremented when <see cref="SearchDiagnostics.Enabled"/>
/// is on, so the production search path is untouched by default.
///
/// The first profile answers the three questions that decide where the ~268
/// Elo search gap lives:
/// * how much of the tree is quiescence search (QNodes / Nodes),
/// * how often the TT is probed and hit (TtProbes / TtHits),
/// * how early the cutoff move is found (CutoffIndex histogram).
/// </summary>
public sealed class SearchStats
{
    public long Nodes;
    public long QNodes;
    public long TtProbes;
    public long TtHits;
    public long QTtProbes;
    public long QTtHits;
    public long Cutoffs;
    public long QCutoffs;

    /// <summary>Cutoff move index (0 = first legal move tried). Index 15 is "15+".</summary>
    public readonly long[] CutoffIndex = new long[16];

    /// <summary>Qsearch cutoff move index (0 = first legal move tried).</summary>
    public readonly long[] QCutoffIndex = new long[16];

    public void Reset()
    {
        Nodes = QNodes = TtProbes = TtHits = QTtProbes = QTtHits = Cutoffs = QCutoffs = 0;
        Array.Clear(CutoffIndex);
        Array.Clear(QCutoffIndex);
    }

    public void Add(SearchStats other)
    {
        Nodes += other.Nodes;
        QNodes += other.QNodes;
        TtProbes += other.TtProbes;
        TtHits += other.TtHits;
        QTtProbes += other.QTtProbes;
        QTtHits += other.QTtHits;
        Cutoffs += other.Cutoffs;
        QCutoffs += other.QCutoffs;
        for (int i = 0; i < CutoffIndex.Length; i++)
        {
            CutoffIndex[i] += other.CutoffIndex[i];
            QCutoffIndex[i] += other.QCutoffIndex[i];
        }
    }

    public void RecordQNode() => QNodes++;

    public void RecordTtProbe(bool hit)
    {
        TtProbes++;
        if (hit) TtHits++;
    }

    public void RecordQTtProbe(bool hit)
    {
        QTtProbes++;
        if (hit) QTtHits++;
    }

    public void RecordCutoff(int legalMoveIndex)
    {
        Cutoffs++;
        CutoffIndex[Math.Min(legalMoveIndex, CutoffIndex.Length - 1)]++;
    }

    public void RecordQCutoff(int legalMoveIndex)
    {
        QCutoffs++;
        QCutoffIndex[Math.Min(legalMoveIndex, QCutoffIndex.Length - 1)]++;
    }
}

/// <summary>Global switch for the P0 search counters. Off by default; the
/// `searchstats reset` UCI command turns it on for the next search.</summary>
public static class SearchDiagnostics
{
    public static bool Enabled;
}
