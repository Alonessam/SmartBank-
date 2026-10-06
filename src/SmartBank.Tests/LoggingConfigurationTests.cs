using System.Text.Json;

namespace SmartBank.Tests
{
    /// <summary>
    /// With the default Information level, EF Core wrote every SQL command to the production log, including the standing-order
    /// worker's query that runs every 30 seconds: the log was mostly noise and the useful lines were hard to find.
    /// </summary>
    public class LoggingConfigurationTests
    {
        private static JsonElement Settings()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var file = Path.Combine(dir.FullName, "src", "SmartBank.API", "appsettings.json");
                if (File.Exists(file)) return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            }

            throw new FileNotFoundException("Could not find src/SmartBank.API/appsettings.json.");
        }

        [Fact]
        public void Sql_commands_are_not_logged_at_the_default_level()
        {
            var levels = Settings().GetProperty("Logging").GetProperty("LogLevel");

            Assert.Equal("Warning", levels.GetProperty("Microsoft.EntityFrameworkCore.Database.Command").GetString());
            Assert.Equal("Information", levels.GetProperty("Default").GetString()); // our own messages stay visible
        }
    }
}
