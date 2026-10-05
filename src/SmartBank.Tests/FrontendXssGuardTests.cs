using System.Text.RegularExpressions;

namespace SmartBank.Tests
{
    /// <summary>
    /// The frontend builds a lot of HTML with template literals. Text that comes from another user (a transfer description,
    /// a contact alias, a chat message) used to be placed in those templates raw, which allowed stored XSS. These tests
    /// cannot run the browser code, but they pin down the two things that keep the hole closed: the escaping helper is used
    /// at every known sink, and the pages carry a Content-Security-Policy that forbids inline scripts.
    /// </summary>
    public class FrontendXssGuardTests
    {
        private static string WebRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var web = Path.Combine(dir.FullName, "src", "SmartBank.Web");
                if (Directory.Exists(web)) return web;
            }

            throw new DirectoryNotFoundException("Could not find src/SmartBank.Web.");
        }

        private static string Read(string name) => File.ReadAllText(Path.Combine(WebRoot(), name));

        // Interpolations of values that other users control. None of them may appear in a template without esc(...).
        public static TheoryData<string, string> RawSinks => new()
        {
            { "app.js", "${tx.description" },
            { "app.js", "${t.description}" },
            { "app.js", "${msg.content}" },
            { "app.js", "${sess.title}" },
            { "app.js", "${sess.username}" },
            { "app.js", "${c.alias}" },
            { "app.js", "${c.accountNumber}" },
            { "app.js", "account-number\">${acc.accountNumber}" },
            { "app.js", "${acc.accountCode" },
            { "app.js", "${displayName}" },
            { "app.js", "${order.destinationAccountNumber}" },
            { "app.js", "${order.sourceAccountNumber}" },
            { "chat.js", "${msgDto.content}" },
            { "chat.js", "${translatedMsg}" },
            { "chat.js", "${description}" },
            { "chat.js", "${destination}" },
            { "chat.js", "${source}" },
            { "chat.js", "${displayDept}" },
        };

        [Theory]
        [MemberData(nameof(RawSinks))]
        public void User_controlled_values_are_never_interpolated_raw(string file, string rawExpression)
        {
            var text = Read(file);

            Assert.DoesNotContain(rawExpression, text);
        }

        [Fact]
        public void The_access_token_is_never_built_into_a_url_by_hand()
        {
            // URLs end up in logs, history and Referer headers. SignalR asks for the token through accessTokenFactory instead.
            Assert.DoesNotContain("access_token=", Read("chat.js"));
            Assert.DoesNotContain("access_token=", Read("app.js"));
            Assert.Contains("accessTokenFactory", Read("chat.js"));
        }

        [Fact]
        public void Every_authenticated_call_goes_through_the_refreshing_fetch_wrapper()
        {
            var app = Read("app.js");

            Assert.Contains("window.fetch = async function", app);
            Assert.Contains("/auth/refresh", app);
            Assert.Contains("/auth/logout", app);
        }

        [Fact]
        public void The_escaping_helper_exists_and_escapes_quotes_as_well_as_angle_brackets()
        {
            var app = Read("app.js");

            var helper = Regex.Match(app, @"function esc\(value\)\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline);
            Assert.True(helper.Success, "app.js must define function esc(value).");

            foreach (var entity in new[] { "&amp;", "&lt;", "&gt;", "&quot;", "&#39;" })
            {
                Assert.Contains(entity, helper.Groups["body"].Value);
            }
        }

        [Theory]
        [InlineData("index.html")]
        [InlineData("dashboard.html")]
        [InlineData("agent.html")]
        public void Every_page_has_a_csp_that_forbids_inline_scripts(string page)
        {
            var html = Read(page);

            var csp = Regex.Match(html, "<meta http-equiv=\"Content-Security-Policy\" content=\"(?<policy>[^\"]+)\"");
            Assert.True(csp.Success, $"{page} has no Content-Security-Policy meta tag.");

            var scriptSrc = Regex.Match(csp.Groups["policy"].Value, "script-src[^;]*").Value;
            Assert.NotEmpty(scriptSrc);
            Assert.DoesNotContain("unsafe-inline", scriptSrc);
            Assert.DoesNotContain("unsafe-eval", scriptSrc);
            Assert.Contains("object-src 'none'", csp.Groups["policy"].Value);
        }

        [Theory]
        [InlineData("index.html")]
        [InlineData("dashboard.html")]
        [InlineData("agent.html")]
        public void Pages_contain_no_inline_scripts_or_event_attributes(string page)
        {
            var html = Read(page);

            // <script src="..."> is fine; a script element with a body, or an on...="" attribute, would be blocked by the CSP.
            Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"\son[a-z]+\s*=\s*[""']", RegexOptions.IgnoreCase), html);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
