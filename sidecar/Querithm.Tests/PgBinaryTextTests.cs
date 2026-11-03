using System.Buffers.Binary;
using System.Text;
using Querithm.Providers.Postgres;

namespace Querithm.Tests;

/// <summary>
/// psql-style text for PostgreSQL binary values Npgsql cannot read (user composites and arrays of them), and for
/// unconstrained numeric (NaN, ±Infinity, wider than System.Decimal). Wire bytes are built by hand from the
/// documented formats, so these run without a server.
/// </summary>
public class PgBinaryTextTests
{
    const uint Text = 25, Int4 = 23, Bool = 16, Numeric = 1700, Date = 1082, Timestamptz = 1184, Interval = 1186, Uuid = 2950, Jsonb = 3802, Bytea = 17;
    const uint UserEnumOid = 50_001, UserCompositeOid = 50_002;

    static byte[] I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    static byte[] I64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
    static byte[] I16(short v) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); return b; }
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>int32 count, then (oid, length or -1, bytes) per field.</summary>
    static byte[] Composite(params (uint Oid, byte[]? Value)[] fields) =>
        Cat([I32(fields.Length), .. fields.Select(f => f.Value == null ? Cat(U32(f.Oid), I32(-1)) : Cat(U32(f.Oid), I32(f.Value.Length), f.Value))]);

    /// <summary>One-dimensional array, lower bound 1.</summary>
    static byte[] Array1(uint elemOid, params byte[]?[] items) =>
        Cat([I32(1), I32(items.Any(i => i == null) ? 1 : 0), U32(elemOid), I32(items.Length), I32(1),
            .. items.Select(i => i == null ? I32(-1) : Cat(I32(i.Length), i))]);

    static byte[] NumericBytes(short weight, ushort sign, short dscale, params short[] digits) =>
        Cat([I16((short)digits.Length), I16(weight), I16(unchecked((short)sign)), I16(dscale), .. digits.Select(I16)]);

    [Fact]
    public void Composite_renders_like_psql_with_nulls_empty_and_quoting()
    {
        var addr = Composite((Text, Utf8("1 Main St")), (Text, null), (Text, Utf8("Dhaka")), (Text, Utf8("")), (Text, Utf8("BD")));
        Assert.Equal("(\"1 Main St\",,Dhaka,\"\",BD)", PgBinaryText.Render(addr, isArray: false));
    }

    [Fact]
    public void Composite_fields_of_built_in_types_and_quotes_inside_values()
    {
        var c = Composite((Int4, I32(-7)), (Bool, [1]), (Text, Utf8("say \"hi\", (ok)\\")));
        Assert.Equal("(-7,t,\"say \"\"hi\"\", (ok)\\\\\")", PgBinaryText.Render(c, isArray: false));
    }

    [Fact]
    public void Nested_composites_and_user_enums_inside_a_composite()
    {
        var inner = Composite((Text, Utf8("a")), (Int4, I32(1)));
        var outer = Composite((UserCompositeOid, inner), (UserEnumOid, Utf8("shipped")));
        Assert.Equal("(\"(a,1)\",shipped)", PgBinaryText.Render(outer, isArray: false));
    }

    [Fact]
    public void Arrays_of_composites_quote_each_element_and_keep_nulls()
    {
        var a = Array1(UserCompositeOid, Composite((Text, Utf8("a")), (Text, null)), null);
        Assert.Equal("{\"(a,)\",NULL}", PgBinaryText.Render(a, isArray: true));
    }

    [Fact]
    public void Empty_and_multi_dimensional_arrays()
    {
        Assert.Equal("{}", PgBinaryText.Render(Cat(I32(0), I32(0), U32(Int4)), isArray: true));
        var twoByTwo = Cat(I32(2), I32(0), U32(Int4), I32(2), I32(1), I32(2), I32(1), I32(4), I32(1), I32(4), I32(2), I32(4), I32(3), I32(4), I32(4));
        Assert.Equal("{{1,2},{3,4}}", PgBinaryText.Render(twoByTwo, isArray: true));
    }

    [Fact]
    public void Array_elements_that_need_quotes()
    {
        var a = Array1(Text, Utf8("with space"), Utf8("NULL"), Utf8(""), Utf8("a\"b"), Utf8("plain"));
        Assert.Equal("{\"with space\",\"NULL\",\"\",\"a\\\"b\",plain}", PgBinaryText.Render(a, isArray: true));
    }

    [Theory]
    [InlineData((short)0, (ushort)0xC000, (short)0, new short[0], "NaN")]
    [InlineData((short)0, (ushort)0xD000, (short)0, new short[0], "Infinity")]
    [InlineData((short)0, (ushort)0xF000, (short)0, new short[0], "-Infinity")]
    [InlineData((short)1, (ushort)0, (short)15, new short[] { 1, 2345, 6789, 123, 4567, 8900 }, "12345.678901234567890")]
    [InlineData((short)-1, (ushort)0x4000, (short)4, new short[] { 5 }, "-0.0005")]
    [InlineData((short)2, (ushort)0, (short)0, new short[] { 1 }, "100000000")]
    [InlineData((short)0, (ushort)0, (short)2, new short[0], "0.00")]
    public void Numeric_is_exact_including_special_values(short weight, ushort sign, short dscale, short[] digits, string expected) =>
        Assert.Equal(expected, PgBinaryText.Value(Numeric, NumericBytes(weight, sign, dscale, digits)));

    [Fact]
    public void Numeric_wider_than_decimal()
    {
        // -123456789012345678901234567890.0001: what Npgsql cannot read into System.Decimal.
        var bytes = Convert.FromHexString("0009000740000004000C0D801ED204D2162E23340D801ED20001");
        Assert.Equal("-123456789012345678901234567890.0001", PgBinaryText.Value(Numeric, bytes));
    }

    [Fact]
    public void Dates_times_intervals_and_other_built_ins()
    {
        Assert.Equal("2026-01-01", PgBinaryText.Value(Date, I32(9497)));
        Assert.Equal("infinity", PgBinaryText.Value(Date, I32(int.MaxValue)));
        Assert.Equal("2000-01-01 00:00:01.5+00", PgBinaryText.Value(Timestamptz, I64(1_500_000)));
        Assert.Equal("1 year 2 mons 3 days 04:05:06", PgBinaryText.Value(Interval, Cat(I64(14_706_000_000), I32(3), I32(14))));
        Assert.Equal("a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11", PgBinaryText.Value(Uuid, Convert.FromHexString("A0EEBC999C0B4EF8BB6D6BB9BD380A11")));
        Assert.Equal("{\"a\": 1}", PgBinaryText.Value(Jsonb, Cat([1], Utf8("{\"a\": 1}"))));
        Assert.Equal(@"\x00ff", PgBinaryText.Value(Bytea, [0x00, 0xFF]));
    }

    [Fact]
    public void Unknown_types_fall_back_to_text_then_hex()
    {
        Assert.Equal("café", PgBinaryText.Value(UserEnumOid, Utf8("café")));
        Assert.Equal(@"\x0001fe", PgBinaryText.Value(UserEnumOid, [0x00, 0x01, 0xFE]));
    }

    [Fact]
    public void Truncated_or_malformed_bytes_never_throw()
    {
        var good = Composite((Text, Utf8("abc")), (Int4, I32(1)));
        for (var cut = 0; cut < good.Length; cut++)
        {
            var text = PgBinaryText.Render(good[..cut], isArray: false);
            Assert.NotNull(text);
        }
        Assert.StartsWith(@"\x", PgBinaryText.Value(Int4, [1, 2]));
    }

    [Theory]
    [InlineData("sales.address", true)]
    [InlineData("sales.address[]", true)]
    [InlineData("public.citext", true)]
    [InlineData("pg_catalog.int4", false)]
    [InlineData("pg_catalog.record", false)]
    [InlineData("int4", false)]
    public void Only_user_schema_types_are_candidates(string name, bool expected) =>
        Assert.Equal(expected, PgBinaryText.IsUserType(name));

    [Fact]
    public void One_data_source_per_connection_string_carries_the_resolver()
    {
        // No server needed: data sources connect lazily. The type_zoo integration test reads a composite through it.
        const string a = "Host=127.0.0.1;Port=1;Username=u;Database=a", b = "Host=127.0.0.1;Port=1;Username=u;Database=b";
        Assert.Same(PostgresProvider.DataSourceFor("p", a), PostgresProvider.DataSourceFor("p", a));
        Assert.NotSame(PostgresProvider.DataSourceFor("p", a), PostgresProvider.DataSourceFor("p", b));
    }

    // L19: data sources are keyed without the password and dropped with the profile. 8a review: keyed per profile, the
    // password sent by a provider (a changed one or a refreshed Entra token updates the entry), disposed only once the last
    // connection made from them closed.

    static string Cs(string db, string password) => $"Host=127.0.0.1;Port=1;Username=u;Database={db};Password={password};Timeout=1;Pooling=false";

    static async Task AssertDisposed(Npgsql.NpgsqlDataSource source) =>
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await source.OpenConnectionAsync());

    /// <summary>Alive: an open reaches for the (absent) server instead of refusing as disposed.</summary>
    static async Task AssertAlive(Npgsql.NpgsqlDataSource source)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await source.OpenConnectionAsync());
        Assert.IsNotType<ObjectDisposedException>(ex);
    }

    [Fact]
    public async Task A_changed_password_or_token_keeps_the_data_source_and_the_next_open_sends_the_new_one()
    {
        var db = "l19_" + Guid.NewGuid().ToString("N");
        var old = PostgresProvider.DataSourceFor("p1", Cs(db, "first-secret"));
        var before = PostgresProvider.CachedDataSources;
        // An Entra token is a new value every time: no new source per token (no type loading, no dispose churn).
        for (var i = 0; i < 5; i++) Assert.Same(old, PostgresProvider.DataSourceFor("p1", Cs(db, $"token-{i}")));
        Assert.Equal(before, PostgresProvider.CachedDataSources);
        Assert.Equal("token-4", PostgresProvider.PasswordOf(old));
        await AssertAlive(old);
    }

    [Fact]
    public void Two_profiles_with_the_same_settings_never_share_or_replace_an_entry()
    {
        var db = "l19_" + Guid.NewGuid().ToString("N");
        var a = PostgresProvider.DataSourceFor("pa", Cs(db, "secret-a"));
        var b = PostgresProvider.DataSourceFor("pb", Cs(db, "secret-b"));
        Assert.NotSame(a, b);
        Assert.Same(a, PostgresProvider.DataSourceFor("pa", Cs(db, "secret-a")));
        Assert.Equal("secret-a", PostgresProvider.PasswordOf(a));
        Assert.Equal("secret-b", PostgresProvider.PasswordOf(b));
    }

    [Fact]
    public void No_cache_key_holds_the_password()
    {
        var db = "l19_" + Guid.NewGuid().ToString("N");
        PostgresProvider.DataSourceFor("p2", Cs(db, "Sup3r-Secret-Value"));
        var mine = PostgresProvider.CacheSnapshot().Where(k => k.Contains(db, StringComparison.Ordinal)).ToList();
        Assert.Single(mine);
        Assert.DoesNotContain("Sup3r-Secret-Value", mine[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Password", mine[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forgetting_a_profile_disposes_its_data_sources_only()
    {
        var db = "l19_" + Guid.NewGuid().ToString("N");
        var mine = PostgresProvider.DataSourceFor("forget-me", Cs(db + "a", "s"));
        var other = PostgresProvider.DataSourceFor("keep-me", Cs(db + "b", "s"));
        new PostgresProvider().ForgetConnections("forget-me");
        await AssertDisposed(mine);
        Assert.Same(other, PostgresProvider.DataSourceFor("keep-me", Cs(db + "b", "s")));
        Assert.NotSame(mine, PostgresProvider.DataSourceFor("forget-me", Cs(db + "a", "s")));
    }

    [Fact]
    public async Task A_forgotten_data_source_stays_alive_until_its_last_connection_closes()
    {
        var db = "l19_" + Guid.NewGuid().ToString("N");
        // Two opens in flight (no server here: the open-closed path is PostgresDataSourceIntegrationTests').
        using var first = PostgresProvider.CreateConnection("lease-me", Cs(db, "s"), out var releaseFirst);
        using var second = PostgresProvider.CreateConnection("lease-me", Cs(db, "s"), out var releaseSecond);
        var source = PostgresProvider.DataSourceFor("lease-me", Cs(db, "s"));
        new PostgresProvider().ForgetConnections("lease-me");
        await AssertAlive(source); // two connections still hold it
        releaseFirst();
        await AssertAlive(source);
        releaseSecond(); // a failed open releases its lease
        releaseSecond(); // once only
        await AssertDisposed(source);
        Assert.NotSame(source, PostgresProvider.DataSourceFor("lease-me", Cs(db, "s")));
    }

    [Fact]
    public void The_cache_is_bounded()
    {
        var db = "l19_" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < PostgresProvider.MaxDataSources + 10; i++) PostgresProvider.DataSourceFor("bound", Cs($"{db}_{i}", "s"));
        Assert.True(PostgresProvider.CachedDataSources <= PostgresProvider.MaxDataSources);
    }
}
