using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Services
{
    public class DisneyPageClassifierTests
    {
        [Theory]
        [InlineData("https://www.disneyplus.com/identity/login/enter-email", true, false, false, 0, DisneyPageKind.LoginRequired)]
        [InlineData("https://www.disneyplus.com/identity/login/enter-email", false, false, false, 0, DisneyPageKind.Unknown)] // no field: a redirect may be in progress
        [InlineData("https://www.disneyplus.com/en-gb/identity/login/enter-email", true, false, false, 0, DisneyPageKind.LoginRequired)]
        [InlineData("https://www.disneyplus.com/identity/login/passwordless", false, false, false, 0, DisneyPageKind.Unknown)]
        [InlineData("https://www.disneyplus.com/identity/login/enter-password", true, false, false, 0, DisneyPageKind.LoginRequired)]
        [InlineData("https://www.disneyplus.com/en-gb/select-profile", false, false, false, 0, DisneyPageKind.ProfilePicker)]
        [InlineData("https://www.disneyplus.com/en-gb/profiles/loading", false, false, false, 0, DisneyPageKind.Unknown)]
        [InlineData("https://www.disneyplus.com/en-gb/home", false, false, false, 0, DisneyPageKind.Unknown)] // no app shell yet: not proof of a login
        [InlineData("https://www.disneyplus.com/en-gb/home", false, false, false, 0, DisneyPageKind.LoggedIn, true)]
        [InlineData("https://www.disneyplus.com/en-gb/browse/search", false, false, false, 0, DisneyPageKind.Unknown)]
        [InlineData("https://www.disneyplus.com/en-gb/browse/search", false, false, false, 0, DisneyPageKind.LoggedIn, true)]
        [InlineData("https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51", false, false, false, 0, DisneyPageKind.Unknown)]
        [InlineData("https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51", false, false, true, 12.5, DisneyPageKind.VideoPlaying)]
        [InlineData("https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51", false, false, true, 0.0, DisneyPageKind.Unknown)]
        [InlineData("https://example.com/home", false, false, false, 0, DisneyPageKind.Unknown)]
        [InlineData("", false, false, false, 0, DisneyPageKind.Unknown)]
        public void Classifies_by_url_and_signals(string url, bool login, bool picker, bool video, double t, DisneyPageKind expected, bool nav = false)
            => Assert.Equal(expected, DisneyPageClassifier.Classify(url, login, picker, video, t, nav));

        [Fact]
        public void A_login_input_beats_an_app_looking_url()
            => Assert.Equal(DisneyPageKind.LoginRequired, DisneyPageClassifier.Classify("https://www.disneyplus.com/en-gb/home", true, false, false, 0));

        [Fact]
        public void A_profile_picker_beats_a_playing_video()
            => Assert.Equal(DisneyPageKind.ProfilePicker, DisneyPageClassifier.Classify("https://www.disneyplus.com/en-gb/home", false, true, true, 5));
    }

    public class DisneyCredentialsTests
    {
        [Fact]
        public void ToString_hides_the_password() => Assert.DoesNotContain("secret", new DisneyCredentials("a@b.c", "secret").ToString());
    }

    public class DisneySearchParserTests
    {
        // Shape of GET /explore/v1.21/search (observed Oct 2026), trimmed to the fields Baird reads. The infoBlocks are real
        // base64url prefixes carrying "urn:ds:cmp:eva:movie" / "urn:ds:cmp:eva:series".
        internal static string Item(string title, string id, string kind, string? rating = "12+", string? year = "2026", string? imageId = "01a06213-a763")
        {
            var info = Convert.ToBase64String(System.Text.Encoding.Latin1.GetBytes("\u0012$urn:event\u0006browse\u0012\u0010 urn:ds:cmp:eva:" + kind + "/" + id))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var visuals = new JsonObject
            {
                ["title"] = title,
                ["metastringParts"] = new JsonObject
                {
                    ["ratingInfo"] = new JsonObject { ["rating"] = new JsonObject { ["text"] = rating } },
                    ["releaseYearRange"] = new JsonObject { ["startYear"] = year }
                }
            };
            if (imageId != null)
                visuals["artwork"] = new JsonObject { ["standard"] = new JsonObject { ["tile"] = new JsonObject { ["1.78"] = new JsonObject { ["imageId"] = imageId } } } };
            return new JsonObject { ["type"] = "view", ["id"] = id, ["visuals"] = visuals, ["infoBlock"] = info }.ToJsonString();
        }

        internal static string Reply(params string[] items) =>
            "{\"data\":{\"page\":{\"type\":\"page\",\"id\":\"search\",\"containers\":[{\"type\":\"set\",\"items\":[" + string.Join(",", items) + "]}]}}}";

        [Fact]
        public void Parses_titles_kind_rating_year_and_artwork()
        {
            var reply = DisneySearchParser.Parse(200, Reply(
                Item("Star Wars: The Mandalorian and Grogu", "d830ec57-499e-498c-b6b3-2b85a18a5f51", "movie"),
                Item("The Mandalorian", "aaaa", "series", "15+", "2019")));

            Assert.True(reply.Ok);
            Assert.Equal(2, reply.Results.Count);
            var first = reply.Results[0];
            Assert.Equal("Star Wars: The Mandalorian and Grogu", first.Title);
            Assert.Equal("d830ec57-499e-498c-b6b3-2b85a18a5f51", first.EntityId);
            Assert.Equal("movie", first.Kind);
            Assert.Equal("12+", first.Rating);
            Assert.Equal("2026", first.Year);
            Assert.Equal("https://disney.images.edge.bamgrid.com/ripcut-delivery/v2/variant/disney/01a06213-a763/compose?format=webp&width=320", first.ImageUrl);
            Assert.Equal("https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51", first.PlayUrl);
            Assert.Equal("series", reply.Results[1].Kind);
        }

        [Fact]
        public void Skips_duplicates_untitled_and_non_view_items_and_tolerates_missing_artwork()
        {
            var notAView = new JsonObject { ["type"] = "set", ["id"] = "zzz" }.ToJsonString();
            var reply = DisneySearchParser.Parse(200, Reply(
                Item("A", "1", "movie", imageId: null), Item("A again", "1", "movie"), Item("", "2", "movie"), notAView));
            Assert.Single(reply.Results);
            Assert.Null(reply.Results[0].ImageUrl);
        }

        [Fact]
        public void An_empty_result_page_is_ok_with_no_titles()
        {
            var reply = DisneySearchParser.Parse(200, Reply());
            Assert.True(reply.Ok);
            Assert.Empty(reply.Results);
        }

        [Theory]
        [InlineData(401, true)]
        [InlineData(403, true)]
        [InlineData(500, false)]
        public void HTTP_errors_are_reported_and_auth_errors_ask_for_a_new_token(int status, bool needsToken)
        {
            var reply = DisneySearchParser.Parse(status, "{\"errors\":[]}");
            Assert.False(reply.Ok);
            Assert.Equal(needsToken, reply.NeedsNewToken);
            Assert.Contains($"HTTP {status}", reply.ErrorMessage);
        }

        [Fact]
        public void GraphQL_style_errors_and_bad_json_are_reported()
        {
            Assert.Contains("API error", DisneySearchParser.Parse(200, "{\"errors\":[{\"code\":\"x\"}]}").ErrorMessage);
            Assert.Contains("BAD_JSON", DisneySearchParser.Parse(200, "<html>").ErrorMessage);
            Assert.False(DisneySearchParser.Parse(200, null).Ok);
        }

        [Fact]
        public void KindOf_reads_the_urn_and_survives_garbage()
        {
            Assert.Null(DisneySearchParser.KindOf(null));
            Assert.Null(DisneySearchParser.KindOf("!!!not base64"));
        }
    }

    /// <summary>A scripted Disney+ page: answers by what the script asks for, so the real session logic runs unchanged.</summary>
    internal sealed class FakeDisneyBrowser : Baird.Tests.ViewModels.MockCdpBrowserService, ICdpPageDriver
    {
        public Queue<JsonObject> States { get; } = new();
        public JsonObject LastState { get; set; } = new();
        public string? SearchBody { get; set; }
        public string? EntityBody { get; set; }
        public Queue<int> FetchStatuses { get; } = new();
        public Func<JsonObject>? PollReply { get; set; }
        public List<string> ControlScripts { get; } = new();
        /// <summary>Season replies: season id -> (offset -> body). Used for GET season/{id}?limit=100&offset=N.</summary>
        public Dictionary<string, Func<int, string>> SeasonBodies { get; } = new();
        public List<string> SeasonRequests { get; } = new();
        public int PromptClicks { get; private set; }
        public int EndCardCloses { get; private set; }
        public int MouseMoves { get; private set; }
        public List<string> SkipScripts { get; } = new();
        public bool PromptOnScreen { get; set; } = true;
        public int CaptureCalls { get; private set; }
        public int FetchCalls { get; private set; }
        public int SearchStatus { get; set; } = 200;
        public CapturedRequest? Captured { get; set; } = new("https://disney.api.edge.bamgrid.com/explore/v1.21/globalNav", "GET", null,
            new Dictionary<string, string> { ["authorization"] = "Bearer SECRET-TOKEN", ["x-bamsdk-platform"] = "javascript/linux/chrome" });
        public List<string> Typed { get; } = new();
        public List<string> Scripts { get; } = new();
        public string? ProfileClicked { get; set; }
        public int ThrowOnStateReads { get; set; }
        public Queue<string>? ProfileClickResults { get; set; }
        public bool EmailPresent { get; set; } = true;

        public static JsonObject Poll(double t, bool paused = false, bool ended = false, bool found = true, string? url = null, string? prompt = null, double? titleSeconds = null, double? titleLength = null) => new()
        {
            ["found"] = found,
            ["currentTime"] = t,
            ["readyState"] = 4,
            ["paused"] = paused,
            ["ended"] = ended,
            ["prompt"] = prompt,
            ["titleSeconds"] = titleSeconds,
            ["titleLength"] = titleLength,
            ["url"] = url ?? ("https://www.disneyplus.com/en-gb/play/" + DisneyEntityFixtures.Episode1),
            ["title"] = "Disney+"
        };

        public static JsonObject State(string url, bool login = false, bool picker = false, bool video = false, double t = 0, string text = "", bool nav = false) => new()
        {
            ["url"] = url,
            ["title"] = "Disney+",
            ["hasLoginInput"] = login,
            ["hasProfilePicker"] = picker,
            ["isVideo"] = video,
            ["currentTime"] = t,
            ["duration"] = video ? 6000 : 0,
            ["paused"] = false,
            ["videoWidth"] = video ? 854 : 0,
            ["text"] = text,
            ["hasAppNav"] = nav
        };

        public Task<JsonElement?> EvaluateAsync(string script, CancellationToken ct = default)
        {
            Scripts.Add(script);
            string json;
            if (script.Contains("ended: v.ended"))
            {
                json = PollReply != null ? PollReply().ToJsonString() : "null";
            }
            else if (script.Contains("found.click()") && script.Contains("END-CARD-OVERLAY"))
            {
                EndCardCloses++;
                json = "\"clicked\"";
            }
            else if (script.Contains("found.click()"))
            {
                PromptClicks++;
                json = PromptOnScreen ? "\"clicked\"" : "null";
            }
            else if (script.Contains("let button = null"))
            {
                SkipScripts.Add(script);
                json = "\"buttons\"";
            }
            else if (script.Contains("action === 'Pause'"))
            {
                ControlScripts.Add(script);
                json = "\"video\"";
            }
            else if (script.Contains("hasProfilePicker")) { if (ThrowOnStateReads-- > 0) throw new InvalidOperationException("CDP Error: Inspected target navigated or closed"); if (States.Count > 0) LastState = States.Dequeue(); json = LastState.ToJsonString(); }
            else if (script.Contains("return !!document.querySelector('input[type=email]');")) json = EmailPresent ? "true" : "false";
            else if (script.Contains("fetch("))
            {
                FetchCalls++;
                var status = FetchStatuses.Count > 0 ? FetchStatuses.Dequeue() : SearchStatus;
                string? body;
                var seasonKey = SeasonBodies.Keys.FirstOrDefault(k => script.Contains("/season/" + k));
                if (seasonKey != null)
                {
                    var offsetMatch = System.Text.RegularExpressions.Regex.Match(script, @"offset=(\d+)");
                    var offset = offsetMatch.Success ? int.Parse(offsetMatch.Groups[1].Value) : 0;
                    SeasonRequests.Add($"{seasonKey}@{offset}");
                    body = SeasonBodies[seasonKey](offset);
                }
                else body = script.Contains("/page/entity-") ? EntityBody : SearchBody;
                json = JsonValue.Create(new JsonObject { ["status"] = status, ["body"] = body }.ToJsonString())!.ToJsonString();
            }
            else if (script.Contains("profile-avatar")) { json = ProfileClickResults is { Count: > 0 } q ? q.Dequeue() : "\"Robert\""; if (json != "null") ProfileClicked = "Robert"; }
            else json = "true"; // WaitFor conditions and clicks
            return Task.FromResult<JsonElement?>(JsonDocument.Parse(json).RootElement.Clone());
        }

        public Task<bool> TypeIntoAsync(string selector, string text, CancellationToken ct = default) { Typed.Add(selector); return Task.FromResult(true); }
        public Task PressEnterAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) { MouseMoves++; return Task.CompletedTask; }
        public Task<CapturedRequest?> NavigateAndCaptureAsync(string url, Func<CapturedRequest, bool> match, TimeSpan timeout, CancellationToken ct = default)
        {
            CaptureCalls++;
            return Task.FromResult(Captured != null && match(Captured) ? Captured : null);
        }
    }

    public class DisneySpikeViewModelTests
    {
        private const string Play = "https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51";

        private static DisneySpikeViewModel Vm(FakeDisneyBrowser b, DisneyCredentials? creds = null) => new(b, creds)
        {
            SignInTimeout = TimeSpan.FromSeconds(3),
            BufferingTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(10)
        };

        [Fact]
        public async Task Signed_in_playback_is_confirmed_only_when_currentTime_advances_then_the_window_is_revealed()
        {
            var b = new FakeDisneyBrowser();
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/select-profile", picker: true));
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true));
            b.States.Enqueue(FakeDisneyBrowser.State(Play)); // checked after sign-in
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 0.0));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 1.5));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 3.0));
            var vm = Vm(b);

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(DisneySpikeState.Playing, vm.State);
            Assert.Equal("Robert", b.ProfileClicked);
            Assert.True(b.InjectOverlayCalled);
            Assert.True(b.RevealWindowCalled);
            Assert.Contains("854", vm.VideoInfo);
        }

        [Fact]
        public async Task A_video_that_never_advances_fails_loudly_with_the_page_text()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State(Play, video: true, t: 0, text: "Something went wrong") };
            var vm = Vm(b);
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Failed, vm.State);
            Assert.Contains("Something went wrong", vm.StatusMessage);
            Assert.False(b.RevealWindowCalled);
            Assert.True(b.StopCalled);
        }

        [Fact]
        public async Task Login_page_without_credentials_fails_naming_the_config_keys()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/identity/login/enter-email", login: true) };
            var vm = Vm(b);
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Failed, vm.State);
            Assert.Contains("DISNEY_USER", vm.StatusMessage);
            Assert.False(b.RevealWindowCalled);
        }

        [Fact]
        public async Task Configured_credentials_are_typed_once_and_never_logged()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/identity/login/enter-email", login: true) };
            var vm = Vm(b, new DisneyCredentials("me@example.com", "hunter2-secret"));
            await vm.LaunchPlaybackFlowAsync();
            Assert.Contains("input[type=email]", b.Typed);
            Assert.Contains("input[type=password]", b.Typed);
            Assert.Equal(1, b.Typed.Count(s => s == "input[type=password]")); // one attempt per run
            Assert.DoesNotContain(vm.LogEntries, l => l.Contains("hunter2-secret"));
            Assert.DoesNotContain("hunter2-secret", vm.StatusMessage);
            Assert.Equal(DisneySpikeState.Failed, vm.State); // the form never went away in this fake
        }

        [Fact]
        public async Task An_app_url_without_the_signed_in_shell_is_not_treated_as_signed_in()
        {
            // Measured on the Pi: signed-out Disney+ serves /browse/... briefly before redirecting to the login flow.
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/browse/search") };
            var vm = Vm(b);
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Failed, vm.State);
            Assert.Contains("no signed-in Disney+ page", vm.StatusMessage);
        }

        [Fact]
        public async Task The_not_found_page_served_to_signed_out_visitors_starts_the_sign_in()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/browse/search", text: "The page you were looking for cannot be found.") };
            var vm = Vm(b, new DisneyCredentials("me@example.com", "pw"));
            await vm.LaunchPlaybackFlowAsync();
            Assert.Contains("input[type=email]", b.Typed);
            Assert.Contains("input[type=password]", b.Typed);
        }

        [Fact]
        public async Task A_CDP_error_while_the_page_navigates_is_retried_not_fatal()
        {
            var b = new FakeDisneyBrowser { ThrowOnStateReads = 3 };
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 1));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 2));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 3));
            var vm = Vm(b);
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Playing, vm.State);
        }

        [Fact]
        public async Task A_profile_picker_whose_tiles_have_not_rendered_yet_is_retried()
        {
            var b = new FakeDisneyBrowser { ProfileClickResults = new Queue<string>(new[] { "null", "null", "\"Robert\"" }) };
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/select-profile"));
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/select-profile"));
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/select-profile", picker: true));
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true));
            b.States.Enqueue(FakeDisneyBrowser.State(Play));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 1));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 2));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 3));
            var vm = Vm(b);
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Playing, vm.State);
        }

        [Fact]
        public async Task A_signed_in_session_redirected_through_an_identity_url_is_not_signed_in_again()
        {
            // Measured on the Pi after a reboot: /identity/... with no login field, then the profile picker.
            var b = new FakeDisneyBrowser();
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/identity/login/enter-email"));
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/profiles/loading"));
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/select-profile", picker: true));
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true));
            b.States.Enqueue(FakeDisneyBrowser.State(Play));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 1));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 2));
            b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: 3));
            var vm = Vm(b, new DisneyCredentials("me@example.com", "pw"));
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Playing, vm.State);
            Assert.Empty(b.Typed);
        }

        [Fact]
        public async Task If_the_login_page_redirects_away_the_sign_in_types_nothing()
        {
            var b = new FakeDisneyBrowser { EmailPresent = false, LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/browse/search", text: "The page you were looking for cannot be found.") };
            var vm = Vm(b, new DisneyCredentials("me@example.com", "pw"));
            await vm.LaunchPlaybackFlowAsync();
            Assert.Empty(b.Typed);
            Assert.Contains(vm.LogEntries, l => l.Contains("already signed in"));
        }

        private static async Task<(FakeDisneyBrowser, DisneySpikeViewModel)> PlayingVm()
        {
            var b = new FakeDisneyBrowser();
            b.States.Enqueue(FakeDisneyBrowser.State(Play));
            for (int t = 1; t <= 12; t++) b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: t));
            var vm = Vm(b);
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Playing, vm.State);
            return (b, vm);
        }

        [Fact]
        public async Task Back_on_the_remote_while_playing_stops_the_browser_and_returns_to_Baird()
        {
            var (b, vm) = await PlayingVm();
            Assert.False(b.StopCalled); // a playing session has not stopped the browser
            b.RaisePageSignal("{\"type\":\"exit\"}");
            Assert.True(b.StopCalled);
            Assert.Equal(DisneySpikeState.Stopped, vm.State);
        }

        [Theory]
        [InlineData("{\"type\":\"other\"}")]
        [InlineData("{\"x\":1}")]
        [InlineData("not json")]
        [InlineData("[]")]
        public async Task Other_or_malformed_signals_are_ignored(string payload)
        {
            var (b, vm) = await PlayingVm();
            b.RaisePageSignal(payload);
            Assert.False(b.StopCalled);
            Assert.Equal(DisneySpikeState.Playing, vm.State);
        }

        [Fact]
        public void An_exit_signal_when_nothing_is_playing_is_ignored()
        {
            var b = new FakeDisneyBrowser();
            var vm = Vm(b);
            b.RaisePageSignal("{\"type\":\"exit\"}");
            Assert.False(b.StopCalled);
            Assert.Equal(DisneySpikeState.Idle, vm.State);
        }

        [Fact]
        public async Task A_browser_that_will_not_start_fails_with_its_reason()
        {
            var b = new FakeDisneyBrowser { LaunchResult = false, LastError = "profile locked" };
            var vm = Vm(b);
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(DisneySpikeState.Failed, vm.State);
            Assert.Contains("profile locked", vm.StatusMessage);
        }
    }

    public class DisneySearchSpikeViewModelTests
    {
        private static DisneySearchSpikeViewModel Vm(FakeDisneyBrowser b) => new(b)
        {
            SignInTimeout = TimeSpan.FromSeconds(3),
            PollInterval = TimeSpan.FromMilliseconds(10)
        };

        [Fact]
        public async Task Search_returns_the_accounts_titles_and_closes_the_browser()
        {
            var b = new FakeDisneyBrowser
            {
                LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true),
                SearchBody = DisneySearchParserTests.Reply(DisneySearchParserTests.Item("The Mandalorian", "id-1", "series"))
            };
            var vm = Vm(b);
            await vm.SearchAsync();
            Assert.True(vm.Succeeded);
            Assert.Single(vm.Results);
            Assert.Equal("The Mandalorian", vm.Results[0].Title);
            Assert.True(b.StopCalled);
            Assert.False(vm.IsBusy);
        }

        [Fact]
        public async Task The_token_is_sent_to_the_api_but_never_logged()
        {
            var b = new FakeDisneyBrowser
            {
                LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true),
                SearchBody = DisneySearchParserTests.Reply(DisneySearchParserTests.Item("X", "id-1", "movie"))
            };
            var vm = Vm(b);
            await vm.SearchAsync();
            Assert.Contains(b.Scripts, s => s.Contains("fetch(") && s.Contains("SECRET-TOKEN") && s.Contains("query=Mandalorian"));
            Assert.DoesNotContain(vm.LogEntries, l => l.Contains("SECRET-TOKEN"));
            Assert.DoesNotContain("SECRET-TOKEN", vm.Status);
        }

        [Fact]
        public async Task An_expired_token_is_a_loud_failure_not_an_empty_list()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true), SearchStatus = 401, SearchBody = "{}" };
            var vm = Vm(b);
            await vm.SearchAsync();
            Assert.False(vm.Succeeded);
            Assert.StartsWith("FAILED", vm.Status);
            Assert.Contains("401", vm.Status);
        }

        [Fact]
        public async Task No_captured_api_request_is_a_loud_failure()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true), Captured = null };
            var vm = Vm(b);
            await vm.SearchAsync();
            Assert.StartsWith("FAILED", vm.Status);
            Assert.Contains("capture", vm.Status);
        }

        [Fact]
        public async Task Not_signed_in_and_no_credentials_fails_naming_the_config_keys()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/identity/login/enter-email", login: true) };
            var vm = Vm(b);
            await vm.SearchAsync();
            Assert.StartsWith("FAILED", vm.Status);
            Assert.Contains("DISNEY_USER", vm.Status);
        }
    }

    /// <summary>One browser, one tab, two services: attach, hand the tab over, and never disturb a playing service.</summary>
    public class SharedBrowserTests
    {
        private const string Home = "https://www.disneyplus.com/en-gb/home";
        private const string Play = "https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51";

        private static (FakeDisneyBrowser Browser, BrowserHub Hub, DisneyService Disney, NetflixSession Netflix) Setup()
        {
            var b = new FakeDisneyBrowser
            {
                LastState = FakeDisneyBrowser.State(Home, nav: true),
                SessionSequence = () => new NetflixSessionState("https://www.netflix.com/browse", false, true, System.Array.Empty<string>())
            };
            var hub = new BrowserHub(b);
            var disney = new DisneyService(hub, new DisneyCredentials("me@example.com", "pw"))
            {
                SignInTimeout = TimeSpan.FromSeconds(3),
                PollInterval = TimeSpan.FromMilliseconds(10)
            };
            var netflix = new NetflixSession(b, new NetflixCredentials("me@example.com", "pw"), hub: hub)
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SettleDelay = TimeSpan.FromMilliseconds(10)
            };
            return (b, hub, disney, netflix);
        }

        [Fact]
        public async Task Disney_attaches_to_a_browser_that_is_already_open_instead_of_starting_another()
        {
            var (b, hub, disney, _) = Setup();
            b.IsRunning = true; // opened earlier (by the Netflix session)

            var failure = await disney.EnsureReadyAsync(CancellationToken.None);

            Assert.Null(failure);
            Assert.False(b.LaunchCalled);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
            Assert.Equal("Disney+", hub.PageOwner);
        }

        [Fact]
        public async Task Disney_starts_the_browser_on_the_shared_port_and_profile_when_nobody_has()
        {
            var (b, hub, disney, _) = Setup();
            hub.Port = 9222;
            var failure = await disney.EnsureReadyAsync(CancellationToken.None);
            Assert.Null(failure);
            Assert.True(b.LaunchCalled);
        }

        [Fact]
        public async Task A_search_leaves_the_tab_on_the_idle_page_and_the_browser_open()
        {
            var (b, _, disney, _) = Setup();
            b.SearchBody = DisneySearchParserTests.Reply(DisneySearchParserTests.Item("The Mandalorian", "id-1", "series"));

            var outcome = await disney.SearchAsync("mandalorian", CancellationToken.None);

            Assert.True(outcome.Ok);
            Assert.Single(outcome.Reply!.Results);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
            Assert.False(b.StopCalled);
        }

        [Fact]
        public async Task Netflix_takes_the_tab_back_after_Disney_used_it()
        {
            var (b, hub, disney, netflix) = Setup();

            Assert.Null(await netflix.EnsureReadyAsync(CancellationToken.None));
            Assert.Equal("Netflix", hub.PageOwner);
            Assert.Null(await disney.EnsureReadyAsync(CancellationToken.None));
            Assert.Equal("Disney+", hub.PageOwner);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);

            Assert.Null(await netflix.EnsureReadyAsync(CancellationToken.None));

            Assert.Equal("Netflix", hub.PageOwner);
            Assert.Equal(NetflixSession.IdleUrl, b.LastNavigatedUrl); // Netflix's search is a same-origin fetch: it must be on netflix.com
        }

        [Fact]
        public async Task Disney_takes_the_tab_back_after_Netflix_used_it()
        {
            var (b, hub, disney, netflix) = Setup();
            Assert.Null(await disney.EnsureReadyAsync(CancellationToken.None));
            Assert.Null(await netflix.EnsureReadyAsync(CancellationToken.None));
            Assert.Equal("Netflix", hub.PageOwner);

            Assert.Null(await disney.EnsureReadyAsync(CancellationToken.None));

            Assert.Equal("Disney+", hub.PageOwner);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
        }

        [Fact]
        public async Task A_playing_Disney_blocks_Netflix_from_the_tab()
        {
            var (b, _, disney, netflix) = Setup();
            Assert.Null(await disney.EnsureReadyAsync(CancellationToken.None));
            disney.IsPlaybackActive = true;

            var ready = await netflix.EnsureReadyAsync(CancellationToken.None);
            var search = await netflix.SearchAsync("x", CancellationToken.None);

            Assert.Contains("Disney+ is playing", ready);
            Assert.Contains("Disney+ is playing", search.Failure);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl); // untouched
        }

        [Fact]
        public async Task A_playing_Netflix_blocks_Disney_from_the_tab()
        {
            var (b, _, disney, netflix) = Setup();
            Assert.Null(await netflix.EnsureReadyAsync(CancellationToken.None));
            netflix.IsPlaybackActive = true;
            var urlBefore = b.LastNavigatedUrl;

            var ready = await disney.EnsureReadyAsync(CancellationToken.None);
            var search = await disney.SearchAsync("x", CancellationToken.None);

            Assert.Contains("Netflix is playing", ready);
            Assert.Contains("Netflix is playing", search.Failure);
            Assert.Equal(urlBefore, b.LastNavigatedUrl);
        }

        [Fact]
        public async Task Beginning_playback_inside_EnsureReady_marks_it_only_on_success()
        {
            var (b, hub, disney, _) = Setup();
            Assert.Null(await disney.EnsureReadyAsync(CancellationToken.None, beginPlayback: true));
            Assert.Equal("Disney+", hub.ActivePlayback);
            disney.IsPlaybackActive = false;

            b.LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/identity/login/enter-email", login: true);
            var other = new DisneyService(new BrowserHub(b), null) { SignInTimeout = TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromMilliseconds(10) };
            Assert.NotNull(await other.EnsureReadyAsync(CancellationToken.None, beginPlayback: true));
            Assert.Null(other.Hub.ActivePlayback);
        }

        [Fact]
        public async Task The_playback_flag_of_one_service_never_clears_the_other()
        {
            var (_, hub, disney, netflix) = Setup();
            disney.IsPlaybackActive = true;
            netflix.IsPlaybackActive = false; // a stale "playback ended" from Netflix
            Assert.Equal("Disney+", hub.ActivePlayback);
            disney.IsPlaybackActive = false;
            Assert.Null(hub.ActivePlayback);
        }

        [Fact]
        public async Task Shared_Disney_playback_keeps_the_browser_open_and_returns_the_tab_to_the_idle_page()
        {
            var (b, hub, disney, _) = Setup();
            b.States.Enqueue(FakeDisneyBrowser.State(Home, nav: true));
            for (int t = 1; t <= 12; t++) b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: t));
            var vm = new DisneySpikeViewModel(disney) { BufferingTimeout = TimeSpan.FromSeconds(2) };

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(DisneySpikeState.Playing, vm.State);
            Assert.Equal("Disney+", hub.ActivePlayback);
            Assert.True(vm.KeepBrowserOpen);

            b.RaisePageSignal("{\"type\":\"exit\"}"); // remote Back

            Assert.Equal(DisneySpikeState.Stopped, vm.State);
            Assert.False(b.StopCalled);
            Assert.True(b.HideWindowCalled); // the revealed window must go back behind Baird, or the TV keeps showing the browser
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
            Assert.Null(hub.ActivePlayback);
            Assert.Contains("stays open", vm.StatusMessage);
        }

        [Fact]
        public async Task A_failed_shared_playback_returns_the_tab_and_frees_the_browser()
        {
            var (b, hub, disney, _) = Setup();
            b.LastState = FakeDisneyBrowser.State(Home, nav: true);
            var vm = new DisneySpikeViewModel(disney) { BufferingTimeout = TimeSpan.FromMilliseconds(300) };

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(DisneySpikeState.Failed, vm.State);
            Assert.False(b.StopCalled);
            Assert.Null(hub.ActivePlayback);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
        }
    }

    /// <summary>Shapes mirror GET /explore/v1.21/page/entity-{id} (observed Oct 2026), trimmed to the fields Baird reads.</summary>
    internal static class DisneyEntityFixtures
    {
        public const string SeriesId = "422f6dcc-226f-44e7-98d4-22de69b31cf3";
        public const string Episode1 = "8b4a1f96-9a29-45fd-b642-ab99ba2bc0ba";
        public const string Episode2 = "f6bc4f01-2ae0-44f0-b2a8-29c0a8b18fb5";

        private static JsonObject Meta(double? runtimeMs) => runtimeMs is { } ms
            ? new JsonObject { ["runtime"] = new JsonObject { ["runtimeMs"] = ms } }
            : new JsonObject();

        private static JsonObject Playback(string deeplinkId, string text = "CONTINUE") =>
            new() { ["type"] = "playback", ["deeplinkId"] = deeplinkId, ["visuals"] = new JsonObject { ["displayText"] = text } };

        private static JsonObject Episode(string id, string title, int season, int number, double? runtimeMs) => new()
        {
            ["type"] = "view",
            ["id"] = id,
            ["visuals"] = new JsonObject
            {
                ["title"] = "The Mandalorian",
                ["episodeTitle"] = title,
                ["seasonNumber"] = season.ToString(),
                ["episodeNumber"] = number.ToString(),
                ["metastringParts"] = Meta(runtimeMs)
            }
        };

        public static string Series(string playId = Episode1, bool withPlayback = true) => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["page"] = new JsonObject
                {
                    ["visuals"] = new JsonObject { ["title"] = "The Mandalorian", ["metastringParts"] = Meta(null) },
                    ["actions"] = withPlayback
                        ? new JsonArray(Playback(playId), Playback(playId, "RESTART"), new JsonObject { ["type"] = "trailer", ["deeplinkId"] = "trailer-id" })
                        : new JsonArray(new JsonObject { ["type"] = "trailer", ["deeplinkId"] = "trailer-id" }),
                    ["containers"] = new JsonArray(
                        new JsonObject { ["type"] = "set", ["id"] = "suggested", ["items"] = new JsonArray() },
                        new JsonObject
                        {
                            ["type"] = "episodes",
                            ["seasons"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "season",
                                ["items"] = new JsonArray(
                                    Episode(Episode1, "Chapter 1: The Mandalorian", 1, 1, 2_037_000),
                                    Episode(Episode2, "Chapter 2: The Child", 1, 2, 1_860_000))
                            })
                        })
                }
            }
        }.ToJsonString();

        public const string Episode3 = "ef62b353-bdc2-464a-b85c-5d5d7c14e8c1";

        /// <summary>A series with two seasons: season 1 has two episodes (one without a runtime), season 2 has one.</summary>
        public static string TwoSeasons() => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["page"] = new JsonObject
                {
                    ["visuals"] = new JsonObject { ["title"] = "The Mandalorian", ["metastringParts"] = Meta(null) },
                    ["actions"] = new JsonArray(Playback(Episode1)),
                    ["containers"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "episodes",
                        ["seasons"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "season",
                                ["visuals"] = new JsonObject { ["name"] = "Season 1" },
                                ["items"] = new JsonArray(
                                    Episode(Episode1, "Chapter 1: The Mandalorian", 1, 1, 2_037_000),
                                    Episode(Episode2, "Chapter 2: The Child", 1, 2, null))
                            },
                            new JsonObject
                            {
                                ["type"] = "season",
                                ["visuals"] = new JsonObject { ["name"] = "Season 2" },
                                ["items"] = new JsonArray(Episode(Episode3, "Chapter 9: The Marshal", 2, 1, 2_700_000))
                            })
                    })
                }
            }
        }.ToJsonString();

        public const string LazySeason1 = "season-1-id";
        public const string LazySeason2 = "season-2-id";
        public const string LazyExclusives = "exclusives-id";

        private static JsonObject LazySeasonEntry(string id, string name, int total) => new()
        {
            ["type"] = "season",
            ["id"] = id,
            ["visuals"] = new JsonObject { ["name"] = name },
            ["pagination"] = new JsonObject { ["hasMore"] = true, ["currentOffset"] = 0, ["totalCount"] = total }
        };

        /// <summary>Shaped like The Simpsons' entity page: the seasons are listed with a total and NO episodes; "Exclusives" is not a season.</summary>
        public static string LazySeries() => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["page"] = new JsonObject
                {
                    ["visuals"] = new JsonObject { ["title"] = "The Simpsons", ["metastringParts"] = Meta(null) },
                    ["actions"] = new JsonArray(Playback("s1e1-id")),
                    ["containers"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "episodes",
                        ["seasons"] = new JsonArray(
                            LazySeasonEntry(LazyExclusives, "Exclusives", 6),
                            LazySeasonEntry(LazySeason2, "Season 2", 3),
                            LazySeasonEntry(LazySeason1, "Season 1", 13))
                    })
                }
            }
        }.ToJsonString();

        /// <summary>A season's own reply: <paramref name="count"/> episodes numbered from <paramref name="first"/>.</summary>
        public static string SeasonReply(int season, int first, int count, bool hasMore, int total)
        {
            var items = new JsonArray();
            for (var i = 0; i < count; i++)
                items.Add(Episode($"s{season}e{first + i}-id", $"Episode {first + i}", season, first + i, 1_434_000));
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["season"] = new JsonObject
                    {
                        ["type"] = "season",
                        ["items"] = items,
                        ["pagination"] = new JsonObject { ["hasMore"] = hasMore, ["currentOffset"] = first - 1, ["totalCount"] = total }
                    }
                }
            }.ToJsonString();
        }

        public static string Film(string id, double runtimeMs = 8_062_084) => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["page"] = new JsonObject
                {
                    ["visuals"] = new JsonObject { ["title"] = "Star Wars: The Mandalorian and Grogu", ["metastringParts"] = Meta(runtimeMs) },
                    ["actions"] = new JsonArray(Playback(id)),
                    ["containers"] = new JsonArray()
                }
            }
        }.ToJsonString();
    }

    public class DisneyEntityParserTests
    {
        [Fact]
        public void A_series_page_gives_the_next_episode_its_length_and_a_label()
        {
            var target = DisneySearchParser.ParseEntityPlayTarget(200, DisneyEntityFixtures.Series(DisneyEntityFixtures.Episode2), out var failure);

            Assert.Null(failure);
            Assert.Equal(DisneyEntityFixtures.Episode2, target!.PlayId);
            Assert.Equal(1860, target.RuntimeSeconds);
            Assert.Equal("The Mandalorian — S1:E2 Chapter 2: The Child", target.Label);
            Assert.Equal("https://www.disneyplus.com/en-gb/play/" + DisneyEntityFixtures.Episode2, target.PlayUrl);
        }

        [Fact]
        public void A_film_page_gives_its_play_id_and_the_length_from_the_page()
        {
            var target = DisneySearchParser.ParseEntityPlayTarget(200, DisneyEntityFixtures.Film("film-1"), out _);

            Assert.Equal("film-1", target!.PlayId);
            Assert.Equal(8062.084, target.RuntimeSeconds, 3);
            Assert.Equal("Star Wars: The Mandalorian and Grogu", target.Label);
        }

        [Fact]
        public void An_episode_without_a_runtime_has_an_unknown_length_not_the_series_one()
        {
            var json = DisneyEntityFixtures.Series().Replace("2037000", "0");
            var target = DisneySearchParser.ParseEntityPlayTarget(200, json, out _);
            Assert.Equal(0, target!.RuntimeSeconds);
        }

        [Fact]
        public void A_page_without_a_playback_action_says_it_cannot_be_played()
        {
            var target = DisneySearchParser.ParseEntityPlayTarget(200, DisneyEntityFixtures.Series(withPlayback: false), out var failure);
            Assert.Null(target);
            Assert.Contains("no playback action", failure);
        }

        [Theory]
        [InlineData(404, "{}", "HTTP 404")]
        [InlineData(200, "", "no reply")]
        [InlineData(200, "<html>", "BAD_JSON")]
        [InlineData(200, "{\"data\":{}}", "no data.page")]
        public void Errors_are_reported_with_the_reason(int status, string body, string expected)
        {
            var target = DisneySearchParser.ParseEntityPlayTarget(status, body, out var failure);
            Assert.Null(target);
            Assert.Contains(expected, failure);
        }

        [Fact]
        public void Search_results_carry_the_runtime_and_description()
        {
            var item = new JsonObject
            {
                ["type"] = "view",
                ["id"] = "x1",
                ["visuals"] = new JsonObject
                {
                    ["title"] = "A Film",
                    ["description"] = new JsonObject { ["brief"] = "Short text." },
                    ["metastringParts"] = new JsonObject { ["runtime"] = new JsonObject { ["runtimeMs"] = 5_400_000 } }
                }
            }.ToJsonString();

            var result = DisneySearchParser.Parse(200, DisneySearchParserTests.Reply(item)).Results.Single();

            Assert.Equal(5400, result.RuntimeSeconds);
            Assert.Equal("Short text.", result.Description);
        }
    }

    public class DisneyProviderTests
    {
        private static DisneySearchResult Film() => new("Star Wars: The Mandalorian and Grogu", "film-1", "https://img/x", "movie", "12+", "2026", 8062, "A film.");
        private static DisneySearchResult Series() => new("The Mandalorian", "series-1", null, "series", "14+", "2019");

        [Fact]
        public void A_film_becomes_a_video_item_that_names_the_kind_year_and_rating()
        {
            var item = DisneyProvider.ToMediaItem(Film());

            Assert.Equal("disney:film-1", item.Id);
            Assert.Equal("Disney+", item.Source);
            Assert.Equal("Film · 2026 · 12+", item.Details);
            Assert.Equal("A film.", item.Synopsis);
            Assert.Equal(TimeSpan.FromSeconds(8062), item.Duration);
            Assert.False(item.IsLive);
            Assert.True(DisneyProvider.IsFilm(item));
            Assert.Equal("film-1", DisneyProvider.EntityIdOf(item.Id));
        }

        [Fact]
        public void A_series_is_a_container_item_without_a_stream_url_because_a_series_id_is_not_a_play_id()
        {
            var item = DisneyProvider.ToMediaItem(Series());

            Assert.Equal("Series · 2019 · 14+", item.Details);
            Assert.Equal(MediaType.Brand, item.Type); // a container: its seasons and episodes are its children
            Assert.Equal("", item.StreamUrl);
            Assert.False(DisneyProvider.IsFilm(item));
            Assert.Equal(TimeSpan.Zero, item.Duration);
            Assert.Equal("", item.ImageUrl);
        }

        [Fact]
        public void IsDisney_matches_only_the_Disney_source()
        {
            Assert.True(DisneyProvider.IsDisney("Disney+"));
            Assert.False(DisneyProvider.IsDisney("Netflix"));
            Assert.False(DisneyProvider.IsDisney(null));
        }

        private static (FakeDisneyBrowser Browser, DisneyProvider Provider) Setup()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true) };
            var service = new DisneyService(new BrowserHub(b), new DisneyCredentials("me@example.com", "pw"))
            {
                SignInTimeout = TimeSpan.FromSeconds(3),
                PollInterval = TimeSpan.FromMilliseconds(10)
            };
            return (b, new DisneyProvider(service));
        }

        [Fact]
        public async Task Search_returns_the_account_catalogue_as_items()
        {
            var (b, provider) = Setup();
            b.SearchBody = DisneySearchParserTests.Reply(DisneySearchParserTests.Item("The Mandalorian", "series-1", "series"));

            var items = (await provider.SearchAsync("mandalorian")).ToList();

            Assert.Single(items);
            Assert.Equal("The Mandalorian", items[0].Name);
        }

        [Fact]
        public async Task A_blank_query_makes_no_request()
        {
            var (b, provider) = Setup();
            Assert.Empty(await provider.SearchAsync("   "));
            Assert.Equal(0, b.FetchCalls);
        }

        [Fact]
        public async Task A_Disney_failure_is_loud_so_the_search_screen_marks_the_provider_failed()
        {
            var (b, provider) = Setup();
            b.SearchStatus = 500;
            b.SearchBody = "{}";

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync("x"));

            Assert.Contains("Disney+ search failed", ex.Message);
            Assert.Contains("500", ex.Message);
        }
    }

    public class DisneyTokenAndResolveTests
    {
        private static (FakeDisneyBrowser Browser, DisneyService Service) Setup()
        {
            var b = new FakeDisneyBrowser
            {
                LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true),
                SearchBody = DisneySearchParserTests.Reply(DisneySearchParserTests.Item("The Mandalorian", "series-1", "series")),
                EntityBody = DisneyEntityFixtures.Series()
            };
            var service = new DisneyService(new BrowserHub(b), new DisneyCredentials("me@example.com", "pw"))
            {
                SignInTimeout = TimeSpan.FromSeconds(3),
                PollInterval = TimeSpan.FromMilliseconds(10)
            };
            return (b, service);
        }

        [Fact]
        public async Task The_token_is_captured_once_and_reused_by_later_calls()
        {
            var (b, service) = Setup();

            await service.SearchAsync("a", CancellationToken.None);
            await service.SearchAsync("b", CancellationToken.None);
            await service.ResolvePlayTargetAsync("series-1", isFilm: false, 0, CancellationToken.None);

            Assert.Equal(1, b.CaptureCalls);
            Assert.Equal(3, b.FetchCalls);
        }

        [Fact]
        public async Task A_rejected_token_is_replaced_once_and_the_call_retried()
        {
            var (b, service) = Setup();
            await service.SearchAsync("a", CancellationToken.None); // token cached
            b.FetchStatuses.Enqueue(401); // then Disney+ rejects it

            var outcome = await service.SearchAsync("b", CancellationToken.None);

            Assert.True(outcome.Ok);
            Assert.Equal(2, b.CaptureCalls);
        }

        [Fact]
        public async Task A_token_that_keeps_being_rejected_ends_in_a_loud_failure_after_one_retry()
        {
            var (b, service) = Setup();
            b.SearchStatus = 403;

            var outcome = await service.SearchAsync("a", CancellationToken.None);

            Assert.False(outcome.Ok);
            Assert.Contains("403", outcome.Failure);
            Assert.Equal(2, b.CaptureCalls); // first token, then one fresh one: never a loop
        }

        [Fact]
        public async Task A_cached_search_does_not_touch_the_page_again()
        {
            var (b, service) = Setup();
            await service.SearchAsync("a", CancellationToken.None);
            var urlAfterFirst = b.LastNavigatedUrl;
            Assert.Equal(DisneyService.IdleUrl, urlAfterFirst);

            var navigationsBefore = b.NavigatedUrls.Count;
            await service.SearchAsync("b", CancellationToken.None);

            Assert.Equal(navigationsBefore, b.NavigatedUrls.Count); // still on the idle page: no navigation was needed
        }

        [Fact]
        public async Task Resolving_a_series_returns_the_episode_runtime_and_label_and_leaves_the_tab_idle()
        {
            var (b, service) = Setup();

            var (target, failure) = await service.ResolvePlayTargetAsync("series-1", isFilm: false, 0, CancellationToken.None);

            Assert.Null(failure);
            Assert.Equal(DisneyEntityFixtures.Episode1, target!.PlayId);
            Assert.Equal(2037, target.RuntimeSeconds);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
        }

        [Fact]
        public async Task A_film_whose_page_cannot_be_read_is_played_by_its_entity_id_with_the_known_runtime()
        {
            var (b, service) = Setup();
            b.EntityBody = "{}";
            b.FetchStatuses.Enqueue(500);

            var (target, failure) = await service.ResolvePlayTargetAsync("film-1", isFilm: true, 8062, CancellationToken.None);

            Assert.Null(failure);
            Assert.Equal("film-1", target!.PlayId);
            Assert.Equal(8062, target.RuntimeSeconds);
        }

        [Fact]
        public async Task A_series_whose_page_cannot_be_read_is_a_loud_failure_never_a_wrong_play_id()
        {
            var (b, service) = Setup();
            b.EntityBody = DisneyEntityFixtures.Series(withPlayback: false);

            var (target, failure) = await service.ResolvePlayTargetAsync("series-1", isFilm: false, 0, CancellationToken.None);

            Assert.Null(target);
            Assert.Contains("Could not find what to play", failure);
        }

        [Fact]
        public async Task The_runtime_known_from_search_fills_in_when_the_page_has_none()
        {
            var (b, service) = Setup();
            b.EntityBody = DisneyEntityFixtures.Film("film-1", runtimeMs: 0);

            var (target, _) = await service.ResolvePlayTargetAsync("film-1", isFilm: true, 7000, CancellationToken.None);

            Assert.Equal(7000, target!.RuntimeSeconds);
        }

        [Fact]
        public async Task Resolving_is_refused_while_Netflix_plays()
        {
            var (_, service) = Setup();
            service.Hub.ActivePlayback = "Netflix";

            var (target, failure) = await service.ResolvePlayTargetAsync("series-1", false, 0, CancellationToken.None);

            Assert.Null(target);
            Assert.Contains("Netflix is playing", failure);
        }

        [Fact]
        public async Task Start_without_credentials_does_nothing()
        {
            var b = new FakeDisneyBrowser();
            var service = new DisneyService(new BrowserHub(b), null);
            service.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
            await Task.Delay(100);
            Assert.False(b.LaunchCalled);
            service.Stop();
        }

        [Fact]
        public async Task Start_gets_Disney_ready_and_takes_the_token_in_the_background_then_leaves_the_tab_idle()
        {
            var (b, service) = Setup();
            service.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(50));

            for (int i = 0; i < 100 && !service.Session.HasApiToken; i++) await Task.Delay(50);
            service.Stop();

            Assert.True(service.Session.HasApiToken);
            Assert.Equal(1, b.CaptureCalls);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);

            var outcome = await service.SearchAsync("a", CancellationToken.None); // the first search needs no capture
            Assert.True(outcome.Ok);
            Assert.Equal(1, b.CaptureCalls);
        }

        [Fact]
        public async Task The_background_start_stays_away_while_something_plays()
        {
            var (b, service) = Setup();
            service.Hub.ActivePlayback = "Netflix";
            service.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(30));
            await Task.Delay(200);
            service.Stop();
            Assert.False(b.LaunchCalled);
            Assert.False(service.Session.HasApiToken);
        }
    }
}

