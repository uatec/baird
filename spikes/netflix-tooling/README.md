# Netflix / Pi spike tooling

Scripts used to build, deploy and verify Baird spikes on the Raspberry Pi. How and why to use them is in
[`docs/spike-strategy.md`](../../docs/spike-strategy.md); what they found is in
[`docs/netflix-spike-findings.md`](../../docs/netflix-spike-findings.md).

- `deploy_test_build.sh`: run on the dev PC from the repo root. Builds linux-arm64 like CI and copies it to `~/baird-test` on the Pi.
- `pi_autotest.sh [netflix|netflix-search] [test|installed] [shots]`: run on the Pi. Unattended test with screenshots; always restores `baird.service`.
- `pi_harness.sh <profile> [url]`: run on the Pi. A Chromium with Baird's Netflix flags on CDP port 9557, behind Baird.
- `pi_keys.py KEY...`: run on the Pi with sudo. Presses keys through a uinput device (through labwc, like the real remote).
- `cdpmini.py`: a stdlib-only CDP client for the dev PC, used through an ssh tunnel.
