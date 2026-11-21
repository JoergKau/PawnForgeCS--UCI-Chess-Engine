using System;
using System.IO;
using System.Linq;
using PawnForge.Core;
using PawnForge.UCI;

// Process entry point: with arguments, runs them as a one-shot UCI command line
// (a `quit` is appended so the engine exits afterwards); otherwise starts the
// interactive UCI loop.
//
// The former `inspect` subcommand was removed with the HCE modules: it printed
// HCE material values and an HCE startpos eval, and NNUE has no equivalent dump.

var handler = new UciHandler();

// [EXP-PARAM] `param:Name=Value` argv entries preload search-parameter
// overrides (same validated store as setoption; fail-fast on unknown names).
// Every other argv remains the one-shot UCI command line.
var paramSpecs = args.Where(a => a.StartsWith("param:", StringComparison.OrdinalIgnoreCase))
                     .Select(a => a["param:".Length..])
                     .ToArray();
var restArgs = args.Where(a => !a.StartsWith("param:", StringComparison.OrdinalIgnoreCase)).ToArray();
handler.PresetParams = paramSpecs;

if (restArgs.Length > 0)
{
    var commandLine = string.Join(" ", restArgs);
    // Append 'quit' so the engine exits after the command
    var input = commandLine + Environment.NewLine + "quit";
    using (var stringReader = new StringReader(input))
    {
        handler.Run(stringReader, Console.Out);
    }
}
else
{
    handler.Run(Console.In, Console.Out);
}
