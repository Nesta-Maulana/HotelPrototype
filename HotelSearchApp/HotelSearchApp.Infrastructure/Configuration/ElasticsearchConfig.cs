using System;
using Elasticsearch.Net;
using HotelSearchApp.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nest;

namespace HotelSearchApp.Infrastructure.Configuration
{
    public static class ElasticsearchConfig
    {
        public static void AddElasticsearch(this IServiceCollection services, IConfiguration configuration)
        {
            var url = configuration["Elasticsearch:Url"];
            var defaultIndex = configuration["Elasticsearch:Index"];

            // Tambahkan null check
            if (string.IsNullOrEmpty(url))
            {
                url = "http://localhost:9200"; // Default URL
            }

            if (string.IsNullOrEmpty(defaultIndex))
            {
                defaultIndex = "hotels"; // Default index
            }

            var settings = new ConnectionSettings(new Uri(url))
                .DefaultIndex(defaultIndex)
                .DefaultMappingFor<Hotel>(m => m
                    .IndexName(defaultIndex)
                );

            var client = new ElasticClient(settings);

            services.AddSingleton<IElasticClient>(client);
        }
    }
}