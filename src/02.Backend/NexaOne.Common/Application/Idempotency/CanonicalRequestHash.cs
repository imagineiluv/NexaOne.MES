using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NexaOne.Application.Idempotency;

/// <summary>
/// Produces a stable, type-aware request fingerprint without delimiter ambiguity.
/// Values are encoded as a type tag followed by a byte-length-prefixed UTF-8 payload.
/// </summary>
public static class CanonicalRequestHash
{
    private static readonly byte[] FormatMarker = "NEXA-REQUEST-HASH-V1"u8.ToArray();

    public static string Compute(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var stream = new MemoryStream();
        stream.Write(FormatMarker);
        WriteInt32(stream, values.Length);
        foreach (var value in values)
            WriteValue(stream, value);

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public static string CreateId(string prefix, int hashCharacters, params object?[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (hashCharacters is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(hashCharacters), "Hash length must be between 1 and 64 characters.");

        return prefix + Compute(values)[..hashCharacters];
    }

    private static void WriteValue(Stream stream, object? value)
    {
        if (value is null)
        {
            WritePayload(stream, 0, ReadOnlySpan<byte>.Empty);
            return;
        }

        switch (value)
        {
            case string text:
                WriteText(stream, 1, text);
                return;
            case bool boolean:
                WriteText(stream, 2, boolean ? "1" : "0");
                return;
            case byte number:
                WriteText(stream, 3, number.ToString(CultureInfo.InvariantCulture));
                return;
            case sbyte number:
                WriteText(stream, 4, number.ToString(CultureInfo.InvariantCulture));
                return;
            case short number:
                WriteText(stream, 5, number.ToString(CultureInfo.InvariantCulture));
                return;
            case ushort number:
                WriteText(stream, 6, number.ToString(CultureInfo.InvariantCulture));
                return;
            case int number:
                WriteText(stream, 7, number.ToString(CultureInfo.InvariantCulture));
                return;
            case uint number:
                WriteText(stream, 8, number.ToString(CultureInfo.InvariantCulture));
                return;
            case long number:
                WriteText(stream, 9, number.ToString(CultureInfo.InvariantCulture));
                return;
            case ulong number:
                WriteText(stream, 10, number.ToString(CultureInfo.InvariantCulture));
                return;
            case decimal number:
                WriteText(stream, 11, number.ToString("G29", CultureInfo.InvariantCulture));
                return;
            case double number:
                WriteText(stream, 12, number.ToString("R", CultureInfo.InvariantCulture));
                return;
            case float number:
                WriteText(stream, 13, number.ToString("R", CultureInfo.InvariantCulture));
                return;
            case DateTime dateTime:
                WriteText(stream, 14, NormalizeUtc(dateTime).Ticks.ToString(CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dateTimeOffset:
                WriteText(stream, 15, dateTimeOffset.UtcTicks.ToString(CultureInfo.InvariantCulture));
                return;
            case Guid guid:
                WriteText(stream, 16, guid.ToString("N"));
                return;
            case Enum enumValue:
                WriteText(stream, 17,
                    $"{enumValue.GetType().FullName}:{enumValue.ToString("D")}");
                return;
            case byte[] bytes:
                WritePayload(stream, 18, bytes);
                return;
            default:
                throw new ArgumentException(
                    $"Unsupported canonical request value type '{value.GetType().FullName}'.",
                    nameof(value));
        }
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static void WriteText(Stream stream, byte typeTag, string value)
        => WritePayload(stream, typeTag, Encoding.UTF8.GetBytes(value));

    private static void WritePayload(Stream stream, byte typeTag, ReadOnlySpan<byte> payload)
    {
        stream.WriteByte(typeTag);
        WriteInt32(stream, payload.Length);
        stream.Write(payload);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
