using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// Tier-2 T-SQL intelligence on ScriptDom (§9). One parse per document version, shared by every lang.* call. Every
/// call that parses or walks a tree runs on a deep stack (<see cref="DeepStack"/>): ScriptDom recurses, and a long OR or
/// UNION ALL chain overflowed a thread-pool thread's stack and ended the sidecar.
/// </summary>
public sealed class TSqlLanguageService : IDialectService
{
    public Classification Classify(string text) => DeepStack.Run(() => Classifier.Classify(TSqlParsing.Parse(text)));

    public Redaction RedactSecrets(string text) => TSqlRedaction.Redact(text);

    public void Forget(string docId)
    {
        TSqlParsing.Forget(docId);
        Completion.DocModel.Forget(docId);
    }

    /// <summary>
    /// A large text's chunks parse on their own deep stacks (one per worker), so the chunked path runs outside one: a
    /// deep thread held only to wait for the workers would reserve another 256 MiB for nothing (review 2026-11-26).
    /// </summary>
    public Diagnostic[] Diagnostics(DiagnosticsParams p) => ChunkedDiagnostics.TryDiagnose(p.Text) ?? DeepStack.Run(() =>
    {
        var parsed = TSqlParsing.Parse(p.Text, p.DocId);
        return parsed.Errors.Select(e =>
        {
            var len = TokenLengthAt(parsed, e.Offset);
            return new Diagnostic
            {
                Line = e.Line, Column = e.Column, EndLine = e.Line, EndColumn = e.Column + Math.Max(1, len), Message = e.Message, Severity = Severity.Error,
            };
        }).ToArray();
    });

    static int TokenLengthAt(ParsedScript parsed, int offset)
    {
        foreach (var t in parsed.Tokens)
            if (t.Offset == offset) return t.Text?.Split('\n')[0].Length ?? 1;
        return 1;
    }

    public FormatResult Format(FormatParams p) => DeepStack.Run(() => Formatting.Format(p.Text, p.Options));

    public FoldingRange[] Folding(FoldingParams p) => DeepStack.Run(() => TSql.Folding.Ranges(TSqlParsing.Parse(p.Text, p.DocId)));

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema, CancellationToken ct) => DeepStack.Run(() => Completion.TSqlCompletion.Complete(p, schema, ct));

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema, CancellationToken ct) => Completion.TSqlSignature.Help(p, schema, ct);
}
