
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using HeThongHoTroChoThueThietBi.Features.Identity.Services;
using HeThongHoTroChoThueThietBi.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using CloudinaryDotNet;
using Microsoft.Extensions.Options;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Configurations;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<JwtOptions>(
    builder.Configuration.GetSection("Jwt")
);


//Dang ky service cloud dinary
builder.Services
    .AddOptions<CloudinaryOptions>()
    .Bind(builder.Configuration.GetSection("Cloudinary"))
    .Validate(
        options =>
            !string.IsNullOrWhiteSpace(options.CloudName) &&
            !string.IsNullOrWhiteSpace(options.ApiKey) &&
            !string.IsNullOrWhiteSpace(options.ApiSecret),
        "Cloudinary configuration is invalid."
    )
    .ValidateOnStart();

builder.Services.AddSingleton<Cloudinary>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<CloudinaryOptions>>()
        .Value;

    var account = new Account(
        options.CloudName,
        options.ApiKey,
        options.ApiSecret
    );

    var cloudinary = new Cloudinary(account);

    cloudinary.Api.Secure = true;

    return cloudinary;
});

//

builder.Services.AddScoped<IFileUploadService, CloudinaryUploadService>();

builder.Services.AddScoped<TokenService>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")
    )
);

//Dang ky global exception
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Custom Authentication Handler
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtAuthenticationHandler.SchemeName;
        options.DefaultChallengeScheme = JwtAuthenticationHandler.SchemeName;
        options.DefaultForbidScheme = JwtAuthenticationHandler.SchemeName;
    })
    .AddScheme<AuthenticationSchemeOptions, JwtAuthenticationHandler>(
        JwtAuthenticationHandler.SchemeName,
        _ => { }
    );

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
