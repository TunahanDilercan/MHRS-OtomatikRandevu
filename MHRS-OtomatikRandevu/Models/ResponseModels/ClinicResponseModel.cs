using System.Text.Json.Serialization;

namespace MHRS_OtomatikRandevu.Models.ResponseModels
{
    public class ClinicResponseModel
    {
        [JsonPropertyName("value")]
        [JsonConverter(typeof(IntOrStringConverter))]
        public string Value { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }
        
        // Value'yi int olarak almak için extension property
        public int ValueAsInt => int.Parse(Value ?? "0");
    }
}
