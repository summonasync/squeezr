namespace Squeezr.Cli.RankEval;

public sealed record EvalQueryLine(
    string Kind,
    string Query,
    double Mrr,
    double P5,
    double Recall,
    IReadOnlyDictionary<string, int> Ranks);

public sealed record EvalSummaryLine(
    string Kind,
    int Queries,
    double Mrr,
    double P5,
    double Recall,
    bool SemanticDegraded);
