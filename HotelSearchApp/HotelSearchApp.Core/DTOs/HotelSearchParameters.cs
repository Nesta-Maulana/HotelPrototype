namespace HotelSearchApp.Core.DTOs
{
    public class HotelSearchParameters
    {
        public string? CityName { get; set; }
        public string? HotelCode { get; set; }
        public string? HotelName { get; set; }
        public string? Address1 { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }
}