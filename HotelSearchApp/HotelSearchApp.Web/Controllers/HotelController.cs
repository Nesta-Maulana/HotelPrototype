using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HotelSearchApp.Core.DTOs;
using HotelSearchApp.Core.Interfaces;
using HotelSearchApp.Core.Models;
using HotelSearchApp.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace HotelSearchApp.Web.Controllers
{
    public class HotelController : Controller
    {
        private readonly IElasticSearchService _elasticSearchService;

        public HotelController(IElasticSearchService elasticSearchService)
        {
            _elasticSearchService = elasticSearchService;
        }

        public IActionResult Index()
        {
            return View(new HotelSearchViewModel());
        }

        [HttpGet]
        public async Task<IActionResult> Search(HotelSearchViewModel viewModel)
        {
            var stopwatch = Stopwatch.StartNew();

            var searchParams = new HotelSearchParameters
            {
                CityName = viewModel.CityName,
                HotelCode = viewModel.HotelCode,
                HotelName = viewModel.HotelName,
                Address1 = viewModel.Address1,
                PageNumber = viewModel.PageNumber,
                PageSize = viewModel.PageSize
            };

            var result = await _elasticSearchService.SearchHotelsAsync(searchParams);
            
            stopwatch.Stop();

            viewModel.SearchResults = result;
            viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

            return View("Index", viewModel);
        }
    }
}