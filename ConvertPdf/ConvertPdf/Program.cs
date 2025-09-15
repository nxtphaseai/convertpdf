namespace ConvertPdf
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            // Simple API key middleware
            app.Use(async (context, next) =>
            {
                // configure in appsettings.json or environment
                var configuredKey = builder.Configuration["ApiKey"];

                if (string.IsNullOrEmpty(configuredKey))
                {
                    await next(); // no key configured → no protection
                    return;
                }

                if (!context.Request.Headers.TryGetValue("x-api-key", out var providedKey)
                    || providedKey != configuredKey)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("invalid or missing x-api-key");
                    return;
                }

                await next();
            });

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
