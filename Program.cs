
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Npgsql;


namespace Zullo.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            NpgsqlConnection.GlobalTypeMapper.EnableDynamicJson();


            // Add services to the container.
            builder.Services.AddControllers()
              .AddJsonOptions(o =>
    {
                  o.JsonSerializerOptions.Converters.Add(
                       new System.Text.Json.Serialization.JsonStringEnumConverter()
                  );
              });

            // Swagger/OpenAPI
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();



            // Database (PostgreSQL),  lagra profiler   och likes i databasen,  ska raderas sedan om vi inte behöver det
            builder.Services.AddDbContext<AppDbContext>(options =>
             options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));


            builder.Services.AddScoped<Zullo.Api.Services.LikeLimitService>();


            var app = builder.Build();

            app.UseDeveloperExceptionPage();  //tillfärlig kod ska tas bort

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();

                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            // You can keep this even if we don't use it yet ska raderas sedan om vi inte behöver det 
            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
