using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HotelSearchApp.Core.Interfaces;
using HotelSearchApp.Core.Models;
using HotelSearchApp.Infrastructure.Configuration;
using HotelSearchApp.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OfficeOpenXml;

namespace HotelSearchApp.DataImporter
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Hotel Data Importer starting...");

            if (args.Length == 0)
            {
                Console.WriteLine("Usage: dotnet run <path-to-excel-file>");
                return;
            }

            var excelFilePath = args[0];

            if (!File.Exists(excelFilePath))
            {
                Console.WriteLine($"File not found: {excelFilePath}");
                return;
            }

            // Setup configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // Setup DI
            var services = new ServiceCollection();

            // Tambahkan ElasticSearch
            ElasticsearchConfig.AddElasticsearch(services, configuration);

            // Tambahkan service lainnya
            services.AddTransient<IElasticSearchService, ElasticSearchService>();

            // Build service provider
            var serviceProvider = services.BuildServiceProvider();

            // Get service
            var elasticSearchService = serviceProvider.GetRequiredService<IElasticSearchService>();

            try
            {
                Console.WriteLine("Creating hotel index...");
                await elasticSearchService.CreateHotelIndexAsync();

                Console.WriteLine("Creating n-gram hotel index...");
                await elasticSearchService.CreateHotelNGramIndexAsync();

                Console.WriteLine("Importing data from Excel...");
                await ImportDataFromExcel(excelFilePath, elasticSearchService);

                Console.WriteLine("Importing data from Excel to n-gram index...");
                await ImportDataFromExcelToNGram(excelFilePath, elasticSearchService);

                Console.WriteLine("Import completed successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        private static async Task ImportDataFromExcel(
            string filePath,
            IElasticSearchService elasticSearchService
        )
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                var worksheet = package.Workbook.Worksheets[0]; // First worksheet
                var rowCount = worksheet.Dimension?.Rows ?? 0;
                var colCount = worksheet.Dimension?.Columns ?? 0;

                if (rowCount == 0 || colCount == 0)
                {
                    Console.WriteLine("Excel file appears to be empty or has no data.");
                    return;
                }

                Console.WriteLine(
                    $"Found {rowCount} rows and {colCount} columns in the Excel file."
                );

                // Get header row (assuming first row is header)
                var headers = new Dictionary<int, string>();
                for (int col = 1; col <= colCount; col++)
                {
                    var headerValue = worksheet.Cells[1, col].Value?.ToString();
                    if (!string.IsNullOrEmpty(headerValue))
                    {
                        headers.Add(col, headerValue.Trim());
                    }
                }

                // Print detected headers
                Console.WriteLine("Detected headers:");
                foreach (var header in headers)
                {
                    Console.WriteLine($"Column {header.Key}: {header.Value}");
                }

                // Get indices for required columns
                var countryCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("countrycode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var countryNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("CountryName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var cityCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("citycode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var cityNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("CityName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotelcode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotelname", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelChainIdIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("HotelChainid", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelChainCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("hotelchaincode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelChainNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("hotelchainname", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelBrandIdIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotel Brand id", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelBrandCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotelbrandcode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelBrandNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("hotelbrandname", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var channelManagerNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("channel manager name", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var areaNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Area name", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var ratingIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Rating", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var contractingManagerIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("ContractingManager", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var priorityIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Priority", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;

                // Check required column indices
                if (
                    hotelCodeIndex == 0
                    || hotelNameIndex == 0
                    || cityNameIndex == 0
                    || countryNameIndex == 0
                )
                {
                    Console.WriteLine(
                        "Required columns (Hotelcode, Hotelname, CityName, CountryName) not found!"
                    );
                    return;
                }

                var hotels = new List<Hotel>();

                for (int row = 2; row <= rowCount; row++) // Start from row 2 (skip header)
                {
                    var hotel = new Hotel
                    {
                        Id = row - 1, // Generate ID based on row number
                        HotelCode = GetCellValue(worksheet, row, hotelCodeIndex),
                        HotelName = GetCellValue(worksheet, row, hotelNameIndex),
                        CityName = GetCellValue(worksheet, row, cityNameIndex),
                        Country = GetCellValue(worksheet, row, countryNameIndex),
                        // Store area name in Address1 field for searchability
                        Address1 = GetCellValue(worksheet, row, areaNameIndex),
                        // We can add some additional data in Address2 for potential future use
                        Address2 = string.Join(
                            ", ",
                            new[]
                            {
                                GetCellValue(worksheet, row, hotelChainNameIndex),
                                GetCellValue(worksheet, row, hotelBrandNameIndex),
                                GetCellValue(worksheet, row, channelManagerNameIndex),
                            }.Where(s => !string.IsNullOrEmpty(s))
                        ),
                        // State can hold rating
                        State = GetCellValue(worksheet, row, ratingIndex),
                        // PostalCode can hold city code
                        PostalCode = GetCellValue(worksheet, row, cityCodeIndex),
                        // PhoneNumber can hold contracting manager info
                        PhoneNumber = GetCellValue(worksheet, row, contractingManagerIndex),
                        // Set LastUpdated to current time
                        LastUpdated = DateTime.UtcNow,
                    };

                    // Skip rows with no hotel code or hotel name
                    if (
                        !string.IsNullOrWhiteSpace(hotel.HotelCode)
                        && !string.IsNullOrWhiteSpace(hotel.HotelName)
                    )
                    {
                        hotels.Add(hotel);
                    }

                    // Import in batches of 1000
                    if (hotels.Count >= 1000)
                    {
                        Console.WriteLine($"Indexing batch of {hotels.Count} hotels...");
                        await elasticSearchService.IndexHotelsAsync(hotels);
                        hotels.Clear();
                    }
                }

                // Index any remaining hotels
                if (hotels.Any())
                {
                    Console.WriteLine($"Indexing final batch of {hotels.Count} hotels...");
                    await elasticSearchService.IndexHotelsAsync(hotels);
                }

                Console.WriteLine($"Imported {rowCount - 1} hotels to Elasticsearch.");
            }
        }

        // Update the NGram import method similarly
        private static async Task ImportDataFromExcelToNGram(
            string filePath,
            IElasticSearchService elasticSearchService
        )
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                var worksheet = package.Workbook.Worksheets[0]; // First worksheet
                var rowCount = worksheet.Dimension?.Rows ?? 0;
                var colCount = worksheet.Dimension?.Columns ?? 0;

                if (rowCount == 0 || colCount == 0)
                {
                    Console.WriteLine("Excel file appears to be empty or has no data.");
                    return;
                }

                Console.WriteLine(
                    $"Found {rowCount} rows and {colCount} columns in the Excel file for n-gram import."
                );

                // Get header row
                var headers = new Dictionary<int, string>();
                for (int col = 1; col <= colCount; col++)
                {
                    var headerValue = worksheet.Cells[1, col].Value?.ToString();
                    if (!string.IsNullOrEmpty(headerValue))
                    {
                        headers.Add(col, headerValue.Trim());
                    }
                }

                // Print detected headers
                Console.WriteLine("Detected headers for n-gram import:");
                foreach (var header in headers)
                {
                    Console.WriteLine($"Column {header.Key}: {header.Value}");
                }

                // Get indices for required columns (same as above)
                var countryCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("countrycode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var countryNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("CountryName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var cityCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("citycode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var cityNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("CityName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotelcode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotelname", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelChainIdIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("HotelChainid", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelChainCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("hotelchaincode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelChainNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("hotelchainname", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelBrandIdIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotel Brand id", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelBrandCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Hotelbrandcode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelBrandNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("hotelbrandname", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var channelManagerNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("channel manager name", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var areaNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Area name", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var ratingIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Rating", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var contractingManagerIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("ContractingManager", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var priorityIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Priority", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;

                // Check required column indices
                if (
                    hotelCodeIndex == 0
                    || hotelNameIndex == 0
                    || cityNameIndex == 0
                    || countryNameIndex == 0
                )
                {
                    Console.WriteLine(
                        "Required columns (Hotelcode, Hotelname, CityName, CountryName) not found!"
                    );
                    return;
                }

                var hotels = new List<Hotel>();

                for (int row = 2; row <= rowCount; row++) // Start from row 2 (skip header)
                {
                    var hotel = new Hotel
                    {
                        Id = row - 1, // Generate ID based on row number
                        HotelCode = GetCellValue(worksheet, row, hotelCodeIndex),
                        HotelName = GetCellValue(worksheet, row, hotelNameIndex),
                        CityName = GetCellValue(worksheet, row, cityNameIndex),
                        Country = GetCellValue(worksheet, row, countryNameIndex),
                        // Store area name in Address1 field for searchability
                        Address1 = GetCellValue(worksheet, row, areaNameIndex),
                        // We can add some additional data in Address2 for potential future use
                        Address2 = string.Join(
                            ", ",
                            new[]
                            {
                                GetCellValue(worksheet, row, hotelChainNameIndex),
                                GetCellValue(worksheet, row, hotelBrandNameIndex),
                                GetCellValue(worksheet, row, channelManagerNameIndex),
                            }.Where(s => !string.IsNullOrEmpty(s))
                        ),
                        // State can hold rating
                        State = GetCellValue(worksheet, row, ratingIndex),
                        // PostalCode can hold city code
                        PostalCode = GetCellValue(worksheet, row, cityCodeIndex),
                        // PhoneNumber can hold contracting manager info
                        PhoneNumber = GetCellValue(worksheet, row, contractingManagerIndex),
                        // Set LastUpdated to current time
                        LastUpdated = DateTime.UtcNow,
                    };

                    // Skip rows with no hotel code or hotel name
                    if (
                        !string.IsNullOrWhiteSpace(hotel.HotelCode)
                        && !string.IsNullOrWhiteSpace(hotel.HotelName)
                    )
                    {
                        hotels.Add(hotel);
                    }

                    // Import in batches of 1000
                    if (hotels.Count >= 1000)
                    {
                        Console.WriteLine(
                            $"Indexing batch of {hotels.Count} hotels to n-gram index..."
                        );
                        await elasticSearchService.IndexHotelsNGramAsync(hotels);
                        hotels.Clear();
                    }
                }

                // Index any remaining hotels
                if (hotels.Any())
                {
                    Console.WriteLine(
                        $"Indexing final batch of {hotels.Count} hotels to n-gram index..."
                    );
                    await elasticSearchService.IndexHotelsNGramAsync(hotels);
                }

                Console.WriteLine($"Imported {rowCount - 1} hotels to Elasticsearch n-gram index.");
            }
        }

        private static string? GetCellValue(ExcelWorksheet worksheet, int row, int col)
        {
            if (col == 0)
                return null;

            var value = worksheet.Cells[row, col].Value;
            return value?.ToString()?.Trim();
        }
    }
}
