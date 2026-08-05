using MqttRouting.ClientSimulator.Services;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServiceDefaults();
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
