
using PiRouterBackend.Services;

namespace PiRouterBackend;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // Add CORS for frontend
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", corsPolicyBuilder =>
            {
                corsPolicyBuilder.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        // Add application services
        builder.Services.AddSingleton<IVpnManager, VpnManager>();
        builder.Services.AddSingleton<IDeviceManager, DeviceManager>();
        builder.Services.AddSingleton<IDomainManager, DomainManager>();
        builder.Services.AddSingleton<ISystemManager, SystemManager>();
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IConfigManager, ConfigManager>();

        var app = builder.Build();

        // Initialize services
        using (var scope = app.Services.CreateScope())
        {
            var domainManager = scope.ServiceProvider.GetRequiredService<IDomainManager>();
            var deviceManager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            
            try
            {
                // Run initialization with timeout (30 seconds)
                var initTask = Task.Run(async () => 
                {
                    try
                    {
                        await domainManager.Initialize();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error initializing DomainManager");
                    }
                    
                    try
                    {
                        await deviceManager.Initialize();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error initializing DeviceManager");
                    }
                });
                
                if (!initTask.Wait(TimeSpan.FromSeconds(30)))
                {
                    logger.LogWarning("Initialization timeout - continuing startup anyway");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during service initialization");
            }
        }

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseCors("AllowAll");

        app.UseAuthorization();

        app.MapControllers();

        // Root endpoint
        app.MapGet("/", () => new
        {
            message = "PiRouter VPN Manager API",
            version = "2.0.0",
            docs = "/swagger"
        });

        // Set to listen on port 51508 for C# backend (or 5000 for development)
        var port = 5000;  // Use standard development port
        if (!app.Environment.IsDevelopment())
        {
            port = 51508;  // Use configured port in production
        }
        
        app.Urls.Clear();
        app.Urls.Add($"http://0.0.0.0:{port}");  // Listen on ALL interfaces, not just localhost

        Console.WriteLine($"Starting PiRouter Backend on http://0.0.0.0:{port}");
        Console.WriteLine($"Environment: {app.Environment.EnvironmentName}");
        
        app.Run();
    }
}
