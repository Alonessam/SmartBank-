using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests
{
    /// <summary>
    /// Turkish text in the source was once corrupted: each Turkish letter turned into a pair of unrelated characters
    /// because a tool read UTF-8 as the Windows ANSI code page and wrote the wrong characters back. It got as far as
    /// the database (the default transfer category), so that is checked for explicitly. (No example of the corrupted
    /// text appears in this comment, because this file is scanned too.)
    /// </summary>
    public class EncodingHygieneTests
    {
        // A lead character typical of UTF-8 read as ANSI (Ã Ä Å Â) followed by a character from the 0x80-0xBF range as
        // ANSI shows it. Correct Turkish text never contains such a pair.
        private static readonly Regex Mojibake = new(
            "[ÃÄÅÂ][\u0080-¿ŒœŠšŸŽžƒˆ˜–—‘-„†-•…‰‹›€™]",
            RegexOptions.Compiled);

        private static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SmartBank.slnx"))) return dir.FullName;
            }

            throw new FileNotFoundException("Could not find the repository root (SmartBank.slnx).");
        }

        [Fact]
        public void No_source_file_contains_double_encoded_text()
        {
            var root = RepositoryRoot();
            var extensions = new[] { ".cs", ".js", ".html", ".css", ".md", ".sql", ".yml", ".json" };
            var skipped = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                  $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                                  "Migrations" };

            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                if (skipped.Any(s => file.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;

                var text = File.ReadAllText(file, Encoding.UTF8);
                var match = Mojibake.Match(text);
                if (match.Success)
                {
                    var line = text[..match.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{line} '{text.Substring(match.Index, 12).Split('\n')[0]}'");
                }
            }

            Assert.True(offenders.Count == 0, "Double-encoded text found:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void Scripts_are_ascii_only_or_start_with_a_byte_order_mark()
        {
            // Windows PowerShell 5.1 reads a .ps1 file without a byte order mark in the system ANSI code page, so any
            // non-ASCII character in it turns into other characters (and cmd.exe does the same with .bat files).
            // Keep the scripts pure ASCII (or give them a BOM on purpose).
            var root = RepositoryRoot();
            var extensions = new[] { ".ps1", ".sh", ".bat", ".cmd" };
            var skipped = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                  $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}" };

            var scripts = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !skipped.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Assert.NotEmpty(scripts); // the scan itself must find the scripts

            var offenders = new List<string>();
            foreach (var file in scripts)
            {
                var bytes = File.ReadAllBytes(file);
                var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var nonAscii = bytes.Count(b => b > 0x7F);
                if (!hasBom && nonAscii > 0)
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {nonAscii} non-ASCII bytes and no byte order mark");
                }
            }

            Assert.True(offenders.Count == 0, "Scripts must be ASCII-only or start with a BOM:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public async Task A_transfer_without_a_category_is_filed_under_the_correct_Turkish_word()
        {
            await using var context = new SmartBankDbContext(new DbContextOptionsBuilder<SmartBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            var sender = new User { Username = "sender", Tckn = "11111111111", PasswordHash = "x", FullName = "Sender", Email = "s@test.com" };
            var receiver = new User { Username = "receiver", Tckn = "22222222222", PasswordHash = "x", FullName = "Receiver", Email = "r@test.com" };
            context.Users.AddRange(sender, receiver);
            context.Accounts.AddRange(
                new Account { UserId = sender.Id, AccountNumber = "TR0000000000000001", AccountCode = "ACC-1", Balance = 500m, Currency = "TRY" },
                new Account { UserId = receiver.Id, AccountNumber = "TR0000000000000002", AccountCode = "ACC-2", Balance = 0m, Currency = "TRY" });
            await context.SaveChangesAsync();

            var service = new BankingService(context, new FakeOtpDelivery(), new Mock<IMarketRateService>().Object);
            var result = await service.TransferMoneyAsync(sender.Id, new TransferRequestDto
            {
                SourceAccountNumber = "TR0000000000000001",
                DestinationAccountNumber = "TR0000000000000002",
                Amount = 10m,
                Description = "no category"
            });

            Assert.True(result.IsSuccess);
            Assert.Equal("Diğer", result.Data!.Category);
        }
    }
}
