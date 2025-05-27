using System;
using System.Collections.Generic;

namespace HotelSearchApp.Core.DTOs
{
    public class ElasticSearchResponse<T>
        where T : class
    {
        public required IEnumerable<T> Items { get; set; }
        public long TotalHits { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling(TotalHits / (double)PageSize);
        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;

        // Properties for special search types
        public List<string> TopCities { get; set; } = new List<string>();
        public bool IsCountrySearch { get; set; } = false;
        public bool IsHotelCodeSearch { get; set; } = false;
    }
}
