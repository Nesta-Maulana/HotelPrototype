using System.Collections.Generic;
using System.Threading.Tasks;
using HotelSearchApp.Core.DTOs;
using HotelSearchApp.Core.Models;

namespace HotelSearchApp.Core.Interfaces
{
    public interface IElasticSearchService
    {
        Task<ElasticSearchResponse<Hotel>> SearchHotelsAsync(HotelSearchParameters searchParams);
        Task<ElasticSearchResponse<Hotel>> SearchHotelsNGramAsync(
            HotelSearchParameters searchParams
        );
        Task<ElasticSearchResponse<Hotel>> UnifiedSearchAsync(
            string searchQuery,
            int pageNumber = 1,
            int pageSize = 10
        );
        Task<bool> IndexHotelAsync(Hotel hotel);
        Task<bool> IndexHotelsAsync(IEnumerable<Hotel> hotels);
        Task<bool> IndexHotelsNGramAsync(IEnumerable<Hotel> hotels);
        Task<bool> CreateHotelIndexAsync();
        Task<bool> CreateHotelNGramIndexAsync();
        Task<bool> DeleteHotelIndexAsync();
    }
}
