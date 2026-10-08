using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartBank.API.Json
{
    /// <summary>
    /// Refuses a JSON string that contains the NUL character (\u0000). SQL Server stores it, PostgreSQL cannot (error 22021), so
    /// the same input was a 200 on one and a 500 on the other. No field of this API has a use for it: it is rejected at the door,
    /// for every text of every request body and every hub message, with the ordinary "request is not valid" answer.
    /// </summary>
    public sealed class NoNulStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value != null && value.Contains('\0'))
            {
                throw new JsonException("Text must not contain the NUL character.");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
