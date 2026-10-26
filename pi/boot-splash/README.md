# Boot splash

From power-on the TV shows one picture — the Baird wordmark on black — until Baird's start-up animation takes over from
that exact frame (`Baird/NewUi/Screens/Intro.cs`), turns a Nipkow disc around it and fades into Home.

- `splash.png` — rendered from the intro's first frame by the test `IntroTests.Renders_the_boot_splash`:
  `BAIRD_WRITE_SPLASH=$PWD/pi/boot-splash/splash.png dotnet test Baird.Tests --filter Renders_the_boot_splash`
- `baird.plymouth`, `baird.script` — the Plymouth theme (shown from the kernel until the desktop starts).
- `make_intro_lottie.py` — writes `Baird/Assets/Lottie/baird-intro.json` (the disc).

`configure_pi.sh` installs the theme, turns off the firmware's rainbow (`disable_splash=1`), and makes the desktop that
shows between Plymouth and Baird the same picture (wallpaper = splash.png, no taskbar, no desktop icons).
