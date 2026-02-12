// Models/DocAnnotation.cs
using System;

namespace CbetaTranslator.App.Models;

public sealed class DocAnnotation
{
    public int Start { get; }
    public int EndExclusive { get; }
    public string Text { get; }
    public string? Kind { get; }

    public DocAnnotation(int start, int endExclusive, string text, string? kind = null)
    {
        Start = Math.Max(0, start);
        EndExclusive = Math.Max(Start, endExclusive);
        Text = text ?? "";
        Kind = kind;
    }
}
