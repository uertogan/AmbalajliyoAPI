using Ambalajliyo.BLL.Interfaces;
using Ambalajliyo.BLL.Services;
using Ambalajliyo.DAL.AbstractRepository;
using Ambalajliyo.DAL.ConcreteRepository;
using Ambalajliyo.DAL.Context;
using Ambalajliyo.DAL.Entities;
using Ambalajliyo.WebAPI.ActionFilters;
using Ambalajliyo.WebAPI.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;
using System.Text;
using System.Threading.RateLimiting;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Serilog konfig�rasyonu
        var connectionString = builder.Configuration.GetConnectionString("LogConnection");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.MSSqlServer(
                connectionString: connectionString,
                sinkOptions: new MSSqlServerSinkOptions
                {
                    TableName = "Logs",
                    AutoCreateSqlTable = true
                },
                restrictedToMinimumLevel: LogEventLevel.Information)
            .CreateLogger();

        // Veritaban� konfig�rasyonu
        builder.Services.AddDbContext<AmbalajliyoDbContext>(options =>
            options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
        builder.Services.AddDbContext<AmbalajliyoLogDbContext>(options =>
            options.UseSqlServer(builder.Configuration.GetConnectionString("LogConnection")));

        // Identity konfig�rasyonu
        builder.Services.AddIdentity<AmbalajliyoUser, AmbalajliyoRole>()
            .AddEntityFrameworkStores<AmbalajliyoDbContext>()
            .AddDefaultTokenProviders();

        // JWT Authentication konfig�rasyonu (Tekrar eden k�sm� kald�rd�k)
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Token ayarlar�n� belirleyelim
                options.TokenValidationParameters = new TokenValidationParameters()
                {
                    ValidateIssuer = true, // JWT token'� veren ki�iyi do�rula
                    ValidateAudience = true, // JWT token'� kullanan ki�iyi do�rula
                    ValidateLifetime = true, // JWT token'�n s�resini do�rula
                    ValidateIssuerSigningKey = true, // JWT token'�n imza key'ini do�rula
                    RequireExpirationTime = true,

                    ValidIssuer = builder.Configuration["JwtTokenSettings:Issuer"],
                    ValidAudience = builder.Configuration["JwtTokenSettings:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["JwtTokenSettings:Key"]))
                };
            });            

            // Rate limiting yap�land�rmas�
            builder.Services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    // Swagger dok�mantasyonu i�in rate limiting'i devre d��� b�rak�yoruz.
                    // ��nk� Swagger UI genellikle dok�mantasyon ama�l�d�r ve burada rate limiting'in uygulanmas� gereksizdir.
                    var path = httpContext.Request.Path.ToString().ToLower();
                    if (path.StartsWith("/swagger") || path.StartsWith("/v1/swagger"))
                    {
                        // Swagger iste�i oldu�unda rate limiter devre d��� b�rak�l�r.
                        return RateLimitPartition.GetNoLimiter<string>(httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString());
                    }

                    // Di�er t�m istekler i�in rate limiting uygulan�r.
                    // Kullan�c� kimli�i ya da host bilgisine g�re partition key belirlenir.
                    return RateLimitPartition.GetFixedWindowLimiter(
                        // Kullan�c� kimli�i varsa partition key olarak kullan�l�r, yoksa host bilgisi kullan�l�r.
                        partitionKey: httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),

                        // Belirli bir zaman diliminde (Fixed Window) istekleri s�n�rlamak i�in limiter yap�land�r�l�yor.
                        factory: partition => new FixedWindowRateLimiterOptions
                        {
                            AutoReplenishment = true, // S�re doldu�unda limit otomatik olarak s�f�rlan�r.
                            PermitLimit = 20, // 1 saniyede en fazla 20 istek yap�labilir.
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst, // S�raya al�nan istekler ilk gelen ilk ��kar prensibine g�re i�lenir.
                            Window = TimeSpan.FromSeconds(1) // 1 saniyelik zaman penceresi belirlenir, bu s�re i�inde en fazla 2 istek yap�labilir.                            
                        });
                });

                // Rate limiting ihlali durumunda geri d�n��
                options.OnRejected = async (context, token) =>
                {
                    Log.Warning("Rate limit a��ld�: {User} - {Host}",
                        context.HttpContext.User.Identity?.Name ?? "Anonim",
                        context.HttpContext.Request.Headers.Host.ToString());

                    context.HttpContext.Response.StatusCode = 429;

                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers["Retry-After"] = retryAfter.TotalSeconds.ToString();
                        await context.HttpContext.Response.WriteAsJsonAsync(
                            new
                            {
                                Message = $"�ok fazla istekde bulundunuz. L�tfen sonra tekrar deneyin {retryAfter.TotalMinutes} dakika sonra.",
                                RetryAfter = retryAfter.TotalSeconds
                            }, cancellationToken: token);
                    }
                    else
                    {
                        await context.HttpContext.Response.WriteAsJsonAsync(
                            new
                            {
                                Message = "�ok fazla istekde bulundunuz. L�tfen sonra tekrar deneyin."
                            }, cancellationToken: token);
                    }
                };
            });

        // Controller ve filtre kay�tlar�
        builder.Services.AddControllers(config =>
        {
            config.Filters.Add<PerformanceLoggingFilter>(); // Global filtre kayd�
        });

        // Authorization
        builder.Services.AddAuthorization();

        // Swagger konfig�rasyonu
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // Dependency Injection (DI) kay�tlar�
        builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        builder.Services.AddScoped<IProductService, ProductService>();
        builder.Services.AddScoped<ICategoryService, CategoryService>();
        builder.Services.AddScoped<ICustomerService, CustomerService>();
        builder.Services.AddScoped<ILogService, LogService>();
        builder.Services.AddScoped<IPostService, PostService>();
        builder.Services.AddScoped<ITokenService, TokenService>();
        builder.Services.AddScoped<IFaqService, FaqService>();
        builder.Services.AddScoped<ICatalogService, CatalogService>();

        // Health checks konfig�rasyonu
        builder.Services.AddHealthChecks()
            .AddSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"), name: "sqlserver");

        var app = builder.Build();

        // Health check endpoint
        app.MapHealthChecks("/health");

        // Middleware'ler
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseMiddleware<RequestTimingMiddleware>();

        // Geli�tirme ortam�nda Swagger kullan�m�
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Rate Limiter'� kullan
        app.UseRateLimiter();

        app.UseHttpsRedirection();

        app.UseAuthentication(); // Authentication kullan�m�
        app.UseAuthorization();  // Authorization kullan�m�

        app.MapControllers();

        app.Run();
    }
}
