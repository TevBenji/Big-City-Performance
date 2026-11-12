# Big City Performance

<img align="right" src="src/BigCityPerformance/Properties/Thumbnail.png" alt="Big City Performance" width="260">

<a href="https://mods.paradoxplaza.com/mods/166756/Windows"><img alt="Paradox Mods: Big City Performance" src="https://img.shields.io/badge/Paradox%20Mods-Big%20City%20Performance-2ea44f"></a>
<img alt="Version" src="https://img.shields.io/badge/version-0.3.4-1f6feb">
<a href="LICENSE"><img alt="Licence" src="https://img.shields.io/badge/licence-GPL--3.0-c2410c"></a>
<img alt="Game version" src="https://img.shields.io/badge/Cities%3A%20Skylines%20II-1.6-0e7490">

A Cities: Skylines II mod that keeps very big cities running at full speed.

A city of a million people in the base game runs at about a twentieth of normal speed: vehicles and cims crawl, and a game
day takes hours. With this mod the same city runs at real time or faster on the second speed button, on a four-core laptop.

<a href="#what-it-does">What it does</a> ·
<a href="#measured">Measured</a> ·
<a href="#settings">Settings</a> ·
<a href="#what-to-expect">What to expect</a> ·
<a href="#works-with-other-mods">Works with other mods</a> ·
<a href="#saves-and-removing-the-mod">Saves and removing the mod</a> ·
<a href="#how-it-works">How it works</a> ·
<a href="#building-from-source">Building from source</a>

## Why big cities slow down

Two things, and neither is the frame rate.

- **The clock waits for routes.** Every trip needs a route, and the game gives each route request a deadline. When the
  route-finder falls behind, the simulation clock slows down and stops until it catches up. A big city asks for thousands of
  routes at once (every hour, as commuters set off), so the clock spends most of its time waiting.
- **Everyone is simulated one by one.** A million people means a million citizens deciding, walking, waiting at stops and
  re-planning trips, every few seconds.

## What it does

**Keep the clock running** (on by default):

- When the route-finder falls behind, new route requests get more time instead of the clock stopping. People set off a
  moment later; the city keeps moving. Emergency services, transit and service vehicles keep the game's own deadlines.
- While it's behind, citizens look for new jobs, and empty taxis look for passengers, less often until it catches up:
  a few at a time, everyone in turn, never all at once.
- Rush hour is spread out. The game sends everyone who starts work at the same hour off in the same second; the mod gives
  each worker a few minutes' head start of their own, so they leave across a window instead. Nobody arrives later.
- Service coverage (how far each school, clinic or station reaches) is recalculated about once a minute instead of every
  few seconds. In a million-person city that recalculation was a quarter to a third of all route searches.

**Limit the people simulated in detail** (on by default, with the limit chosen automatically):

- In a city bigger than the limit, that many people are simulated one by one. The rest stay at home, asleep. They keep
  their homes and their jobs, count in the population, earn their wages, pay rent and taxes, and still buy from the shops
  (as many sales as awake households make, spread over the shops people actually use). They just make no trips.
- Households go to sleep when everyone in them is home or on foot, so no bus or taxi waits for someone who isn't there.
- At overloaded stops, only the first hundred people at each place stay awake. The rest wait asleep and wake in turn as
  the ones in front board. A crowd of 1,900 at one stop is no longer 1,900 people stepping around each other and asking
  for new routes.
- **The limit picks itself.** It starts at 50,000 and, every 20 seconds of play, goes down a fifth when the clock falls
  behind and fewer people would help (the clock waited on routes, or the processor set the pace), or up a tenth when the
  clock keeps up with room to spare. A slow graphics card alone doesn't lower it: fewer people wouldn't draw the city any
  faster. Turn it off to pick the number yourself.
- Right after a city loads, the clock waits a moment while the first households go to sleep, instead of everyone asking
  for routes at once.
- **A count in the population box.** While the city is bigger than the limit, the population box at the bottom of the
  screen also shows how many people are simulated in detail, like `959,129 | 48k AUTO`. Hover it for the details (the
  limit, the game speed, people waiting at stops); click it to open the settings.
- While anyone sleeps, the city-wide counts of households, companies and homes (they feed demand and the statistics) run
  one time in four, about once a second of play instead of four times. Sleepers don't change between counts.
- Cities smaller than the limit are not changed at all.

**Run extra simulation steps** (off by default):

- Below 30 frames a second, the game can't run enough simulation steps to keep normal speed. This lets it run up to
  twice as many per frame: the simulation goes faster, but each frame takes longer.
- Worth trying when your graphics card is the slow part. When your processor is, the frame rate can drop a lot: one
  player went from 30 frames a second to 9–12 for a quarter more simulation.

## Measured

The "1 Million Population City" save (about 960,000 people), on a laptop with a four-core, eight-thread processor and an
RTX 3050 laptop graphics card, with Run extra simulation steps on:

| | Game speed | Frames a second |
|---|---|---|
| The base game | 0.05× | 14 |
| The mod, normal speed (first button) | 0.9× | 20–25 |
| The mod, second button | 1.1–1.7× | 15–23 |
| The same view, paused (the graphics card's limit) | – | 28 |

Short slowdowns remain when a wave of route requests arrives at once (a big hour of commuters, say). The frame rate in a
city this size is mostly the graphics card: the mod can't draw a million-person city faster, only simulate it faster.

## Settings

Options → Big City Performance:

| Setting | Default | What it does |
|---|---|---|
| Keep the clock running | On | Everything in the first list above |
| Run extra simulation steps | Off | Up to twice the simulation steps per frame below 30 frames a second; costs frames |
| Limit the people simulated in detail | On | Everything in the second list above |
| Choose the limit automatically | On | The limit follows how your computer keeps up |
| Most people simulated in detail (thousands) | 50 | When the automatic limit is off: lower runs faster, higher puts more people on the streets |
| Show it in the population box | On | The count of people in detail beside the population at the bottom of the screen |
| Write a performance report | (button) | Measures for 3 minutes, then writes a short report to `BigCityPerformance.log`: see below |

Under **Right now** the page shows what the mod is doing as you play: people simulated in detail out of the whole
population, the game speed reached over the last few seconds (1 = normal), and how many people wait asleep at crowded
stops.

The settings are in English, German, Spanish, French, Italian, Japanese, Korean, Polish, Brazilian Portuguese, Russian
and Chinese (simplified and traditional).

## What to expect

- **Fewer people on the streets** of a city over the limit. That's the point: they're home.
- **Households take turns.** While asleep, a household doesn't age, change jobs or move. The ones asleep longest wake a
  few at a time and others go to sleep in their place, so every household gets its time awake about once every two game
  days, while the number awake stays at the limit.
- **Rush hour is gentler.** Same trips, spread over a few minutes.
- **Coverage maps update about once a minute** at normal speed. A new school shows its coverage a little later.
- **Job hunting slows down** while the route-finder is busy, and catches up when it isn't.

## Works with other mods

- **Realistic Trips** works. With it and the Stutter Fix, a million-person city ran about a tenth slower in testing.
- **Service Coverage Stutter Fix** works, and the two help each other: it makes each coverage update cheaper, this mod runs them less often. On the current game version it shows its own warning popup at start: click Continue.
- **Ultra Performance & Simulation Booster** overlaps with this mod. Turn off its Pathfind Queue Unchoker, and its Optimize Parking too: a bug there opens an error popup that pauses the game.
- Don't run Ultra Performance and the Stutter Fix together. They clash with each other and show an error popup at every start.

**MapExt: Big Map, Economy and Performance** should work (read from its code, not tested in the game). If you turn on its resource buyer option, sleepers' shopping and the slower coverage updates stop.

**FiscalOverhaul** works: its tax engine charges sleeping households residential tax too.

**MapExt** on a bigger map may show a CRITICAL CellMap conflict popup at start that lists every loaded mod, this one
included. It appears when another mod switches off the game's happiness system (FiscalOverhaul does, to run its own
copy of it), not because of this mod.

Other mods that change simulation speed or route-finding haven't been tested with it.

## Saves and removing the mod

Sleeping households are written to a save as ordinary households, so nobody has to wake up for it. Every save is an
ordinary save that loads without the mod. Removing the mod is safe at any time.

## How it works

- The game's route requests all go through one call; the mod scales their deadlines from how close the clock is to
  stopping, doubling while it keeps stopping and halving back after a calm spell.
- Sleeping uses the entity system's `Disabled` tag, which every game system skips. The game never uses that tag itself, so
  anything carrying it belongs to the mod, and waking everyone is one search. Tags change at the end of a frame, as the
  game's own changes do.
- The game's own population count, wage and residential tax systems are given copies of their queries that include
  sleepers, so the numbers are the game's own. Sleepers' shopping goes into the game's own sales queue as sales without a
  trip, so the game moves the money and the goods itself.
- The step limit and the coverage pace are small patches with a check: if a game update changes the code they rely on,
  they leave the game alone and say so in the log.
- Every five minutes of play the mod writes a status line to `Logs\BigCityPerformance.log`: game speed, frame rate, route
  requests waiting, how many people are asleep and what they bought.

## Found a bug?

Questions and suggestions are welcome on the [forum thread](https://forum.paradoxplaza.com/forum/threads/mod-big-city-performance-million-person-cities-at-full-speed-discussion-feedback.1949141/) or in [this thread](https://discord.com/channels/263634513861541888/1568997116595150939) on the official Cities: Skylines Discord; the **Discord** button in Options opens it too.

Click **Report a bug** in Options → Big City Performance, or [open an issue](https://github.com/TevBenji/Big-City-Performance/issues). Add a screenshot and the log at
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\BigCityPerformance.log`. The mod writes a status
line there every five minutes, so the log shows how the city was running.

**If your city runs slowly**, press **Write a performance report** in Options → Big City Performance with the city running
at normal speed and the camera where you usually play. After 3 minutes (the second half runs a little slower) the log gets
a short report: the city's size, its speed and frame rate, whether the processor or the graphics card sets the pace, and
the game's systems that take the most time, other mods' included. Paste it into Discord or the issue.

## Building from source

**Requirements:** Windows, Cities: Skylines II, the .NET SDK 9 or later, and Node.js 18 or later. The mod targets .NET
Framework 4.8 with C# 9 and compiles against the game's own assemblies.

```powershell
git clone https://github.com/TevBenji/Big-City-Performance.git
cd Big-City-Performance/src/BigCityPerformance.UI
npm ci
cd ../..
dotnet build BigCityPerformance.sln -c Release
```

Without `npm ci` the build skips the population box bundle with a warning.

The build writes the mod to `src/BigCityPerformance/bin/Release`. If the game is not installed under `D:\Steam`, point the
build at it: `-p:ManagedPath="<Steam library>\steamapps\common\Cities Skylines II\Cities2_Data\Managed"`. Building never
installs the mod. To install a build by hand, close the game and copy everything in `src/BigCityPerformance/bin/Release`
into `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\BigCityPerformance`.

| Folder | What's in it |
|---|---|
| `src/BigCityPerformance.Core` | The deadline and throttle rules and the commute marker: pure C#, tested outside the game |
| `src/BigCityPerformance` | The mod: the clock, the sleeping, the bookkeeping and the settings |
| `src/BigCityPerformance.UI` | The count in the population box: React and TypeScript, bundled for the game's UI |

## Credits

Big City Performance is made by [TevBenji](https://github.com/TevBenji). The project layout started from
**[Stepped Streets](https://github.com/TevBenji/Stepped-Streets)** and **Footbridge Builder**, the same author's mods, and
before that from **[Placemaker](https://github.com/jackwrichards/placemaker)** by jackwrichards. Runtime patching uses
**Harmony** by Andreas Pardeike (MIT). The full account is in [CREDITS.md](CREDITS.md).

## Licence

Released under the [GNU General Public License v3.0](LICENSE). Cities: Skylines II is a trademark of Paradox Interactive;
this is an independent mod, not made or endorsed by Paradox Interactive or Colossal Order.
