using System;
using Nest;

namespace HotelSearchApp.Core.Models
{
    public class Hotel
    {
        [Number(Name = "id")]
        public int Id { get; set; }

        [Keyword(Name = "hotelcode")]
        public string? HotelCode { get; set; }

        [Text(Name = "hotelname", Analyzer = "standard", SearchAnalyzer = "standard")]
        public string? HotelName { get; set; }

        [Text(Name = "cityname", Analyzer = "standard", SearchAnalyzer = "standard")]
        public string? CityName { get; set; }

        [Text(Name = "address1", Analyzer = "standard", SearchAnalyzer = "standard")]
        public string? Address1 { get; set; }

        [Text(Name = "address2", Analyzer = "standard", SearchAnalyzer = "standard")]
        public string? Address2 { get; set; }

        [Text(Name = "state", Analyzer = "standard", SearchAnalyzer = "standard")]
        public string? State { get; set; }

        [Text(Name = "country", Analyzer = "standard", SearchAnalyzer = "standard")]
        public string? Country { get; set; }

        [Keyword(Name = "postalcode")]
        public string? PostalCode { get; set; }

        [Keyword(Name = "phonenumber")]
        public string? PhoneNumber { get; set; }

        [Date(Name = "lastupdated")]
        public DateTime? LastUpdated { get; set; }
    }
}