using HotelSearchApp.Core.Interfaces;
using HotelSearchApp.Infrastructure.Configuration;
using HotelSearchApp.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(); // Pastikan ini ada
builder.Services.AddElasticsearch(builder.Configuration);
builder.Services.AddTransient<IElasticSearchService, ElasticSearchService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

// Update routing untuk menggunakan MVC dengan Hotel sebagai controller default
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Hotel}/{action=Index}/{id?}");

app.Run();