using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql;

/// <summary>Tier-2 T-SQL intelligence on ScriptDom (§9). One parse per document version, shared by every lang.* call.</summary>
public sealed class TSqlLanguageService : IDialectService
{
    /// <summary>§9: if a full parse exceeds 100 ms, completion parses only the enclosing batch.</summary>
    public const int FullParseBudgetMs = 100;

    public Classification Classify(string text) => Classifier.Classify(TSqlParsing.Parse(text));

    public Redaction RedactSecrets(string text) => TSqlRedaction.Redact(text);

    public void Forget(string docId)
    {
        TSqlParsing.Forget(docId);
        Completion.DocModel.Forget(docId);
    }

    public Diagnostic[] Diagnostics(DiagnosticsParams p)
    {
        if (ChunkedDiagnostics.TryDiagnose(p.Text) is { } chunked) return chunked;
        var parsed = TSqlParsing.Parse(p.Text, p.DocId);
        return parsed.Errors.Select(e =>
        {
            var len = TokenLengthAt(parsed, e.Offset);
            return new Diagnostic
            {
                Line = e.Line, Column = e.Column, EndLine = e.Line, EndColumn = e.Column + Math.Max(1, len), Message = e.Message, Severity = Severity.Error,
            };
        }).ToArray();
    }

    static int TokenLengthAt(ParsedScript parsed, int offset)
    {
        foreach (var t in parsed.Tokens)
            if (t.Offset == offset) return t.Text?.Split('\n')[0].Length ?? 1;
        return 1;
    }

    public FormatResult Format(FormatParams p) => Formatting.Format(p.Text, p.Options);

    public FoldingRange[] Folding(FoldingParams p) => TSql.Folding.Ranges(TSqlParsing.Parse(p.Text, p.DocId));

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema) => Completion.TSqlCompletion.Complete(p, schema);

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema) => Completion.TSqlSignature.Help(p, schema);
}
