namespace HotelSearchApp.Core.DTOs
{
    public class CitySuggestionDto
    {
        public string CityName { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public int HotelCount { get; set; }
        public double Similarity { get; set; }
    }
}