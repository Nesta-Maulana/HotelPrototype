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
                var hotelCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("HotelCode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("HotelName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var cityNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("CityName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var address1Index = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Address1", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var address2Index = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Address2", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var stateIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("State", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var countryIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Country", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var postalCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("PostalCode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var phoneNumberIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("PhoneNumber", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var lastUpdatedIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("LastUpdated", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;

                // Check required column indices
                if (
                    hotelCodeIndex == 0
                    || hotelNameIndex == 0
                    || cityNameIndex == 0
                    || address1Index == 0
                )
                {
                    Console.WriteLine(
                        "Required columns (HotelCode, HotelName, CityName, Address1) not found!"
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
                        Address1 = GetCellValue(worksheet, row, address1Index),
                        Address2 =
                            address2Index > 0 ? GetCellValue(worksheet, row, address2Index) : null,
                        State = stateIndex > 0 ? GetCellValue(worksheet, row, stateIndex) : null,
                        Country =
                            countryIndex > 0 ? GetCellValue(worksheet, row, countryIndex) : null,
                        PostalCode =
                            postalCodeIndex > 0
                                ? GetCellValue(worksheet, row, postalCodeIndex)
                                : null,
                        PhoneNumber =
                            phoneNumberIndex > 0
                                ? GetCellValue(worksheet, row, phoneNumberIndex)
                                : null,
                    };

                    if (lastUpdatedIndex > 0)
                    {
                        var lastUpdatedValue = worksheet.Cells[row, lastUpdatedIndex].Value;
                        if (
                            lastUpdatedValue != null
                            && DateTime.TryParse(lastUpdatedValue.ToString(), out var date)
                        )
                        {
                            hotel.LastUpdated = date;
                        }
                    }

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

                // Get indices for required columns
                var hotelCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("HotelCode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var hotelNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("HotelName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var cityNameIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("CityName", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var address1Index = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Address1", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var address2Index = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Address2", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var stateIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("State", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var countryIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("Country", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var postalCodeIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("PostalCode", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var phoneNumberIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("PhoneNumber", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                var lastUpdatedIndex = headers
                    .FirstOrDefault(h =>
                        h.Value.Equals("LastUpdated", StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;

                if (
                    hotelCodeIndex == 0
                    || hotelNameIndex == 0
                    || cityNameIndex == 0
                    || address1Index == 0
                )
                {
                    Console.WriteLine(
                        "Required columns (HotelCode, HotelName, CityName, Address1) not found!"
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
                        Address1 = GetCellValue(worksheet, row, address1Index),
                        Address2 =
                            address2Index > 0 ? GetCellValue(worksheet, row, address2Index) : null,
                        State = stateIndex > 0 ? GetCellValue(worksheet, row, stateIndex) : null,
                        Country =
                            countryIndex > 0 ? GetCellValue(worksheet, row, countryIndex) : null,
                        PostalCode =
                            postalCodeIndex > 0
                                ? GetCellValue(worksheet, row, postalCodeIndex)
                                : null,
                        PhoneNumber =
                            phoneNumberIndex > 0
                                ? GetCellValue(worksheet, row, phoneNumberIndex)
                                : null,
                    };

                    if (lastUpdatedIndex > 0)
                    {
                        var lastUpdatedValue = worksheet.Cells[row, lastUpdatedIndex].Value;
                        if (
                            lastUpdatedValue != null
                            && DateTime.TryParse(lastUpdatedValue.ToString(), out var date)
                        )
                        {
                            hotel.LastUpdated = date;
                        }
                    }

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
