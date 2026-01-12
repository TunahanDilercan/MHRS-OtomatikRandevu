using System.Text.Json.Serialization;
using System.Text.Json;

namespace MHRS_OtomatikRandevu.Models.ResponseModels
{
    public class IntOrStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetInt32().ToString();
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString() ?? "0";
            }
            return "0";
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    public class GenericResponseModel
    {
        [JsonPropertyName("value")]
        [JsonConverter(typeof(IntOrStringConverter))]
        public string Value { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("children")]
        public List<ProvinceResponseModel> Children { get; set; }
        
        // Value'yi int olarak almak için extension property
        public int ValueAsInt => int.Parse(Value ?? "0");
    }
}
