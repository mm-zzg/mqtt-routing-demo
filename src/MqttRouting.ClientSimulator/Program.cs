using Microsoft.EntityFrameworkCore;
using MqttRouting.ClientSimulator.Data;
using MqttRouting.ClientSimulator.Services;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("ClientSimulator")
                       ?? "Data Source=clientsimulator.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddSingleton<ClientSimulatorManager>();
builder.Services.AddHostedService<ClientSimulatorHostedService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapDefaultEndpoints();

app.Run();
