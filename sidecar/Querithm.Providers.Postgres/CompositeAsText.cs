#pragma warning disable NPG9001 // Npgsql's type-resolver plugin API is marked experimental.
using System.Text;
using Npgsql.Internal;
using Npgsql.Internal.Postgres;

namespace Querithm.Providers.Postgres;

/// <summary>
/// User-defined composite types (and arrays of them) have no CLR mapping, and Npgsql then refuses to read the column
/// at all: not as object, string, bytes or a stream, so <c>SELECT *</c> over such a table failed. For a user type
/// (outside pg_catalog) that none of Npgsql's own resolvers handles, this resolver reads the raw value and renders it
/// as psql text, e.g. <c>(line1,,city,code,GB)</c>. Types Npgsql already reads (enums, hstore, …) are untouched.
/// Read only: parameters are sent as untyped text and never use this.
/// </summary>
sealed class UnreadableUserTypesAsTextResolverFactory : PgTypeInfoResolverFactory
{
    public override IPgTypeInfoResolver CreateResolver() => new Resolver();
    public override IPgTypeInfoResolver CreateArrayResolver() => new Resolver();

    sealed class Resolver : IPgTypeInfoResolver
    {
        [ThreadStatic] static bool _probing;

        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
        {
            if (_probing || dataTypeName is not { } name || !PgBinaryText.IsUserType(name.Value)) return null;
            // Read as string; GetValue asks for object and gets the same string boxed.
            if (type != null && type != typeof(object) && type != typeof(string)) return null;
            // Only when the rest of the chain has nothing for it (asking the whole chain re-enters here, hence the guard).
            _probing = true;
            try
            {
                if (options.TypeInfoResolver.GetTypeInfo(type, name, options) != null) return null;
            }
            catch (Exception)
            {
                // A resolver that throws for the type cannot read it either.
            }
            finally
            {
                _probing = false;
            }
            var mapping = new TypeInfoMapping(type ?? typeof(string), name.Value, (o, m, _) => m.CreateInfo(o,
                type == typeof(object) ? new RawAsTextConverter<object>(o.TextEncoding, name.IsArray) : new RawAsTextConverter<string>(o.TextEncoding, name.IsArray),
                preferredFormat: null, supportsWriting: false));
            return mapping.Factory(options, mapping, true);
        }
    }

    /// <summary>Binary values go through <see cref="PgBinaryText"/>; a text-format value is already psql text.</summary>
    /// <remarks>Typed as object for GetValue, string otherwise: Npgsql requires the converter type to match the request.</remarks>
    sealed class RawAsTextConverter<T>(Encoding encoding, bool isArray) : PgStreamingConverter<T> where T : class
    {
        public override bool CanConvert(DataFormat format, out BufferRequirements bufferRequirements)
        {
            bufferRequirements = BufferRequirements.None;
            return true;
        }

        public override T Read(PgReader reader) => (T)(object)Decode(reader, ReadAll(reader.GetStream()));

        public override async ValueTask<T> ReadAsync(PgReader reader, CancellationToken cancellationToken = default)
        {
            await using var s = reader.GetStream();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            return (T)(object)Decode(reader, ms.ToArray());
        }

        static byte[] ReadAll(Stream s)
        {
            using (s)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        string Decode(PgReader reader, byte[] bytes) =>
            reader.Current.Format == DataFormat.Text ? encoding.GetString(bytes) : PgBinaryText.Render(bytes, isArray);

        public override Size GetSize(SizeContext context, T value, ref object? writeState) => throw new NotSupportedException("Read only.");
        public override void Write(PgWriter writer, T value) => throw new NotSupportedException("Read only.");
        public override ValueTask WriteAsync(PgWriter writer, T value, CancellationToken cancellationToken = default) => throw new NotSupportedException("Read only.");
    }
}
