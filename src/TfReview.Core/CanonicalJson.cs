using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TfReview.Core;

/// <summary>
/// One byte-stable form of a JSON document: object keys in ordinal order, array order untouched,
/// numbers exactly as written. Prompt caching and result caching both depend on the bytes.
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteNode(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses a document, throwing <see cref="JsonException"/> on a repeated object key.</summary>
    /// <remarks>Parsers disagree on which repeated key wins, so a value could otherwise hide from a check.</remarks>
    public static JsonNode? ParseStrict(string json)
    {
        ThrowOnDuplicateKeys(json);
        return JsonNode.Parse(json);
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var (key, child) in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, child);
                }

                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (var child in value)
                {
                    WriteNode(writer, child);
                }

                writer.WriteEndArray();
                break;
            default:
                // A parsed value keeps its original number text, which a double couldn't for large numbers.
                node.WriteTo(writer);
                break;
        }
    }

    private static void ThrowOnDuplicateKeys(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        var scopes = new Stack<HashSet<string>?>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!scopes.Peek()!.Add(reader.GetString()!))
                    {
                        throw new JsonException("The document repeats an object key.");
                    }

                    break;
            }
        }
    }
}
