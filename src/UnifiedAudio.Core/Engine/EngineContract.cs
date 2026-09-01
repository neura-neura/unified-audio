using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedAudio.Core.Engine;

public static class EngineContract
{
    public const int MajorVersion = 1;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
}

public enum EngineMessageKind
{
    Command,
    Response,
    Event
}

public sealed record EngineError(string Code, string Message, JsonElement? Detail = null);

public sealed record EngineMessage
{
    public int ContractVersion { get; init; } = EngineContract.MajorVersion;
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; init; }
    public EngineMessageKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public JsonElement Payload { get; init; }
    public EngineError? Error { get; init; }
}

public static class EngineFrameCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] Encode(EngineMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ContractVersion != EngineContract.MajorVersion)
        {
            throw new InvalidDataException($"Unsupported engine contract {message.ContractVersion}.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length > EngineContract.MaximumFrameBytes)
        {
            throw new InvalidDataException("Engine frame exceeds the bounded protocol size.");
        }

        var frame = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame.AsSpan(sizeof(int)));
        return frame;
    }

    public static EngineMessage Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < sizeof(int))
        {
            throw new InvalidDataException("Engine frame has no length prefix.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (length < 0 || length > EngineContract.MaximumFrameBytes || frame.Length != length + sizeof(int))
        {
            throw new InvalidDataException("Engine frame length is invalid.");
        }

        var message = JsonSerializer.Deserialize<EngineMessage>(frame[sizeof(int)..], JsonOptions)
            ?? throw new InvalidDataException("Engine frame contains no message.");
        if (message.ContractVersion != EngineContract.MajorVersion)
        {
            throw new InvalidDataException($"Unsupported engine contract {message.ContractVersion}.");
        }

        return message;
    }
}
