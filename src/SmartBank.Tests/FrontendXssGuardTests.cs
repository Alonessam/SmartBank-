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

        [Theory]
        [InlineData("fromSystem && content.includes(\"[SESSION_TRANSFERRED:\")")]
        [InlineData("fromAi && content.includes(\"[CONFIRM_TRANSFER:\")")]
        [InlineData("fromSystem && content.includes(\"[TRANSFER_SUCCESS:\")")]
        [InlineData("fromSystem && content.includes(\"[TRANSFER_FAILED:\")")]
        public void Chat_markers_become_cards_only_when_they_come_from_the_right_sender(string gatedCondition)
        {
            var chat = Read("chat.js");

            Assert.Contains(gatedCondition, chat);
            // No marker check may exist without a sender condition in front of it.
            Assert.DoesNotMatch(new Regex(@"if \(content\.includes\(""\[(SESSION_TRANSFERRED|CONFIRM_TRANSFER|TRANSFER_SUCCESS|TRANSFER_FAILED):"), chat);
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
        public void There_is_no_html_escaping_helper_because_nothing_is_parsed_as_html()
        {
            // v1.3 builds all dynamic DOM with h() / textContent; the old esc() helper for innerHTML templates was removed.
            // If a template-based sink ever comes back, this test has to be replaced by the escaping tests again.
            Assert.DoesNotMatch(@"function esc\(", Read("app.js"));
            Assert.DoesNotMatch(@"\.innerHTML\s*=", Read("app.js"));
            Assert.DoesNotMatch(@"\.innerHTML\s*=", Read("chat.js"));
        }

        [Fact]
        public void A_page_that_is_framed_by_another_site_hides_itself()
        {
            // A <meta> CSP cannot carry frame-ancestors, so the script does what the header would do.
            var app = Read("app.js");

            Assert.Contains("window.top !== window.self", app);
            Assert.Contains("document.documentElement.hidden = true", app);
        }

        [Fact]
        public void The_confetti_script_is_pinned_to_the_hash_that_was_checked_against_the_file()
        {
            // Verified by downloading dist/confetti.browser.min.js of canvas-confetti 1.6.0 and hashing it (sha384). If the version is
            // changed, recompute the hash: a wrong one blocks the script and the celebration silently never plays.
            var html = Read("dashboard.html");

            Assert.Contains("canvas-confetti@1.6.0/dist/confetti.browser.min.js\" integrity=\"sha384-HAH79XdRvHr6axVGh4xQWVCp14kcd32bNk4Xu0sHDHtFQ42n6BAM8ykvB47dGz6D\"", html);
            Assert.Contains("useWorker: false", Read("app.js")); // a blob: worker would be blocked by the CSP
        }

        [Fact]
        public void The_signalr_client_does_not_log_the_websocket_address_with_the_token()
        {
            // At the default level the client prints "WebSocket connected to wss://...?access_token=<jwt>" to the console.
            var chat = Read("chat.js");

            Assert.Contains("configureLogging(signalR.LogLevel.Warning)", chat);
            Assert.DoesNotMatch(@"console\.(log|debug|info)\(", chat);
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

        // ------------------------------------------------------------------------------------------------------------
        // v1.3: third-party scripts, the CSP of each page and the remaining DOM sinks
        // ------------------------------------------------------------------------------------------------------------
        private static readonly string[] Pages = { "index.html", "dashboard.html", "agent.html" };

        public static TheoryData<string> AllPages => new() { "index.html", "dashboard.html", "agent.html" };

        private static string PolicyOf(string html)
        {
            var csp = Regex.Match(html, "<meta http-equiv=\"Content-Security-Policy\" content=\"(?<policy>[^\"]+)\"");
            Assert.True(csp.Success, "The page has no Content-Security-Policy meta tag.");
            return csp.Groups["policy"].Value;
        }

        private static string Directive(string policy, string name)
        {
            return Regex.Match(policy, name + "[^;]*").Value;
        }

        private static List<Match> ExternalScripts(string html)
        {
            return Regex.Matches(html, "<script\\b[^>]*\\bsrc=\"(?<src>https?://[^\"]+)\"[^>]*>", RegexOptions.IgnoreCase).ToList();
        }

        [Theory]
        [MemberData(nameof(AllPages))]
        public void Every_external_script_is_pinned_and_integrity_checked(string page)
        {
            foreach (var script in ExternalScripts(Read(page)))
            {
                var tag = script.Value;
                var src = script.Groups["src"].Value;

                Assert.Matches("integrity=\"sha384-[A-Za-z0-9+/=]{64}\"", tag);
                Assert.Contains("crossorigin=\"anonymous\"", tag);
                // A version in the path (".../8.0.0/..." or "...@1.6.0/...") and never a moving target.
                Assert.Matches(@"[@/]\d+\.\d+\.\d+/", src);
                Assert.DoesNotContain("@latest", src);
            }
        }

        [Theory]
        [MemberData(nameof(AllPages))]
        public void The_csp_allows_exactly_the_scripts_the_page_loads(string page)
        {
            var html = Read(page);
            var scriptSrc = Directive(PolicyOf(html), "script-src");
            var sources = scriptSrc.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            var external = ExternalScripts(html).Select(m => m.Groups["src"].Value).ToList();

            Assert.Contains("'self'", sources);
            Assert.DoesNotContain("'unsafe-inline'", sources);
            Assert.DoesNotContain("'unsafe-eval'", sources);

            // Every external script is covered by a path-restricted source, and every host source is used by a script.
            foreach (var src in external)
            {
                Assert.Contains(sources, source => source.EndsWith('/') && src.StartsWith(source, StringComparison.Ordinal));
            }
            foreach (var source in sources.Where(s => s.StartsWith("https://", StringComparison.Ordinal)))
            {
                Assert.True(source.EndsWith('/') && source.Count(c => c == '/') > 3, $"{page}: script source '{source}' must be restricted to a path.");
                Assert.Contains(external, src => src.StartsWith(source, StringComparison.Ordinal));
            }
        }

        [Fact]
        public void The_login_page_loads_no_third_party_script()
        {
            var html = Read("index.html");

            Assert.Empty(ExternalScripts(html));
            Assert.Equal("script-src 'self'", Directive(PolicyOf(html), "script-src"));
        }

        [Theory]
        [MemberData(nameof(AllPages))]
        public void The_csp_has_no_wildcards_and_no_third_party_images(string page)
        {
            var policy = PolicyOf(Read(page));

            Assert.DoesNotContain("*", policy);
            Assert.DoesNotContain("qrserver", policy);
            // No inline style attributes anywhere (the markup uses classes, scripts use the CSSOM), so inline styles stay blocked.
            Assert.DoesNotContain("'unsafe-inline'", policy);
            Assert.DoesNotMatch(new Regex(@"\sstyle=""", RegexOptions.IgnoreCase), Read(page));
            Assert.Contains("default-src 'self'", policy);
            Assert.Contains("base-uri 'self'", policy);
            Assert.Contains("form-action 'self'", policy);
            Assert.Equal("img-src 'self' data:", Directive(policy, "img-src"));
            // The API is the only place the page may talk to (plus localhost for local development).
            foreach (var source in Directive(policy, "connect-src").Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                Assert.Contains(source, new[]
                {
                    "'self'", "http://localhost:5038", "ws://localhost:5038",
                    "https://smartbank-fintech-api.onrender.com", "wss://smartbank-fintech-api.onrender.com"
                });
            }
        }

        [Fact]
        public void The_scripts_build_the_dom_without_parsing_html()
        {
            // New code builds elements with h() / textContent; nothing may hand a string to the HTML parser.
            foreach (var file in new[] { "app.js", "chat.js" })
            {
                var code = Read(file);

                Assert.DoesNotMatch(@"\.innerHTML\s*=", code);
                Assert.DoesNotContain("insertAdjacentHTML", code);
                Assert.DoesNotContain("outerHTML", code);
                Assert.DoesNotContain("document.write", code);
                Assert.DoesNotContain("eval(", code);
                Assert.DoesNotContain("new Function", code);
                Assert.DoesNotMatch(@"set(Timeout|Interval)\(\s*[""'`]", code);
            }
        }

        [Fact]
        public void Browser_storage_is_only_touched_through_the_safe_helper()
        {
            var app = Read("app.js");
            var chat = Read("chat.js");

            // Direct localStorage / sessionStorage calls would throw when site data is blocked.
            Assert.DoesNotMatch(@"(?<![\w.])(localStorage|sessionStorage)\.(get|set|remove)Item", app);
            Assert.DoesNotMatch(@"(localStorage|sessionStorage)\.(get|set|remove)Item", chat);
            // The national id is never kept in the browser.
            Assert.DoesNotContain("tckn: data.tckn", app);
        }

        [Fact]
        public void Signing_out_clears_every_key_stops_the_connection_and_leaves_without_history()
        {
            var app = Read("app.js");

            Assert.Matches(@"\[""token"", ""user"", ""refreshToken"", ""tokenExpiresAt""\]", app);
            Assert.Contains("safeSession.remove(\"activeChatSessionId\")", app);
            Assert.Contains("stopSignalRConnection()", app);
            Assert.Contains("window.location.replace(\"index.html\")", app);
            Assert.DoesNotMatch(@"window\.location\.href\s*=", app);
            Assert.Contains("window.addEventListener(\"storage\"", app);
            Assert.Contains("window.addEventListener(\"pageshow\"", app);
        }
    }
}
