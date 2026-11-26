using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Metadata;
using static Querithm.Tests.CompletionBudgetTests;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 13 (completion audit 2026-11-25, S2): every lang.* call is bounded at the RPC boundary — a docId of 1–512
/// characters, a text of at most <see cref="LanguageService.MaxCompletionText"/> characters with the offset inside it, and
/// names of at most <see cref="LanguageService.MaxNameLength"/>. lang.format and lang.classify took any text until A#9.
/// </summary>
public sealed class LanguageRpcBoundsTests
{
    static readonly string TooLong = new('x', LanguageService.MaxCompletionText + 1);
    static readonly string LongName = new('n', LanguageService.MaxNameLength + 1);
    static readonly string LongDocId = new('d', LanguageService.MaxDocIdLength + 1);

    static async Task Invalid(Func<Task> call)
    {
        var e = await Assert.ThrowsAsync<QuerithmException>(call);
        Assert.Equal("invalid", e.Code);
    }

    [Fact]
    public async Task Lang_complete_and_signature_bound_the_document_the_offset_and_the_names()
    {
        var (svc, _) = Rpc();
        await Invalid(() => svc.CompleteAsync(Req("", "x")));
        await Invalid(() => svc.CompleteAsync(Req(LongDocId, "x")));
        await Invalid(() => svc.CompleteAsync(Req("d", null!)));
        await Invalid(() => svc.CompleteAsync(Req("d", TooLong)));
        await Invalid(() => svc.CompleteAsync(Req("d", "x") with { Offset = 2 }));
        await Invalid(() => svc.CompleteAsync(Req("d", "x") with { Offset = -1 }));
        await Invalid(() => svc.CompleteAsync(Req("d", "x") with { ProfileId = LongName }));
        await Invalid(() => svc.CompleteAsync(Req("d", "x") with { Database = LongName }));
        await Invalid(() => svc.CompleteAsync(Req("d", "x") with { SessionId = LongName }));
        await Invalid(() => svc.CompleteAsync(Req("d", "x") with { Schema = LongName }));
        Assert.NotEmpty(await svc.CompleteAsync(Req("d", "x"))); // within every bound: answered
        await svc.CompleteAsync(Req("d", "x") with { Offset = 1 }); // the offset may sit at the end of the text

        SignatureParams Sig(string doc, string text) => new() { DocId = doc, Text = text, Offset = 0, Dialect = Dialect.Tsql };
        await Invalid(() => svc.SignatureAsync(Sig("", "x")));
        await Invalid(() => svc.SignatureAsync(Sig(LongDocId, "x")));
        await Invalid(() => svc.SignatureAsync(Sig("d", TooLong)));
        await Invalid(() => svc.SignatureAsync(Sig("d", "x") with { Offset = 5 }));
        await Invalid(() => svc.SignatureAsync(Sig("d", "x") with { Database = LongName }));
    }

    [Fact]
    public async Task Lang_diagnostics_folding_and_forget_bound_the_document()
    {
        var (svc, _) = Rpc();
        await Invalid(() => svc.DiagnosticsAsync(new DiagnosticsParams { DocId = "", Text = "x", Dialect = Dialect.Tsql }));
        await Invalid(() => svc.DiagnosticsAsync(new DiagnosticsParams { DocId = LongDocId, Text = "x", Dialect = Dialect.Tsql }));
        await Invalid(() => svc.DiagnosticsAsync(new DiagnosticsParams { DocId = "d", Text = TooLong, Dialect = Dialect.Tsql }));
        await Invalid(() => svc.FoldingAsync(new FoldingParams { DocId = "", Text = "x", Dialect = Dialect.Tsql }));
        await Invalid(() => svc.FoldingAsync(new FoldingParams { DocId = "d", Text = TooLong, Dialect = Dialect.Tsql }));
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => svc.Forget("")).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => svc.Forget(LongDocId)).Code);
        Assert.Empty(await svc.DiagnosticsAsync(new DiagnosticsParams { DocId = "d", Text = "x", Dialect = Dialect.Tsql }));
    }

    [Fact]
    public async Task Lang_format_and_classify_bound_the_text_too()
    {
        // A#9: both took text of any length (and a null one) and handed it to the engine's parser.
        var (svc, _) = Rpc();
        await Invalid(() => svc.FormatAsync(new FormatParams { Text = TooLong, Dialect = Dialect.Tsql }));
        await Invalid(() => svc.FormatAsync(new FormatParams { Text = null!, Dialect = Dialect.Tsql }));
        await Invalid(() => svc.ClassifyAsync(new ClassifyParams { Text = TooLong, Dialect = Dialect.Tsql }));
        await Invalid(() => svc.ClassifyAsync(new ClassifyParams { Text = null!, Dialect = Dialect.Tsql }));
        // Within the bound the call reaches the engine (this fake engine refuses both: proof the boundary let it through).
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.FormatAsync(new FormatParams { Text = "select 1", Dialect = Dialect.Tsql }));
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.ClassifyAsync(new ClassifyParams { Text = "select 1", Dialect = Dialect.Tsql }));
    }
}
