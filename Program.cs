
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
using HeThongHoTroChoThueThietBi.Features.Dashboard.Services;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Organization.Services;
using HeThongHoTroChoThueThietBi.Features.Rental.Configurations;
using HeThongHoTroChoThueThietBi.Features.Rental.Services;

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
builder.Services.AddScoped<UserManagementService>();

// Organization
builder.Services.AddScoped<CompanyService>();

// Rental
builder.Services.Configure<ReservationOptions>(
    builder.Configuration.GetSection(ReservationOptions.SectionName)
);
builder.Services.AddScoped<ReservationService>();
builder.Services.AddScoped<ContractService>();
builder.Services.Configure<LessorOptions>(
    builder.Configuration.GetSection(LessorOptions.SectionName)
);
builder.Services.AddSingleton<ContractPdfGenerator>();

// QuestPDF (xuất hợp đồng PDF): giấy phép Community miễn phí cho dự án doanh thu dưới 1 triệu USD/năm.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// Dashboard
builder.Services.AddScoped<DashboardService>();

// Equipment
builder.Services.AddScoped<EquipmentAvailabilityService>();
builder.Services.AddScoped<EquipmentModelService>();

// Đặt là true nếu muốn dùng Neon, false nếu muốn dùng Local
bool useNeon = builder.Configuration.GetValue<bool>("UseNeonCloud");

var connectionString = useNeon
    ? builder.Configuration.GetConnectionString("NeonDb")
    : builder.Configuration.GetConnectionString("LocalDb");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

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
