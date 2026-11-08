# Big City Performance

<a href="LICENSE"><img alt="Licence" src="https://img.shields.io/badge/licence-GPL--3.0-c2410c"></a>
<img alt="Game version" src="https://img.shields.io/badge/Cities%3A%20Skylines%20II-1.6-0e7490">

A Cities: Skylines II mod that keeps very big cities running at full speed.

**Not on Paradox Mods yet.**

A city of a million people in the base game runs at about a twentieth of normal speed: vehicles and cims crawl, and a game
day takes hours. With this mod the same city runs at real time or faster on the second speed button, on a four-core laptop.

<a href="#what-it-does">What it does</a> ·
<a href="#measured">Measured</a> ·
<a href="#settings">Settings</a> ·
<a href="#what-to-expect">What to expect</a> ·
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
- While it's behind, citizens look for new jobs, and empty taxis look for passengers, less often until it catches up.
- Rush hour is spread out. The game sends everyone who starts work at the same hour off in the same second; the mod gives
  each worker a few minutes' head start of their own, so they leave across a window instead. Nobody arrives later.
- Service coverage (how far each school, clinic or station reaches) is recalculated about once a minute instead of every
  few seconds. In a million-person city that recalculation was a quarter to a third of all route searches.
- Below 30 frames a second, the game lets the clock run up to twice as many simulation steps per frame, so normal speed
  stays normal speed.

**Limit the people simulated in detail** (on by default, 50,000):

- In a city bigger than the limit, that many people are simulated one by one. The rest stay at home, asleep. They keep
  their homes and their jobs, count in the population, earn their wages, pay rent and taxes, and still buy from the shops
  (as many sales as awake households make, spread over the shops people actually use). They just make no trips.
- Households go to sleep when everyone in them is home or on foot, so no bus or taxi waits for someone who isn't there.
- At overloaded stops, only the first hundred people at each place stay awake. The rest wait asleep and wake in turn as
  the ones in front board. A crowd of 1,900 at one stop is no longer 1,900 people stepping around each other and asking
  for new routes.
- Cities smaller than the limit are not changed at all.

## Measured

The "1 Million Population City" save (about 960,000 people), on a laptop with a four-core, eight-thread processor and an
RTX 3050 laptop graphics card:

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
| Limit the people simulated in detail | On | Everything in the second list above |
| Most people simulated in detail (thousands) | 50 | Lower runs faster; higher puts more people on the streets |

## What to expect

- **Fewer people on the streets** of a city over the limit. That's the point: they're home.
- **Sleepers stand still in time.** While asleep, a household doesn't age, change jobs or move. People take turns being
  awake only when the setting or a save wakes them; a later version will rotate who is awake.
- **Rush hour is gentler.** Same trips, spread over a few minutes.
- **Coverage maps update about once a minute** at normal speed. A new school shows its coverage a little later.
- **Job hunting slows down** while the route-finder is busy, and catches up when it isn't.

Other mods that change the same things (simulation speed, commute times, route-finding) haven't been tested alongside it.

## Saves and removing the mod

Everyone wakes up before the game saves, and the save waits until they have. Every save is an ordinary save that loads
without the mod. Removing the mod is safe at any time.

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

## Building from source

**Requirements:** Windows, Cities: Skylines II, and the .NET SDK 9 or later. The mod targets .NET Framework 4.8 with C# 9
and compiles against the game's own assemblies.

```powershell
git clone https://github.com/TevBenji/Big-City-Performance.git
cd Big-City-Performance
dotnet build BigCityPerformance.sln -c Release
```

The build writes the mod to `src/BigCityPerformance/bin/Release`. If the game is not installed under `D:\Steam`, point the
build at it: `-p:ManagedPath="<Steam library>\steamapps\common\Cities Skylines II\Cities2_Data\Managed"`. Building never
installs the mod. To install a build by hand, close the game and copy everything in `src/BigCityPerformance/bin/Release`
into `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\BigCityPerformance`.

| Folder | What's in it |
|---|---|
| `src/BigCityPerformance.Core` | The deadline and throttle rules and the commute marker: pure C#, tested outside the game |
| `src/BigCityPerformance` | The mod: the clock, the sleeping, the bookkeeping and the settings |

## Credits

Big City Performance is made by [TevBenji](https://github.com/TevBenji). The project layout started from
**[Stepped Streets](https://github.com/TevBenji/Stepped-Streets)** and **Footbridge Builder**, the same author's mods, and
before that from **[Placemaker](https://github.com/jackwrichards/placemaker)** by jackwrichards. Runtime patching uses
**Harmony** by Andreas Pardeike (MIT). The full account is in [CREDITS.md](CREDITS.md).

## Licence

Released under the [GNU General Public License v3.0](LICENSE). Cities: Skylines II is a trademark of Paradox Interactive;
this is an independent mod, not made or endorsed by Paradox Interactive or Colossal Order.
