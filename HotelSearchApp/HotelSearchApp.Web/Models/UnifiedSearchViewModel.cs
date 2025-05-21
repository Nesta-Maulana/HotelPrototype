using HotelSearchApp.Core.DTOs;
using HotelSearchApp.Core.Models;

namespace HotelSearchApp.Web.Models
{
    public class UnifiedSearchViewModel
    {
        public string SearchQuery { get; set; } = string.Empty;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public ElasticSearchResponse<Hotel>? SearchResults { get; set; }
        public long TotalElapsedMilliseconds { get; set; }
        public string SearchMethod { get; set; } = "Unified"; // Default ke Unified
    }
}