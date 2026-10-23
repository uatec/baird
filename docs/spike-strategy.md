# How to spike and verify Baird features on the Pi

The method that worked for the Netflix, Lottie and Rive spikes, and the traps that cost hours. Findings are in
[netflix-spike-findings.md](netflix-spike-findings.md). Device access:
- **ssh:** `ssh -o HostKeyAlias=baird baird.local` on the home LAN (user `uatec`, passwordless sudo). The Tailscale name
  `baird` has been unreliable.
- **Logs:** Baird logs to the system journal, so use `journalctl -t baird`, not `journalctl --user`.
- **Service:** `systemctl --user {status,restart} baird.service`.

## Principles

1. **Define "proven" before building, and prove it on the device.** "It builds", "the counter says it played" and "it
   works over ssh" all proved false at some point. Acceptable evidence is a screenshot that shows the thing, a measured
   value moving (e.g. `video.currentTime` advancing between polls), and the app's own log saying why.
2. **Failures must be loud.** Every spike surfaces its reason on screen and in the journal: a red FAILED panel with the
   cause, never a blank screen. Silent catch-and-store-in-a-field (Rive's `LastError`) hid a trivial bug.
3. **Test the real environment, not a lookalike.** The Netflix window bug only existed under the service's Wayland
   session. Every ssh-launched test fell back to X11 and passed. Prefer running the actual app binary under the actual
   service conditions (the autotest hook below).
4. **Keep a control.** Before an experiment, re-run the recipe that last worked. If the control fails, the world changed
   (Netflix signed the Pi out, a rate limit hit), and the experiment means nothing.
5. **Isolate with a switch matrix, not intuition.** When "A works and B doesn't" and you can't see why, list every
   difference, put each behind a temporary env switch in **one** build, and run once per switch. This found E100 in five
   runs after many hours of guessing. Remove the switches afterwards.
6. **Write down what you ruled out**, with the evidence, so nobody repeats it (see the red-herrings table in the findings).
7. **Be careful with real accounts.** Each fresh browser profile or sign-in registers a new Netflix device. Netflix
   signed the Pi out after heavy churn. Reuse the real profile read-only where possible. Copy it only for destructive
   tests, and never run two copies at once.

## Tooling (`spikes/netflix-tooling/`)

| Script | Runs on | Purpose |
|---|---|---|
| `deploy_test_build.sh` | dev PC (repo root) | Publish linux-arm64 exactly like CI (~5 min) and copy it to `~/baird-test` on the Pi. The brew install is untouched. |
| `pi_autotest.sh [netflix\|netflix-search] [test\|installed] [shots]` | Pi | Stop `baird.service`, run the test or installed build with `BAIRD_AUTOTEST=…` in a transient unit, take a `grim` screenshot every 3 s plus focus info, collect journal lines, and **always** restore `baird.service` (trap on exit). |
| `pi_harness.sh <profile> [url]` | Pi | Start a Chromium with Baird's exact Netflix flags, CDP on port 9557, kept behind Baird, for hand-driven experiments. |
| `cdpmini.py` | dev PC | A stdlib-only CDP client (neither machine has a websocket package). Use it through `ssh -N -L 9557:127.0.0.1:9557` (harness) or port 9222/9223 (attach read-only to the app's own browser). |

### The unattended test hook

`BAIRD_AUTOTEST=netflix` makes Baird open the Netflix spike tab 8 s after start and run the flow;
`BAIRD_AUTOTEST=netflix-search` does the same for the search tab. Either logs `[AutoTest] RESULT …` to the journal. It
does nothing unless the variable is set (`MainViewModel.StartAutoTestIfRequested`). Add a case there for any new spike.

Typical loop:

```bash
spikes/netflix-tooling/deploy_test_build.sh                                         # ~5 min
ssh -o HostKeyAlias=baird baird.local 'bash ~/baird-test/pi_autotest.sh netflix test'
scp -o HostKeyAlias=baird -r baird.local:baird-test/shots ./shots                    # then look at the PNGs
```

### Seeing what you can't see

- **Screen:** `grim` (Wayland) captures what the TV shows. For a browser hidden behind Baird, use CDP
  `Page.captureScreenshot` on its page: it renders even when covered.
- **Page state:** evaluate JS over CDP (`cdpmini.STATE` gives membership, video time, error code and path). Netflix's
  player API is `netflix.appContext.state.playerApp.getAPI().videoPlayer`.
- **Network:** `Network.enable` with `maxPostDataSize`, then read `requestWillBeSent` (request bodies, even for requests
  that later fail, such as those to `logs.netflix.com`, which is DNS-blocked) and `Network.getResponseBody`. This is how
  the search API and the MSL error (base64 `errordata`) were found.
- **The app's own browser:** while a test runs, attach read-only to port 9222 (playback) or 9223 (search) and sample its
  state every second. Don't send navigation commands to it.
- **Process context:** compare `/proc/<pid>/cmdline`, `/environ` and `/cgroup` of the app's browser against a working
  one. Select the main browser process, i.e. the one whose args contain `--remote-debugging-port` but not `--type=`.

## Pitfalls that cost real time

- **`pkill -f <pattern>` inside an ssh one-liner kills its own shell**, because the pattern is in the command line. Put
  it in a script file, or exclude `$$` (`ps … | awk -v me=$$ '$1!=me'`).
- **`journalctl --user -t baird-autotest`** finds nothing. Transient-unit output goes to the system journal, so drop
  `--user`. This made the runner wait out its full timeout until fixed.
- **A leftover transient unit** blocks the next `systemd-run --unit=…` ("already loaded"). Run
  `systemctl --user reset-failed <unit>` first.
- **`labwc --reconfigure`** needs `LABWC_PID`. From ssh, use `kill -HUP $(pgrep -x labwc)`.
- **ssh sessions have `XDG_SESSION_TYPE=tty`**, so Chromium launched from ssh uses X11 while the service's uses Wayland.
- **The brew wrapper runs the app from `~/.cache/baird/Baird`.** Loose files beside the install aren't next to the exe,
  so embed assets as Avalonia resources.
- **The shipped build is trimmed with reflection JSON off.** Code that passes desktop tests can throw on the Pi. The test
  project now mirrors that setting.
- **C# verbatim strings holding JS:** `\\s` reaches JS as backslash plus `s`. Prove regexes in a real browser (e.g. a
  local page via `BAIRD_CDP_IT_URL_BASE`, see `Baird.Tests/Services/CdpBrowserLocalIntegrationTests.cs`).
- **Opt-in live tests exist.** `BAIRD_NETFLIX_LIVE=1` runs a logged-out check against real Netflix.
  `BAIRD_NETFLIX_SIGNIN_LIVE=1` does a real sign-in on the dev PC: one attempt, and it deletes its profile afterwards.
  Use them sparingly (device registrations).
- **Updater interference:** the Pi's hourly updater may restart Baird mid-test, and it defers when the TV is "busy".
  Force updates explicitly (command in the findings) when deploying a release you want to test.
- **Shared machine:** other agent sessions may ask for quiet CPU windows. Building arm64 is heavy (~5 min of trimming).
- **CI Release hangs** in `apt-get` occasionally. Cancel and re-run the run instead of waiting.

## Checklist for the next spike

1. Write the pass criterion and what "loud failure" looks like.
2. Add a `BAIRD_AUTOTEST` case and journal logging with a recognisable prefix.
3. Unit-test the pure logic (parsers, classifiers, decision flow with a mock `ICdpBrowserService`).
4. `deploy_test_build.sh`, then `pi_autotest.sh`, then look at the screenshots yourself.
5. If it fails: take a control run first, then the switch matrix, then read the page, network and process state.
6. Record findings (including red herrings) in the findings doc, and commit the tooling you had to invent.
7. Leave the Pi clean: `baird.service` active, no stray Chromium (`ps -eo comm | grep -c '^chromium$'`), no debug ports
   (`ss -ltn | grep -E ':92(22|23)|:9557'`), temporary profile copies deleted.
