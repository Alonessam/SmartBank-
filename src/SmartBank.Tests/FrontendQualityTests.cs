using System.Text.RegularExpressions;

namespace SmartBank.Tests
{
    /// <summary>
    /// Guards for the things that quietly rot in a hand-written frontend: translations that exist in one language only,
    /// form controls without a label, dialogs that are not dialogs, money inputs that refuse decimals, native browser dialogs.
    /// The browser code itself cannot run in these tests, so they read the source files.
    /// </summary>
    public class FrontendQualityTests
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

        public static TheoryData<string> AllPages => new() { "index.html", "dashboard.html", "agent.html" };

        private static (HashSet<string> En, HashSet<string> Tr) Dictionaries()
        {
            var app = Read("app.js");
            var en = app.IndexOf("    en: {", StringComparison.Ordinal);
            var tr = app.IndexOf("    tr: {", StringComparison.Ordinal);
            var end = app.IndexOf("\n};", tr, StringComparison.Ordinal);
            Assert.True(en > 0 && tr > en && end > tr, "Could not find the en / tr dictionaries in app.js.");

            static HashSet<string> Keys(string block) =>
                Regex.Matches(block, "^\\s+\"(?<key>[^\"]+)\":", RegexOptions.Multiline).Select(m => m.Groups["key"].Value).ToHashSet();

            return (Keys(app[en..tr]), Keys(app[tr..end]));
        }

        [Fact]
        public void English_and_Turkish_have_exactly_the_same_keys()
        {
            var (en, tr) = Dictionaries();

            Assert.Empty(en.Except(tr));
            Assert.Empty(tr.Except(en));
            Assert.True(en.Count > 300, "The dictionary looks truncated.");
        }

        [Fact]
        public void Every_translation_key_used_by_the_pages_and_scripts_exists()
        {
            var (en, _) = Dictionaries();
            var used = new HashSet<string>();

            foreach (var page in new[] { "index.html", "dashboard.html", "agent.html" })
            {
                foreach (Match m in Regex.Matches(Read(page), "data-(?:title-i18n|i18n(?:-[a-z]+)*)=\"(?<key>[^\"]+)\""))
                {
                    used.Add(m.Groups["key"].Value);
                }
            }

            foreach (var script in new[] { "app.js", "chat.js" })
            {
                var code = Read(script);
                foreach (Match m in Regex.Matches(code, "(?<![\\w.])t\\(\\s*\"(?<key>[A-Za-z0-9.]+)\"")) used.Add(m.Groups["key"].Value);
                foreach (Match m in Regex.Matches(code, "errorText\\(\\s*\"(?<key>[A-Za-z0-9]+)\"")) used.Add("err." + m.Groups["key"].Value);
                foreach (Match m in Regex.Matches(code, "errorKey: \"(?<key>[A-Za-z0-9]+)\"")) used.Add("err." + m.Groups["key"].Value);
                foreach (Match m in Regex.Matches(code, "noteKey: \"(?<key>[A-Za-z0-9.]+)\"")) used.Add(m.Groups["key"].Value);
            }

            Assert.Empty(used.Except(en));
        }

        [Theory]
        [InlineData("RateUnavailable")]
        [InlineData("InvalidCurrency")]
        [InlineData("InvalidAction")]
        [InlineData("InvalidAmountScale")]
        [InlineData("InvalidOrderType")]
        [InlineData("InvalidFrequency")]
        [InlineData("DestinationAccountNotFound")]
        [InlineData("CurrencyMismatch")]
        [InlineData("PinRequired")]
        [InlineData("TooManyRequests")]
        [InlineData("AccountNotFound")]
        [InlineData("CannotDeleteLastAccount")]
        [InlineData("ChargeFailed")]
        [InlineData("ConcurrentModification")]
        [InlineData("ContactNotFound")]
        [InlineData("FailedToOpenAssetAccount")]
        [InlineData("InsufficientLimit")]
        [InlineData("InvalidExchangeSource")]
        [InlineData("InvalidRefreshToken")]
        [InlineData("InvalidSourceAccount")]
        [InlineData("MaxCreditCardsLimitReached")]
        [InlineData("OrderNotFound")]
        [InlineData("RateNotFound")]
        [InlineData("SessionNotFound")]
        [InlineData("TargetAccountNotFound")]
        [InlineData("TargetAccountRequired")]
        [InlineData("TransactionFailed")]
        [InlineData("TryAccountNotFound")]
        [InlineData("UnauthorizedAccountAccess")]
        [InlineData("UserNotFound")]
        [InlineData("ValidationError")]
        [InlineData("ConnectionError")]
        [InlineData("TcknAlreadyExists")]
        [InlineData("EmailAlreadyExists")]
        public void Server_error_keys_are_translated_in_both_languages(string errorKey)
        {
            var (en, tr) = Dictionaries();

            Assert.Contains("err." + errorKey, en);
            Assert.Contains("err." + errorKey, tr);
        }

        [Fact]
        public void The_api_error_keys_in_the_backend_all_have_a_translation()
        {
            // Walks up to the backend sources: a new ErrorKey added there without a matching "err.<Key>" fails here.
            var root = Path.GetFullPath(Path.Combine(WebRoot(), "..", ".."));
            var (en, _) = Dictionaries();
            var keys = new HashSet<string>();

            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}SmartBank.Tests{Path.DirectorySeparatorChar}")) continue;

                foreach (Match m in Regex.Matches(File.ReadAllText(file), "Failure\\(\\s*\"(?<key>[A-Za-z0-9]+)\"")) keys.Add(m.Groups["key"].Value);
            }

            var missing = keys.Where(k => !en.Contains("err." + k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, "Backend error keys without a frontend translation: " + string.Join(", ", missing));
        }

        [Theory]
        [MemberData(nameof(AllPages))]
        public void Every_page_declares_its_language_viewport_and_head_metadata(string page)
        {
            var html = Read(page);

            Assert.Matches("<html lang=\"(en|tr)\">", html);
            Assert.Contains("name=\"viewport\"", html);
            Assert.Matches("<meta name=\"description\" content=\"[^\"]{20,}\"", html);
            Assert.Matches("<meta name=\"theme-color\" content=\"#[0-9a-fA-F]{6}\"", html);
            Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\">", html);
            Assert.Matches("<link rel=\"icon\" href=\"data:image/svg\\+xml,", html);
            Assert.Matches("<body[^>]*data-page=\"(login|dashboard|agent)\"", html);
            Assert.Matches("<body[^>]*data-title-i18n=\"title\\.", html);
            Assert.Contains("class=\"skip-link\"", html);
        }

        [Fact]
        public void Pages_name_the_page_in_data_page_and_the_scripts_do_not_sniff_the_url()
        {
            Assert.Contains("data-page=\"login\"", Read("index.html"));
            Assert.Contains("data-page=\"dashboard\"", Read("dashboard.html"));
            Assert.Contains("data-page=\"agent\"", Read("agent.html"));

            foreach (var script in new[] { "app.js", "chat.js" })
            {
                Assert.DoesNotMatch(@"pathname\.includes\(", Read(script));
            }
        }

        [Fact]
        public void Scripts_and_styles_carry_one_matching_cache_buster()
        {
            var versions = new HashSet<string>();
            foreach (var page in new[] { "index.html", "dashboard.html", "agent.html" })
            {
                var html = Read(page);
                var matches = Regex.Matches(html, "(?:app\\.js|chat\\.js|styles\\.css)\\?v=(?<v>[0-9.]+)\"").ToList();
                Assert.True(matches.Count >= 2, $"{page}: scripts and stylesheet need a ?v= cache buster.");
                foreach (var m in matches) versions.Add(m.Groups["v"].Value);
            }

            Assert.Single(versions);
            Assert.Contains($"const APP_VERSION = \"{versions.Single()}\"", Read("app.js"));
        }

        [Fact]
        public void The_chat_script_loads_after_app_js_on_the_pages_that_need_it()
        {
            foreach (var page in new[] { "dashboard.html", "agent.html" })
            {
                var html = Read(page);
                Assert.True(html.IndexOf("app.js?v=", StringComparison.Ordinal) < html.IndexOf("chat.js?v=", StringComparison.Ordinal), $"{page}: app.js must load before chat.js.");
                Assert.True(html.IndexOf("signalr.min.js", StringComparison.Ordinal) < html.IndexOf("app.js?v=", StringComparison.Ordinal), $"{page}: SignalR must load before app.js.");
            }

            Assert.DoesNotContain("chat.js", Read("index.html"));
        }

        [Theory]
        [MemberData(nameof(AllPages))]
        public void Form_controls_have_a_label(string page)
        {
            var html = Read(page);
            var unlabeled = new List<string>();

            foreach (Match m in Regex.Matches(html, "<(?<tag>input|select|textarea)\\b(?<attrs>[^>]*)>"))
            {
                var attrs = m.Groups["attrs"].Value;
                if (Regex.IsMatch(attrs, "type=\"(hidden|radio)\"")) continue; // radios sit inside their label
                var id = Regex.Match(attrs, "\\bid=\"(?<id>[^\"]+)\"").Groups["id"].Value;
                var named = attrs.Contains("aria-label=") || attrs.Contains("aria-labelledby=") ||
                            (id.Length > 0 && Regex.IsMatch(html, $"<label[^>]*\\bfor=\"{Regex.Escape(id)}\""));
                if (!named) unlabeled.Add(id.Length > 0 ? id : m.Value);
            }

            Assert.True(unlabeled.Count == 0, $"{page}: controls without a label: {string.Join(", ", unlabeled)}");
        }

        [Fact]
        public void Icon_only_buttons_have_an_accessible_name()
        {
            foreach (var page in new[] { "index.html", "dashboard.html", "agent.html" })
            {
                foreach (Match m in Regex.Matches(Read(page), "<button\\b(?<attrs>[^>]*)>(?<inner>.*?)</button>", RegexOptions.Singleline))
                {
                    var text = Regex.Replace(m.Groups["inner"].Value, "<[^>]+>", "").Trim();
                    var attrs = m.Groups["attrs"].Value;
                    var hasName = attrs.Contains("aria-label=") || attrs.Contains("data-i18n-aria-label=") ||
                                  Regex.IsMatch(m.Groups["inner"].Value, "data-i18n=");
                    // A button whose only content is a symbol or emoji needs an aria-label.
                    var onlySymbols = text.Length > 0 && !text.Any(char.IsLetterOrDigit);
                    Assert.True(text.Length > 0 ? (!onlySymbols || hasName) : hasName, $"{page}: button without a name: {m.Value}");
                }
            }
        }

        [Theory]
        [MemberData(nameof(AllPages))]
        public void Every_dialog_is_labelled_and_modal(string page)
        {
            var html = Read(page);

            foreach (Match m in Regex.Matches(html, "<div id=\"(?<id>[^\"]+)\" class=\"modal-overlay[^\"]*\"(?<attrs>[^>]*)>"))
            {
                var attrs = m.Groups["attrs"].Value;
                Assert.Contains("role=\"dialog\"", attrs);
                Assert.Contains("aria-modal=\"true\"", attrs);
                Assert.Matches("aria-labelledby=\"[^\"]+\"", attrs);
            }
        }

        [Fact]
        public void Tabs_use_the_tab_roles()
        {
            var html = Read("dashboard.html");

            Assert.Equal(2, Regex.Matches(html, "role=\"tablist\"").Count);
            Assert.Equal(5, Regex.Matches(html, "role=\"tab\"").Count);
            Assert.Equal(5, Regex.Matches(html, "role=\"tabpanel\"").Count);
            Assert.Equal(5, Regex.Matches(html, "aria-selected=\"(true|false)\"").Count);
        }

        [Fact]
        public void Money_inputs_accept_cents_and_ask_for_the_decimal_keyboard()
        {
            // type=number without a step refuses 12.50 in a form that is not novalidate.
            foreach (Match m in Regex.Matches(Read("dashboard.html"), "<input\\b(?<attrs>[^>]*type=\"number\"[^>]*)>"))
            {
                var attrs = m.Groups["attrs"].Value;
                Assert.Contains("step=\"0.01\"", attrs);
                Assert.Contains("min=\"0.01\"", attrs);
                Assert.Contains("inputmode=\"decimal\"", attrs);
            }
        }

        [Fact]
        public void Sensitive_fields_ask_the_browser_for_the_right_keyboard_and_autofill()
        {
            var login = Read("index.html");

            Assert.Matches("id=\"login-password\"[^>]*autocomplete=\"current-password\"", login);
            Assert.Matches("id=\"reg-password\"[^>]*autocomplete=\"new-password\"", login);
            Assert.Matches("id=\"forgot-new-password\"[^>]*autocomplete=\"new-password\"", login);
            Assert.Matches("id=\"login-2fa-code\"[^>]*autocomplete=\"one-time-code\"", login);
            Assert.Matches("id=\"forgot-code\"[^>]*autocomplete=\"one-time-code\"", login);
            Assert.Matches("id=\"otp-code-input\"[^>]*autocomplete=\"one-time-code\"", Read("dashboard.html"));
            foreach (var id in new[] { "login-tckn", "login-password", "login-2fa-code", "reg-tckn", "reg-password", "forgot-tckn", "forgot-code", "forgot-new-password" })
            {
                Assert.Matches($"id=\"{id}\"[^>]*inputmode=\"numeric\"", login);
            }
        }

        [Fact]
        public void The_scripts_never_use_the_native_browser_dialogs()
        {
            foreach (var script in new[] { "app.js", "chat.js" })
            {
                foreach (var raw in Read(script).Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*")) continue;
                    Assert.DoesNotMatch(@"(?<![\w.])(alert|confirm|prompt)\(", line);
                }
            }
        }

        [Fact]
        public void Money_moving_actions_run_through_withBusy()
        {
            var app = Read("app.js");
            Assert.Contains("async function withBusy(button, task)", app);

            // Each of these buttons must be wrapped, or a double click sends the request twice.
            Assert.Contains("withBusy(byId(\"btn-transfer-submit\")", app);
            Assert.Contains("withBusy(byId(\"btn-exchange-submit\")", app);
            Assert.Contains("withBusy(byId(\"btn-cc-pay-submit\")", app);
            Assert.Contains("withBusy(byId(\"btn-cc-charge-submit\")", app);
            Assert.Contains("withBusy(byId(\"btn-submit-standing-order\")", app);
            Assert.Contains("withBusy(byId(\"btn-forgot-submit\")", app);
            Assert.Contains("withBusy(addButton,", app);
            Assert.Contains("withBusy(submit,", app);
            Assert.Contains("withBusy(submitBtn,", app);
            Assert.Contains("withBusy(btnSend,", app);
        }

        [Fact]
        public void Disabled_buttons_look_disabled_and_motion_can_be_reduced()
        {
            var css = Read("styles.css");

            Assert.Contains(".btn:disabled", css);
            Assert.Contains("prefers-reduced-motion: reduce", css);
            Assert.Contains("*:focus-visible", css);
            Assert.DoesNotMatch(@"outline:\s*none", css.Replace("main[tabindex=\"-1\"]:focus {\r\n    outline: none !important;", "").Replace("main[tabindex=\"-1\"]:focus {\n    outline: none !important;", ""));
        }

        [Fact]
        public void The_classes_the_markup_relies_on_are_styled()
        {
            var css = Read("styles.css");

            foreach (var cls in new[] { ".text-muted", ".text-danger", ".loading-spinner", ".btn-warning", ".visually-hidden", ".toast", ".rate-symbol-badge.xau", ".rate-symbol-badge.xag" })
            {
                Assert.Contains(cls, css);
            }
            Assert.Contains("--accent-color:", css);
        }
    }
}
