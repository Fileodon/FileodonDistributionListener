
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using FileodonDistributionListener.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Renci.SshNet;

namespace FileodonDistributionListener;

public sealed record SftpEndpoint(
    string Host,
    int Port,
    string Username,
    string PrivateKeyPem);

public sealed record TransferFile(
    string SourceRemotePath,
    string DestinationRemotePath,
    long Length);

public sealed record TransferRequest(
    SftpEndpoint Source,
    SftpEndpoint Destination,
    IReadOnlyList<TransferFile> Files);

public enum TransferJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

public sealed record TransferFileProgress(
    string SourceRemotePath,
    string DestinationRemotePath,
    long TotalBytes,
    long UploadedBytes,
    bool Completed,
    string? Error);

public sealed record TransferJobSnapshot(
    Guid JobId,
    TransferJobStatus Status,
    int TotalFiles,
    long TotalBytes,
    long UploadedBytes,
    double ProgressPercentage,
    IReadOnlyList<TransferFileProgress> Files,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record TransferWorkItem(
    Guid JobId,
    TransferRequest Request);

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

        builder.Services.AddControllers()
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddOpenApi();

        string publicKeyPath = builder.Configuration["Jwt:PublicKeyPath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "public.pem");
        if (!File.Exists(publicKeyPath))
        {
            throw new InvalidOperationException(
                $"JWT public key was not found at '{publicKeyPath}'. Run the installer or set Jwt__PublicKeyPath.");
        }

        using ECDsa publicKey = ECDsa.Create();
        publicKey.ImportFromPem(File.ReadAllText(publicKeyPath));

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "Fileodon Master Server",
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new ECDsaSecurityKey(publicKey),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });
        builder.Services.AddAuthorization();

        Channel<TransferWorkItem> queue = Channel.CreateBounded<TransferWorkItem>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        builder.Services.AddSingleton(queue);
        builder.Services.AddSingleton<TransferJobStore>();
        builder.Services.AddSingleton<TransferQueue>();
        builder.Services.AddHostedService<TransferWorker>();

        var app = builder.Build();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.Run();
    }
}
