using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HotelSearchApp.Core.DTOs;
using HotelSearchApp.Core.Interfaces;
using HotelSearchApp.Core.Models;
using HotelSearchApp.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace HotelSearchApp.Web.Controllers
{
    public class UnifiedSearchController : Controller
    {
        private readonly IElasticSearchService _elasticSearchService;

        public UnifiedSearchController(IElasticSearchService elasticSearchService)
        {
            _elasticSearchService = elasticSearchService;
        }

        public IActionResult Index()
        {
            return View(new UnifiedSearchViewModel());
        }

        // Add this to UnifiedSearchController.cs
        [HttpGet]
        public async Task<IActionResult> Search(UnifiedSearchViewModel viewModel)
        {
            if (string.IsNullOrWhiteSpace(viewModel.SearchQuery))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return PartialView("_SearchResults", viewModel);
                }
                return View("Index", viewModel);
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Get search results using unified search approach
                var searchResult = await _elasticSearchService.UnifiedSearchAsync(
                    viewModel.SearchQuery,
                    viewModel.PageNumber,
                    viewModel.PageSize
                );

                stopwatch.Stop();

                // Update view model with results
                viewModel.SearchResults = searchResult;
                viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                viewModel.SearchMethod = "Unified";

                // Set special search method if detected
                if (searchResult.IsCountrySearch)
                {
                    viewModel.SearchMethod = "Country Search";
                }
                else if (searchResult.IsHotelCodeSearch)
                {
                    viewModel.SearchMethod = "Hotel Code Search";
                }

                // Track search analytics
                viewModel.SearchSuccessful = searchResult.TotalHits > 0;
                viewModel.SearchTermsCount =
                    viewModel
                        .SearchQuery?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Length ?? 0;

                // Handle AJAX requests for real-time search
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return PartialView("_SearchResults", viewModel);
                }

                return View("Index", viewModel);
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Search error: {ex.Message}");

                // Provide error feedback
                viewModel.SearchResults = null;
                viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                viewModel.ErrorMessage = "An error occurred while searching. Please try again.";

                // Handle AJAX requests
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return PartialView("_SearchResults", viewModel);
                }

                return View("Index", viewModel);
            }
        }
        
        [HttpGet]
        public async Task<JsonResult> GetCitySuggestions(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            {
                return Json(new { suggestions = new List<object>(), hotels = new List<object>() });
            }

            try
            {
                // Get city suggestions
                var suggestions = await _elasticSearchService.GetCitySuggestionsAsync(query, 5);
                
                // Get hotels for the best matching city
                var topHotels = new List<object>();
                if (suggestions.Any())
                {
                    var bestMatchCity = suggestions.First().CityName;
                    var hotels = await _elasticSearchService.GetHotelsByCityAsync(bestMatchCity, 10);
                    
                    topHotels = hotels.Select(h => new
                    {
                        id = h.Id,
                        hotelCode = h.HotelCode,
                        hotelName = h.HotelName,
                        cityName = h.CityName,
                        address1 = h.Address1,
                        address2 = h.Address2,
                        country = h.Country
                    }).ToList<object>();
                }
                
                // Convert suggestions to anonymous objects for JSON serialization
                var suggestionsResult = suggestions.Select(s => new
                {
                    cityName = s.CityName,
                    country = s.Country,
                    hotelCount = s.HotelCount,
                    similarity = s.Similarity
                }).ToList();
                
                return Json(new { 
                    suggestions = suggestionsResult,
                    hotels = topHotels,
                    searchQuery = query
                });
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Error getting city suggestions: {ex.Message}");
                return Json(new { suggestions = new List<object>(), hotels = new List<object>() });
            }
        }
    }
}
