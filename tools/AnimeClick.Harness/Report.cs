namespace AnimeClick.Harness;

internal enum Severity
{
    /// <summary>Wrong data would reach the library.</summary>
    Error,

    /// <summary>Data would be missing, or accepted on weak evidence.</summary>
    Warning,

    /// <summary>Worth a human glance, not necessarily wrong.</summary>
    Note
}

internal sealed record Finding(Severity Severity, string Code, string Message)
{
    public override string ToString()
    {
        var mark = Severity switch
        {
            Severity.Error => "ERRORE ",
            Severity.Warning => "AVVISO ",
            _ => "NOTA   "
        };

        return $"  {mark} [{Code}] {Message}";
    }
}

internal sealed class AnimeReport
{
    public required string AnimeClickId { get; init; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    public int? DeclaredEpisodeCount { get; set; }

    public int DeclaredSeasonsCount { get; set; }

    public int PagesFetched { get; set; }

    public int RowsParsed { get; set; }

    public int RegularEpisodes { get; set; }

    public int Specials { get; set; }

    public List<Finding> Findings { get; } = [];

    public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);

    public bool HasWarnings => Findings.Any(f => f.Severity == Severity.Warning);

    public void Add(Severity severity, string code, string message)
        => Findings.Add(new Finding(severity, code, message));
}
