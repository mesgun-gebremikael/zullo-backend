
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Zullo.Api.Data;
using Zullo.Api.Hubs;

using Npgsql;
using Zullo.Api.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;



namespace Zullo.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // EF tooling needs only the database model, never Firebase/JWT or runtime workers.
            // The tools intercept host construction; this branch is never the serving app.
            if (EF.IsDesignTime)
            {
                builder.Services.AddDbContext<AppDbContext>(options =>
                    options.UseNpgsql(DatabaseConnection.Resolve(builder.Configuration)));
                builder.Build().Run();
                return;
            }

            // Firebase init
            FirebaseApp.Create(new AppOptions()
            {
                Credential = CredentialFactory.FromFile<ServiceAccountCredential>(builder.Configuration["Firebase:CredentialsPath"] ?? "firebase-key.json").ToGoogleCredential()
            });


            // CORS (Flutter Web)
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowFlutterWeb",
                    policy =>
                    {
                        policy
                            .AllowAnyOrigin()
                            .AllowAnyHeader()
                            .AllowAnyMethod();
                    });
            });


            // Controllers + JSON enum support
            builder.Services.AddControllers()
                .AddJsonOptions(o =>
                {
                    o.JsonSerializerOptions.Converters.Add(
                        new System.Text.Json.Serialization.JsonStringEnumConverter()
                    );
                });

            // Swagger
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                    Description = "Skriv: Bearer {din token}"
                });

                options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
                    new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
                     new string[] {}
        }
    });
            });

            // Database (PostgreSQL)
            builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
                DatabaseConnection.CreateDataSource(builder.Configuration));
            builder.Services.AddDbContext<AppDbContext>((services, options) =>
                options.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>()));

            builder.Services.AddScoped<Zullo.Api.Services.LikeLimitService>();
            builder.Services.AddScoped<UserRelationService>();
            builder.Services.AddAuthenticationProtection();
            builder.Services.AddScoped<PushNotificationService>();
            builder.Services.AddSingleton<MessageSendLimiter>();
            builder.Services.AddHttpClient<ICommerceVerifier, RevenueCatVerifier>(client => client.Timeout = TimeSpan.FromSeconds(12));
            builder.Services.AddScoped<CommerceService>();
            builder.Services.AddHostedService<CommerceReconciliation>();
            builder.Services.AddHttpClient<IPhotoProvider, CloudinaryPhotoProvider>(client => client.Timeout = TimeSpan.FromSeconds(50))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
            builder.Services.AddScoped<PhotoLifecycle>();
            builder.Services.AddScoped<PhotoCleanup>();
            builder.Services.AddScoped<LegacyPhotoAdoption>();
            builder.Services.AddHostedService<PhotoCleanupWorker>();

            // ================================
            //  JWT AUTHENTICATION (NYTT)
            // ================================
            var jwtKey = builder.Configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(jwtKey))
                throw new InvalidOperationException("Jwt:Key is missing in configuration.");
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Events = new JwtBearerEvents { OnMessageReceived = ChatBearerAuthentication.ReadHubToken, OnTokenValidated = ChatBearerAuthentication.ValidateAccount };
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey =
                            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey!)),
                        ClockSkew = TimeSpan.FromSeconds(30)
                    };
                });

            builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: new[] { "ready" }, timeout: TimeSpan.FromSeconds(5));
            builder.Services.AddAuthorization();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();

            // This API authenticates with independently signed JWTs, not cookies or
            // persistent Data Protection payloads. SignalR connection tokens live only
            // for the process lifetime. Do not write unused plaintext keys to disk.
            // Revisit this choice before adding cookie auth or protected durable links.
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // .NET 8 also registers a hosted preloader for its persistent key ring.
            // It is unused by the explicit ephemeral provider and otherwise writes
            // plaintext key files anyway. Remove only that framework preloader,
            // keeping all application workers. Container tests guard this contract.
            foreach (var registration in builder.Services.Where(service =>
                service.ServiceType == typeof(IHostedService) &&
                service.ImplementationType?.FullName ==
                    "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService").ToArray())
            {
                builder.Services.Remove(registration);
            }



            var app = builder.Build();

            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";

                    var exceptionFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                    var exception = exceptionFeature?.Error;

                    var env = app.Environment;

                    var response = new Zullo.Api.Dtos.ApiErrorResponseDto
                    {
                        Message = "Something went wrong."
                    };

                    // Visa detalj bara i development så du kan felsöka lokalt
                    if (env.IsDevelopment() && exception != null)
                    {
                        response.Detail = exception.Message;
                    }

                    await context.Response.WriteAsJsonAsync(response);
                });
            });

            app.UseStatusCodePages(async context =>
            {
                var response = context.HttpContext.Response;

                // Returnera enkel JSON även för tomma 401/403/404-svar
                if (string.IsNullOrWhiteSpace(response.ContentType))
                {
                    response.ContentType = "application/json";

                    await response.WriteAsJsonAsync(new Zullo.Api.Dtos.ApiErrorResponseDto
                    {
                        Message = $"Request failed with status code {response.StatusCode}."
                    });
                }
            });

            app.Use(async (context, next) =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                await next();

                stopwatch.Stop();

                var method = context.Request.Method;
                var path = context.Request.Path;
                var statusCode = context.Response.StatusCode;
                var elapsedMs = stopwatch.ElapsedMilliseconds;

                app.Logger.LogInformation(
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms",
                    method,
                    path,
                    statusCode,
                    elapsedMs);
            });

            app.UseRouting();
            app.UseCors("AllowFlutterWeb");
            app.UseRateLimiter();

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // app.UseHttpsRedirection();

            //  VIKTIGT: ordningen är viktig
            app.UseAuthentication();   // <--- NY
            app.UseAuthorization();

            app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
            app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
            app.MapControllers();
            app.MapHub<ChatHub>("/hubs/chat");


            app.Run();
        }
    }
}