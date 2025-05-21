using HotelSearchApp.Core.DTOs;
using HotelSearchApp.Core.Models;

namespace HotelSearchApp.Web.Models
{
    public class HotelSearchViewModel
    {
        public string CityName { get; set; } = string.Empty;
        public string HotelCode { get; set; } = string.Empty;
        public string HotelName { get; set; } = string.Empty;
        public string Address1 { get; set; } = string.Empty;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public ElasticSearchResponse<Hotel>? SearchResults { get; set; }
        public long TotalElapsedMilliseconds { get; set; }
        public string SearchMethod { get; set; } = "Fuzzy"; // Default ke Fuzzy
    }
}