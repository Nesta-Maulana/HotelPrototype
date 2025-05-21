using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
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
                // Normalize search query to improve match quality
                var normalizedQuery = viewModel.SearchQuery?.Trim() ?? string.Empty;

                // Always limit to at most 10 hotels total and 5 cities
                var pageSize = 20; // Request more hotels initially to ensure we have enough for filtering
                
                var result = await _elasticSearchService.UnifiedSearchAsync(
                    normalizedQuery,
                    viewModel.PageNumber,
                    pageSize
                );

                stopwatch.Stop();
                
                // Always limit results to 5 cities and 10 hotels
                // Group hotels by city
                var groupedByCities = result.Items.GroupBy(h => h.CityName).Where(g => g.Key != null).ToList();
                
                // Take up to 5 cities
                var limitedCities = groupedByCities.Take(5).ToList();
                
                // For each city, take top hotels (proportionally distributed to get 10 total)
                var limitedHotels = new List<Hotel>();
                int totalHotelsToShow = 10;
                
                if (limitedCities.Any())
                {
                    int hotelsPerCity = Math.Max(1, totalHotelsToShow / limitedCities.Count);
                    int remainingSlots = totalHotelsToShow;
                    
                    foreach (var cityGroup in limitedCities)
                    {                        
                        int take = Math.Min(remainingSlots, hotelsPerCity);
                        limitedHotels.AddRange(cityGroup.Take(take));
                        remainingSlots -= take;
                        
                        if (remainingSlots <= 0)
                            break;
                    }
                    
                    // If we still have slots left and have hotels available, add more from the first cities
                    if (remainingSlots > 0)
                    {
                        foreach (var cityGroup in limitedCities)
                        {
                            var additionalHotels = cityGroup.Skip(hotelsPerCity).Take(remainingSlots).ToList();
                            limitedHotels.AddRange(additionalHotels);
                            remainingSlots -= additionalHotels.Count;
                            
                            if (remainingSlots <= 0)
                                break;
                        }
                    }
                }
                else if (result.Items.Any())
                {
                    // If no city groups (null city names), just take first 10 hotels
                    limitedHotels = result.Items.Take(totalHotelsToShow).ToList();
                }
                
                // Update the response with limited results
                result = new ElasticSearchResponse<Hotel>
                {
                    Items = limitedHotels,
                    TotalHits = result.TotalHits, // Keep original total for pagination info
                    ElapsedTime = result.ElapsedTime,
                    PageNumber = result.PageNumber,
                    PageSize = viewModel.PageSize
                };

                viewModel.SearchResults = result;
                viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

                // Track search success and terms for analytics (could be expanded)
                viewModel.SearchSuccessful = result.TotalHits > 0;
                viewModel.SearchTermsCount =
                    normalizedQuery
                        ?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Length ?? 0;

                // If request is AJAX, return partial view for results
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

                viewModel.SearchResults = null;
                viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                viewModel.ErrorMessage = "An error occurred while searching. Please try again.";

                // If request is AJAX, return partial view for results
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return PartialView("_SearchResults", viewModel);
                }

                return View("Index", viewModel);
            }
        }
    }
}
