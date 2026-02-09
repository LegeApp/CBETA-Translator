using System;
using System.Collections.Generic;

namespace CbetaTranslator.App.Models;

public sealed class IndexCache
{
    public int Version { get; set; } = 1;

    // Absolute root path this cache belongs to (helps prevent accidental reuse)
    public string RootPath { get; set; } = "";

    // When the cache was built (informational)
    public DateTime BuiltUtc { get; set; } = DateTime.UtcNow;

    // The relative paths within xml-p5
    public List<string> RelativePaths { get; set; } = new();
}
