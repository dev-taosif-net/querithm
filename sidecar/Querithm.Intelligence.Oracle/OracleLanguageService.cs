using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.Oracle;

/// <summary>
/// Oracle (Phase D4, hidden engine) before its tokenizer exists (D4 S02): nothing here reads SQL text. Every statement is
/// refused through <see cref="Classification.Refusal"/>, a hard stop the sidecar enforces whatever the UI sends, because
/// without the tokenizer Querithm cannot tell a destructive statement, a PL/SQL block or a client command apart.
/// Completion, diagnostics, signature help and folding are empty; formatting returns the text unchanged with the reason;
/// secret redaction masks the whole text, since no literal can be told from another yet.
/// </summary>
public sealed class OracleLanguageService : IDialectService
{
    /// <summary>Shown wherever Oracle SQL would be read, until D4 S02 lands the tokenizer, splitter and classifier.</summary>
    public const string NotReadYet = "Oracle SQL is not read by this build of Querithm yet: statements cannot be checked, so none are run.";

    const string Redacted = "<redacted>";

    public Classification Classify(string text)
    {
        text ??= "";
        return new Classification
        {
            Destructive = [],
            Batches = [new BatchInfo { Index = 0, StartLine = 1, EndLine = LineCount(text), StartOffset = 0, Length = text.Length }],
            ParseErrors = [],
            Refusal = NotReadYet,
        };
    }

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema) => [];
    public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];
    public FormatResult Format(FormatParams p) => new() { Text = p.Text, Error = NotReadYet };
    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema) => new() { Signatures = [] };
    public FoldingRange[] Folding(FoldingParams p) => [];

    /// <summary>
    /// The whole text is masked: without tokens a <c>CREATE USER … IDENTIFIED BY</c> password cannot be found, and text a
    /// secret might hide in is never kept (history, the monitor's text store, L4).
    /// </summary>
    public Redaction RedactSecrets(string text) => string.IsNullOrEmpty(text) ? new Redaction(text ?? "", []) : new Redaction(Redacted, []);

    public void Forget(string docId) { }

    /// <summary>Counts line breaks (\n, \r\n, lone \r): a character count, not a reading of SQL.</summary>
    static int LineCount(string text)
    {
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lines++;
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) lines++;
        }
        return lines;
    }
}
