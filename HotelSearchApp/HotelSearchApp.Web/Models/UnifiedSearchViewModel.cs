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
        public string SearchMethod { get; set; } = "Unified"; // Default to Unified

        // New properties for tracking search effectiveness
        public bool SearchSuccessful { get; set; }
        public int SearchTermsCount { get; set; }
        public string? ErrorMessage { get; set; }
        public bool HasSearched => !string.IsNullOrWhiteSpace(SearchQuery);
    }
}
