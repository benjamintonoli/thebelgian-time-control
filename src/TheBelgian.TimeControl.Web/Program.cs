using Microsoft.AspNetCore.DataProtection;
using TheBelgian.TimeControl.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionDirectory = Path.Combine(
    builder.Environment.ContentRootPath,
    "data",
    "data-protection-keys");
Directory.CreateDirectory(dataProtectionDirectory);

builder.Services.AddRazorPages();
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory))
    .SetApplicationName("TheBelgian.TimeControl");
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

builder.Services.AddTimeControlInfrastructure(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
await app.Services.InitializeTimeControlDatabaseAsync();

app.Run();
